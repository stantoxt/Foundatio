﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Elasticsearch.Net;
using Foundatio.Caching;
using Foundatio.Elasticsearch.Extensions;
using Foundatio.Elasticsearch.Repositories.Queries;
using Foundatio.Elasticsearch.Repositories.Query.Parts;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Repositories;
using Foundatio.Repositories.Models;
using Foundatio.Repositories.Queries;
using Nest;

namespace Foundatio.Elasticsearch.Repositories {
    public abstract class ElasticReadOnlyRepositoryBase<T> : IElasticReadOnlyRepository<T> where T : class, new() {
        protected internal readonly string EntityType = typeof(T).Name;
        protected internal readonly bool SupportsSoftDeletes = typeof(ISupportSoftDeletes).IsAssignableFrom(typeof(T));
        protected internal readonly bool HasIdentity = typeof(IIdentity).IsAssignableFrom(typeof(T));
        protected internal readonly ElasticRepositoryContext<T> Context;
        private ScopedCacheClient _scopedCacheClient;

        protected ElasticReadOnlyRepositoryBase(ElasticRepositoryContext<T> context) {
            Context = context;
        }

        protected Task<FindResults<T>> FindAsync(object query) {
            return FindAsAsync<T>(query);
        }

        protected async Task<FindResults<TResult>> FindAsAsync<TResult>(object query) where TResult : class, new() {
            if (query == null)
                throw new ArgumentNullException(nameof(query));

            var pagableQuery = query as IPagableQuery;
            // don't use caching with snapshot paging.
            bool allowCaching = IsCacheEnabled && (pagableQuery == null || pagableQuery.UseSnapshotPaging == false);

            Func<FindResults<TResult>, Task<FindResults<TResult>>> getNextPageFunc = async r => {
                if (!String.IsNullOrEmpty(r.ScrollId)) {
                    var scrollResponse = await Context.ElasticClient.ScrollAsync<TResult>("2m", r.ScrollId).AnyContext();
                    return new FindResults<TResult> {
                        Documents = scrollResponse.Documents.ToList(),
                        Total = r.Total,
                        ScrollId = r.ScrollId
                    };
                }

                if (pagableQuery == null)
                    return new FindResults<TResult>();

                pagableQuery.Page = pagableQuery.Page == null ? 2 : pagableQuery.Page + 1;
                return await FindAsAsync<TResult>(query).AnyContext();
            };

            string cacheSuffix = pagableQuery?.ShouldUseLimit() == true ? pagableQuery.Page?.ToString() ?? "1" : String.Empty;

            FindResults<TResult> result;
            if (allowCaching) {
                result = await GetCachedQueryResultAsync<FindResults<TResult>>(query, cacheSuffix: cacheSuffix).AnyContext();
                if (result != null) {
                    result.GetNextPageFunc = getNextPageFunc;
                    return result;
                }
            }

            var searchDescriptor = ConfigureSearchDescriptor(null, query);
            if (pagableQuery?.UseSnapshotPaging == true)
                searchDescriptor.SearchType(SearchType.Scan).Scroll("2m");

            var response = await Context.ElasticClient.SearchAsync<TResult>(searchDescriptor).AnyContext();
            if (!response.IsValid)
                throw new ApplicationException($"Elasticsearch error code \"{response.ConnectionStatus.HttpStatusCode}\".", response.ConnectionStatus.OriginalException);

            if (pagableQuery?.UseSnapshotPaging == true) {
                var scanResponse = response;
                response = await Context.ElasticClient.ScrollAsync<TResult>("2m", response.ScrollId).AnyContext();
                if (!response.IsValid)
                    throw new ApplicationException($"Elasticsearch error code \"{response.ConnectionStatus.HttpStatusCode}\".", response.ConnectionStatus.OriginalException);

                result = new FindResults<TResult> {
                    Documents = response.Documents.ToList(),
                    Total = scanResponse.Total,
                    ScrollId = scanResponse.ScrollId,
                    GetNextPageFunc = getNextPageFunc
                };
            } else if (pagableQuery?.ShouldUseLimit() == true) {
                result = new FindResults<TResult> {
                    Documents = response.Documents.Take(pagableQuery.GetLimit()).ToList(),
                    Total = response.Total,
                    HasMore = pagableQuery.ShouldUseLimit() && response.Documents.Count() > pagableQuery.GetLimit(),
                    GetNextPageFunc = getNextPageFunc
                };
            } else {
                result = new FindResults<TResult> {
                    Documents = response.Documents.ToList(),
                    Total = response.Total
                };
            }

            result.Facets = response.ToFacetResults();

            if (allowCaching) {
                var nextPageFunc = result.GetNextPageFunc;
                result.GetNextPageFunc = null;
                await SetCachedQueryResultAsync(query, result, cacheSuffix: cacheSuffix).AnyContext();
                result.GetNextPageFunc = nextPageFunc;
            }

            return result;
        }

        protected async Task<T> FindOneAsync(object query) {
            if (query == null)
                throw new ArgumentNullException(nameof(query));
            
            var result = IsCacheEnabled ? await GetCachedQueryResultAsync<T>(query).AnyContext() : null;
            if (result != null)
                return result;

            var searchDescriptor = CreateSearchDescriptor(query).Size(1);
            result = (await Context.ElasticClient.SearchAsync<T>(searchDescriptor).AnyContext()).Documents.FirstOrDefault();

            if (IsCacheEnabled)
                await SetCachedQueryResultAsync(query, result).AnyContext();

            return result;
        }

        public async Task<bool> ExistsAsync(string id) {
            if (String.IsNullOrEmpty(id))
                return false;

            return await ExistsAsync(new Queries.Query().WithId(id)).AnyContext();
        }

        protected async Task<bool> ExistsAsync(object query) {
            if (query == null)
                throw new ArgumentNullException(nameof(query));

            var searchDescriptor = CreateSearchDescriptor(query).Size(1);
            searchDescriptor.Fields("id");

            return (await Context.ElasticClient.SearchAsync<T>(searchDescriptor).AnyContext()).HitsMetaData.Total > 0;
        }

        protected async Task<long> CountAsync(object query) {
            if (query == null)
                throw new ArgumentNullException(nameof(query));

            var result = IsCacheEnabled ? await GetCachedQueryResultAsync<long?>(query, "count-").AnyContext() : null;
            if (result != null)
                return result.Value;

            var countDescriptor = new CountDescriptor<T>().Query(GetElasticQuery(query));
            var indices = GetIndexesByQuery(query);
            if (indices?.Length > 0)
                countDescriptor.Indices(indices);
            countDescriptor.IgnoreUnavailable();
            countDescriptor.Type(GetTypeName());

            var results = await Context.ElasticClient.CountAsync<T>(countDescriptor).AnyContext();
            if (!results.IsValid)
                throw new ApplicationException($"ElasticSearch error code \"{results.ConnectionStatus.HttpStatusCode}\".", results.ConnectionStatus.OriginalException);

            if (IsCacheEnabled)
                await SetCachedQueryResultAsync(query, results.Count, "count-").AnyContext();

            return results.Count;
        }

        public async Task<long> CountAsync() {
            return (await Context.ElasticClient.CountAsync<T>(c => c.Query(q => q.MatchAll()).Indices(GetIndexesByQuery(null))).AnyContext()).Count;
        }

        public async Task<T> GetByIdAsync(string id, bool useCache = false, TimeSpan? expiresIn = null) {
            if (String.IsNullOrEmpty(id))
                return null;

            T result = null;
            if (IsCacheEnabled && useCache)
                result = await Cache.GetAsync<T>(id, null).AnyContext();

            if (result != null)
                return result;

            if (GetParentIdFunc == null) // we don't have the parent id
                result = (await Context.ElasticClient.GetAsync<T>(id, GetIndexById(id)).AnyContext()).Source;
            else
                result = await FindOneAsync(new ElasticQuery().WithId(id)).AnyContext();
            
            if (IsCacheEnabled && result != null && useCache)
                await Cache.SetAsync(id, result, expiresIn ?? TimeSpan.FromSeconds(RepositoryConstants.DEFAULT_CACHE_EXPIRATION_SECONDS)).AnyContext();

            return result;
        }

        public async Task<FindResults<T>> GetByIdsAsync(ICollection<string> ids, bool useCache = false, TimeSpan? expiresIn = null) {
            var results = new FindResults<T>();
            if (ids == null || ids.Count == 0)
                return results;

            if (!HasIdentity)
                throw new NotSupportedException("Model type must implement IIdentity.");

            if (IsCacheEnabled && useCache) {
                var cacheHits = await Cache.GetAllAsync<T>(ids.Distinct()).AnyContext();
                results.Documents.AddRange(cacheHits.Where(kvp => kvp.Value.HasValue).Select(kvp => kvp.Value.Value));
                results.Total = results.Documents.Count;

                var notCachedIds = ids.Except(results.Documents.Select(i => ((IIdentity)i).Id)).ToArray();
                if (notCachedIds.Length == 0)
                    return results;
            }

            var itemsToFind = new List<string>(ids.Except(results.Documents.Select(i => ((IIdentity)i).Id)));
            var multiGet = new MultiGetDescriptor();

            if (GetParentIdFunc == null) {
                foreach (var id in itemsToFind)
                    multiGet.Get<T>(f => f.Id(id).Index(GetIndexById(id)));

                foreach (var doc in (await Context.ElasticClient.MultiGetAsync(multiGet).AnyContext()).Documents.Where(doc => doc.Found)) {
                    results.Documents.Add(doc.Source as T);
                    itemsToFind.Remove(doc.Id);
                }
            }

            // fallback to doing a find
            if (itemsToFind.Count > 0)
                results.Documents.AddRange((await FindAsync(new ElasticQuery().WithIds(itemsToFind)).AnyContext()).Documents);

            if (IsCacheEnabled && useCache) {
                foreach (var item in results.Documents)
                    await Cache.SetAsync(((IIdentity)item).Id, item, expiresIn.HasValue ? DateTime.UtcNow.Add(expiresIn.Value) : DateTime.UtcNow.AddSeconds(RepositoryConstants.DEFAULT_CACHE_EXPIRATION_SECONDS)).AnyContext();
            }

            results.Total = results.Documents.Count;
            return results;
        }

        public Task<FindResults<T>> GetAllAsync(SortingOptions sorting = null, PagingOptions paging = null) {
            var search = new ElasticQuery()
                .WithPaging(paging)
                .WithSort(sorting);

            return FindAsync(search);
        }

        public async Task<ICollection<FacetResult>> GetFacetsAsync(object query) {
            var facetQuery = query as IFacetQuery;

            if (facetQuery == null || facetQuery.FacetFields.Count == 0)
                throw new ArgumentException("Query must contain facet fields.", nameof(query));

            if (AllowedFacetFields.Length > 0 && !facetQuery.FacetFields.All(f => AllowedFacetFields.Contains(f.Field)))
                throw new ArgumentException("All facet fields must be allowed.", nameof(query));

            var search = CreateSearchDescriptor(query).SearchType(SearchType.Count);
            var res = await Context.ElasticClient.SearchAsync<T>(search);
            if (!res.IsValid) {
                Logger.Error().Message("Retrieving term stats failed: {0}", res.ServerError.Error).Write();
                throw new ApplicationException("Retrieving term stats failed.");
            }

            return res.ToFacetResults();
        }

        public Task<FindResults<T>> GetBySearchAsync(string systemFilter, string userFilter = null, string query = null, SortingOptions sorting = null, PagingOptions paging = null, FacetOptions facets = null) {
            var search = new ElasticQuery()
                .WithSystemFilter(systemFilter)
                .WithFilter(userFilter)
                .WithSearchQuery(query, false)
                .WithFacets(facets)
                .WithSort(sorting)
                .WithPaging(paging);

            return FindAsync(search);
        }

        public Task<ICollection<FacetResult>> GetFacetsAsync(string systemFilter, FacetOptions facets, string userFilter = null, string query = null) {
            var search = new ElasticQuery()
                .WithSystemFilter(systemFilter)
                .WithFilter(userFilter)
                .WithSearchQuery(query, false)
                .WithFacets(facets);

            return GetFacetsAsync(search);
        }

        protected void DisableCache() {
            IsCacheEnabled = false;
            _scopedCacheClient = new ScopedCacheClient(new NullCacheClient(), GetTypeName());
        }

        public bool IsCacheEnabled { get; private set; } = true;

        protected internal ScopedCacheClient Cache {
            get {
                if (_scopedCacheClient == null) {
                    IsCacheEnabled = Context.Cache != null;
                    _scopedCacheClient = new ScopedCacheClient(Context.Cache, GetTypeName());
                }

                return _scopedCacheClient;
            }
        }

        protected internal virtual string[] AllowedFacetFields => new string[] { };
        protected internal virtual string[] AllowedSortFields => new string[] { };
        protected virtual string GetTypeName() => EntityType;
        protected internal virtual string[] DefaultExcludes => new string[] { };
        protected internal virtual Func<T, string> GetParentIdFunc { get; set; }
        protected internal virtual Func<T, string> GetDocumentIndexFunc { get { return d => null; } }

        protected virtual string[] GetIndexesByQuery(object query) {
            var withIndicesQuery = query as IElasticIndicesQuery;
            return withIndicesQuery?.Indices.ToArray();
        }

        protected internal virtual string GetIndexById(string id) => null;
        
        protected virtual async Task InvalidateCacheAsync(ICollection<ModifiedDocument<T>> documents) {
            if (!IsCacheEnabled)
                return;

            if (documents != null && documents.Count > 0 && HasIdentity) {
                var keys = documents
                    .Select(d => d.Value)
                    .Cast<IIdentity>()
                    .Select(d => d.Id)
                    .ToList();

                if (keys.Count > 0)
                    await Cache.RemoveAllAsync(keys).AnyContext();
            }
        }
        
        public Task InvalidateCacheAsync(T document) {
            return InvalidateCacheAsync(new[] { document });
        }

        public Task InvalidateCacheAsync(ICollection<T> documents) {
            return InvalidateCacheAsync(documents.Select(d => new ModifiedDocument<T>(d, null)).ToList());
        }

        protected SearchDescriptor<T> CreateSearchDescriptor(object query) {
            return ConfigureSearchDescriptor(new SearchDescriptor<T>(), query);
        }

        protected SearchDescriptor<T> ConfigureSearchDescriptor(SearchDescriptor<T> search, object query) {
            if (search == null)
                search = new SearchDescriptor<T>();

            var builders = GetBuilders();
            search.Query(GetElasticQuery(query));

            var indices = GetIndexesByQuery(query);
            if (indices?.Length > 0)
                search.Indices(indices);
            search.IgnoreUnavailable();
            search.Type(GetTypeName());

            foreach (var builder in builders)
                builder.BuildSearch(this, search, query);

            return search;
        }

        protected internal QueryContainer GetElasticQuery(object query) {
            QueryContainer container = new MatchAllQuery();
            container &= new FilteredQuery { Filter = ApplyFilter(query, null) };
            
            foreach (var builder in GetBuilders())
                builder.BuildQuery(this, container, query);
            
            return container;
        }

        private FilterContainer ApplyFilter(object query, FilterContainer container) {
            if (container == null)
                container = new MatchAllFilter();
            
            foreach (var builder in GetBuilders())
                builder.BuildFilter(this, container, query);
            
            return container;
        }

        private ICollection<IQueryBuilder> GetBuilders() {
            var builders = new List<IQueryBuilder>();
            RegisterBuilders(builders);

            return builders;
        }
         
        protected virtual void RegisterBuilders(ICollection<IQueryBuilder> builders) {
            builders.Add(new PagableQueryBuilder());
            builders.Add(new SelectedFieldsQueryBuilder());
            builders.Add(new SortableQueryBuilder());
            builders.Add(new FacetQueryBuilder());
            builders.Add(new ParentQueryBuilder());
            builders.Add(new ChildQueryBuilder());
            builders.Add(new IdentityQueryBuilder());
            builders.Add(new SoftDeletesQueryBuilder());
            builders.Add(new DateRangeQueryBuilder());
            builders.Add(new SearchQueryBuilder());
            builders.Add(new ElasticFilterQueryBuilder());
            builders.Add(new FieldConditionsQueryBuilder());
        }

        protected async Task<TResult> GetCachedQueryResultAsync<TResult>(object query, string cachePrefix = null, string cacheSuffix = null) {
            var cachedQuery = query as ICachableQuery;
            if (!IsCacheEnabled || cachedQuery == null || !cachedQuery.ShouldUseCache())
                return default(TResult);

            string cacheKey = cachePrefix != null ? cachePrefix + ":" + cachedQuery.CacheKey : cachedQuery.CacheKey;
            cacheKey = cacheSuffix != null ? cacheKey + ":" + cacheSuffix : cacheKey;
            var result = await Cache.GetAsync<TResult>(cacheKey, default(TResult)).AnyContext();
            Logger.Trace().Message("Cache {0}: type={1}", result != null ? "hit" : "miss", GetTypeName()).Write();

            return result;
        }

        protected async Task SetCachedQueryResultAsync<TResult>(object query, TResult result, string cachePrefix = null, string cacheSuffix = null) {
            var cachedQuery = query as ICachableQuery;
            if (!IsCacheEnabled || result == null || cachedQuery == null || !cachedQuery.ShouldUseCache())
                return;

            string cacheKey = cachePrefix != null ? cachePrefix + ":" + cachedQuery.CacheKey : cachedQuery.CacheKey;
            cacheKey = cacheSuffix != null ? cacheKey + ":" + cacheSuffix : cacheKey;
            await Cache.SetAsync(cacheKey, result, cachedQuery.GetCacheExpirationDateUtc()).AnyContext();
        }
    }
}
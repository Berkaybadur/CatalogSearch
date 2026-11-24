using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Aggregations;
using Elastic.Clients.Elasticsearch.Core.Search;
using Elastic.Clients.Elasticsearch.QueryDsl;
using MassTransit;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;

namespace Catalog.Api;

/// <summary>MongoDB is the source of truth for products.</summary>
public sealed class MongoProductStore : IProductStore
{
    private readonly IMongoCollection<Product> _products;

    static MongoProductStore()
    {
        BsonSerializer.TryRegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
        BsonSerializer.TryRegisterSerializer(new DecimalSerializer(BsonType.Decimal128));
    }

    public MongoProductStore(IMongoDatabase database) => _products = database.GetCollection<Product>("products");

    public async Task<Product?> GetAsync(Guid id, CancellationToken ct = default) =>
        await _products.Find(p => p.Id == id).FirstOrDefaultAsync(ct);

    public Task UpsertAsync(Product product, CancellationToken ct = default) =>
        _products.ReplaceOneAsync(p => p.Id == product.Id, product, new ReplaceOptions { IsUpsert = true }, ct);
}

/// <summary>
/// Faceted full-text search in Elasticsearch. Relies on dynamic mapping: string fields get a
/// <c>.keyword</c> sub-field that is used for filters and facets.
/// </summary>
public sealed class ElasticSearchIndex(ElasticsearchClient client) : ISearchIndex
{
    private const string IndexName = "products";

    public async Task IndexAsync(Product product, CancellationToken ct = default)
    {
        var response = await client.IndexAsync(product, i => i.Index(IndexName).Id(product.Id), ct);
        if (!response.IsValidResponse) throw new InvalidOperationException($"Indexing failed: {response.DebugInformation}");
    }

    public async Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var must = new List<Query>();
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            must.Add(new MultiMatchQuery
            {
                Query = query.Text,
                Fields = new[] { "name^3", "sku^3", "brand^2", "category^1.5", "description" },
                Operator = Operator.And,
                Fuzziness = new Fuzziness("AUTO")
            });
        }

        var filter = new List<Query>();
        if (query.Category is not null) filter.Add(new TermQuery { Field = "category.keyword", Value = query.Category });
        if (query.Brands.Count > 0) filter.Add(new TermsQuery { Field = "brand.keyword", Terms = new TermsQueryField(query.Brands.Select(FieldValue.String).ToArray()) });
        if (query.MinPrice is not null || query.MaxPrice is not null)
        {
            filter.Add(new NumberRangeQuery("price") { Gte = (double?)query.MinPrice, Lte = (double?)query.MaxPrice });
        }
        if (query.InStockOnly) filter.Add(new NumberRangeQuery("stock") { Gt = 0 });

        var response = await client.SearchAsync<Product>(new SearchRequest(IndexName)
        {
            From = (query.Page - 1) * query.PageSize,
            Size = query.PageSize,
            TrackTotalHits = new TrackHits(true),
            Query = new BoolQuery { Must = must, Filter = filter },
            Sort = query.Sort switch
            {
                ProductSort.PriceAsc => [By("price", SortOrder.Asc)],
                ProductSort.PriceDesc => [By("price", SortOrder.Desc)],
                ProductSort.Rating => [By("rating", SortOrder.Desc)],
                ProductSort.Newest => [By("updatedAt", SortOrder.Desc)],
                _ => null
            },
            Aggregations = new Dictionary<string, Aggregation>
            {
                ["categories"] = new TermsAggregation { Field = "category.keyword", Size = 20 },
                ["brands"] = new TermsAggregation { Field = "brand.keyword", Size = 20 },
                ["minPrice"] = new MinAggregation { Field = "price" },
                ["maxPrice"] = new MaxAggregation { Field = "price" }
            }
        }, ct);

        if (!response.IsValidResponse) throw new InvalidOperationException($"Search failed: {response.DebugInformation}");

        var aggregations = response.Aggregations!;
        return new SearchResult(
            response.Total, query.Page, query.PageSize, response.Documents.ToList(),
            Buckets(aggregations, "categories"), Buckets(aggregations, "brands"),
            Value(aggregations.GetMin("minPrice")?.Value), Value(aggregations.GetMax("maxPrice")?.Value));
    }

    public async Task<IReadOnlyList<string>> SuggestAsync(string prefix, int size, CancellationToken ct = default)
    {
        var response = await client.SearchAsync<Product>(new SearchRequest(IndexName)
        {
            Size = size,
            Source = new SourceConfig(new SourceFilter { Includes = new[] { "name" } }),
            Query = new MatchPhrasePrefixQuery { Field = "name", Query = prefix }
        }, ct);

        if (!response.IsValidResponse) throw new InvalidOperationException($"Suggest failed: {response.DebugInformation}");
        return response.Documents.Select(p => p.Name).Distinct().ToList();
    }

    private static SortOptions By(string field, SortOrder order) =>
        new() { Field = new FieldSort { Field = field, Order = order } };

    private static List<FacetValue> Buckets(AggregateDictionary aggregations, string name) =>
        aggregations.GetStringTerms(name)?.Buckets
            .Select(b => new FacetValue(b.Key.ToString() ?? string.Empty, (int)b.DocCount)).ToList() ?? [];

    private static decimal? Value(double? value) => value is null or double.NaN or double.PositiveInfinity or double.NegativeInfinity ? null : (decimal)value;
}

/// <summary>RabbitMQ (via MassTransit) consumer that keeps the search index in sync.</summary>
public sealed class ProductChangedConsumer(ProductChangedHandler handler) : IConsumer<ProductChanged>
{
    public Task Consume(ConsumeContext<ProductChanged> context) => handler.HandleAsync(context.Message, context.CancellationToken);
}

public sealed class MassTransitPublisher(IPublishEndpoint endpoint) : IEventPublisher
{
    public Task PublishAsync(ProductChanged message, CancellationToken ct = default) => endpoint.Publish(message, ct);
}

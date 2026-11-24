using Elastic.Clients.Elasticsearch;
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

/// <summary>Full-text search over products in Elasticsearch.</summary>
public sealed class ElasticSearchIndex(ElasticsearchClient client) : ISearchIndex
{
    private const string IndexName = "products";

    public async Task IndexAsync(Product product, CancellationToken ct = default)
    {
        var response = await client.IndexAsync(product, i => i.Index(IndexName).Id(product.Id), ct);
        if (!response.IsValidResponse) throw new InvalidOperationException($"Indexing failed: {response.DebugInformation}");
    }

    public async Task<IReadOnlyList<Product>> SearchAsync(string text, string? category, CancellationToken ct = default)
    {
        var response = await client.SearchAsync<Product>(s => s
            .Indices(IndexName)
            .Query(q => q.Bool(b =>
            {
                b.Must(m => m.MultiMatch(mm => mm.Query(text).Fields(new[] { "name^2", "description" })));
                if (category is not null) b.Filter(f => f.Match(m => m.Field(p => p.Category).Query(category)));
            })), ct);

        if (!response.IsValidResponse) throw new InvalidOperationException($"Search failed: {response.DebugInformation}");
        return response.Documents.ToList();
    }
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

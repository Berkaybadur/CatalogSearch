using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace Catalog.Api;

public sealed record Product(
    Guid Id, string Sku, string Name, string Description, string Brand, string Category,
    decimal Price, int Stock, double Rating, DateTimeOffset UpdatedAt)
{
    public bool InStock => Stock > 0;
}

public sealed record ProductRequest(
    string Sku, string Name, string? Description, string Brand, string Category,
    decimal Price, int Stock, double Rating);

public sealed record StockRequest(int Stock);

/// <summary>Message published whenever a product is created or changed.</summary>
public sealed record ProductChanged(Guid ProductId);

public enum ProductSort { Relevance, PriceAsc, PriceDesc, Rating, Newest }

public sealed record SearchQuery(
    string? Text, string? Category, IReadOnlyList<string> Brands, decimal? MinPrice, decimal? MaxPrice,
    bool InStockOnly, ProductSort Sort, int Page, int PageSize);

public sealed record FacetValue(string Value, int Count);

/// <summary>A page of results plus the facets (category, brand, price range) of the whole matching set.</summary>
public sealed record SearchResult(
    long Total, int Page, int PageSize, IReadOnlyList<Product> Items,
    IReadOnlyList<FacetValue> Categories, IReadOnlyList<FacetValue> Brands, decimal? MinPrice, decimal? MaxPrice);

// ---- Ports: each one has an in-memory implementation and a real adapter ----

public interface IProductStore
{
    Task<Product?> GetAsync(Guid id, CancellationToken ct = default);
    Task UpsertAsync(Product product, CancellationToken ct = default);
}

public interface ISearchIndex
{
    Task IndexAsync(Product product, CancellationToken ct = default);
    Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<string>> SuggestAsync(string prefix, int size, CancellationToken ct = default);
}

public interface IEventPublisher
{
    Task PublishAsync(ProductChanged message, CancellationToken ct = default);
}

/// <summary>Consumes <see cref="ProductChanged"/> and refreshes the search index. Re-indexing is idempotent, so redelivery is safe.</summary>
public sealed class ProductChangedHandler(IProductStore store, ISearchIndex index)
{
    public async Task HandleAsync(ProductChanged message, CancellationToken ct = default)
    {
        var product = await store.GetAsync(message.ProductId, ct);
        if (product is not null) await index.IndexAsync(product, ct);
    }
}

/// <summary>
/// Write path: source of truth -> invalidate cache -> publish event (the index is updated asynchronously).
/// Read path: cache-aside in front of the store.
/// </summary>
public sealed class ProductService(IProductStore store, IDistributedCache cache, IEventPublisher publisher)
{
    private static readonly DistributedCacheEntryOptions CacheOptions = new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) };

    private static string CacheKey(Guid id) => $"product:{id}";

    public async Task<Product> SaveAsync(Guid id, ProductRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Sku);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Brand);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Category);
        ArgumentOutOfRangeException.ThrowIfNegative(request.Price);
        ArgumentOutOfRangeException.ThrowIfNegative(request.Stock);
        if (request.Rating is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(request.Rating), "Rating must be between 0 and 5.");

        var product = new Product(id, request.Sku.Trim().ToUpperInvariant(), request.Name.Trim(), request.Description ?? string.Empty,
            request.Brand.Trim(), request.Category.Trim(), request.Price, request.Stock, request.Rating, DateTimeOffset.UtcNow);
        await PersistAsync(product, ct);
        return product;
    }

    /// <summary>Stock changes are frequent, so they have their own cheap command; they still flow to the index.</summary>
    public async Task<Product?> UpdateStockAsync(Guid id, int stock, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(stock);
        var product = await store.GetAsync(id, ct);
        if (product is null) return null;

        product = product with { Stock = stock, UpdatedAt = DateTimeOffset.UtcNow };
        await PersistAsync(product, ct);
        return product;
    }

    public async Task<Product?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var cached = await cache.GetStringAsync(CacheKey(id), ct);
        if (cached is not null) return JsonSerializer.Deserialize<Product>(cached);

        var product = await store.GetAsync(id, ct);
        if (product is not null) await cache.SetStringAsync(CacheKey(id), JsonSerializer.Serialize(product), CacheOptions, ct);
        return product;
    }

    private async Task PersistAsync(Product product, CancellationToken ct)
    {
        await store.UpsertAsync(product, ct);
        await cache.RemoveAsync(CacheKey(product.Id), ct);
        await publisher.PublishAsync(new ProductChanged(product.Id), ct);
    }
}

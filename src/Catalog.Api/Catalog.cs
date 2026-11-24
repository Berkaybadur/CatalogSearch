using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace Catalog.Api;

public sealed record Product(Guid Id, string Name, string Description, string Category, decimal Price, DateTimeOffset UpdatedAt);

public sealed record ProductRequest(string Name, string Description, string Category, decimal Price);

/// <summary>Message published whenever a product is created or changed.</summary>
public sealed record ProductChanged(Guid ProductId);

// ---- Ports: each one has an in-memory implementation and a real adapter ----

public interface IProductStore
{
    Task<Product?> GetAsync(Guid id, CancellationToken ct = default);
    Task UpsertAsync(Product product, CancellationToken ct = default);
}

public interface ISearchIndex
{
    Task IndexAsync(Product product, CancellationToken ct = default);
    Task<IReadOnlyList<Product>> SearchAsync(string text, string? category, CancellationToken ct = default);
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
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Category);
        ArgumentOutOfRangeException.ThrowIfNegative(request.Price);

        var product = new Product(id, request.Name.Trim(), request.Description ?? string.Empty, request.Category.Trim(), request.Price, DateTimeOffset.UtcNow);
        await store.UpsertAsync(product, ct);
        await cache.RemoveAsync(CacheKey(id), ct);
        await publisher.PublishAsync(new ProductChanged(id), ct);
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
}

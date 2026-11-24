using System.Collections.Concurrent;

namespace Catalog.Api;

public sealed class InMemoryProductStore : IProductStore
{
    private readonly ConcurrentDictionary<Guid, Product> _products = new();

    public Task<Product?> GetAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_products.GetValueOrDefault(id));

    public Task UpsertAsync(Product product, CancellationToken ct = default)
    {
        _products[product.Id] = product;
        return Task.CompletedTask;
    }
}

public sealed class InMemorySearchIndex : ISearchIndex
{
    private readonly ConcurrentDictionary<Guid, Product> _documents = new();

    public Task IndexAsync(Product product, CancellationToken ct = default)
    {
        _documents[product.Id] = product;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Product>> SearchAsync(string text, string? category, CancellationToken ct = default)
    {
        IReadOnlyList<Product> result = _documents.Values
            .Where(p => category is null || string.Equals(p.Category, category, StringComparison.OrdinalIgnoreCase))
            .Where(p => p.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || p.Description.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name)
            .ToList();
        return Task.FromResult(result);
    }
}

/// <summary>Delivers the message in-process, used when no RabbitMQ is configured.</summary>
public sealed class InlinePublisher(ProductChangedHandler handler) : IEventPublisher
{
    public Task PublishAsync(ProductChanged message, CancellationToken ct = default) => handler.HandleAsync(message, ct);
}

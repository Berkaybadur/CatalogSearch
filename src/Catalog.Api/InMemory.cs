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

/// <summary>Reference implementation of the search semantics, mirrored by the Elasticsearch adapter.</summary>
public sealed class InMemorySearchIndex : ISearchIndex
{
    private readonly ConcurrentDictionary<Guid, Product> _documents = new();

    public Task IndexAsync(Product product, CancellationToken ct = default)
    {
        _documents[product.Id] = product;
        return Task.CompletedTask;
    }

    public Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var terms = (query.Text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Every term must match somewhere; name matches weigh most, then brand, then description.
        var scored = _documents.Values
            .Where(p => query.Category is null || string.Equals(p.Category, query.Category, StringComparison.OrdinalIgnoreCase))
            .Where(p => query.Brands.Count == 0 || query.Brands.Contains(p.Brand, StringComparer.OrdinalIgnoreCase))
            .Where(p => query.MinPrice is null || p.Price >= query.MinPrice)
            .Where(p => query.MaxPrice is null || p.Price <= query.MaxPrice)
            .Where(p => !query.InStockOnly || p.InStock)
            .Select(p => (Product: p, Score: Score(p, terms)))
            .Where(x => terms.Length == 0 || x.Score > 0)
            .ToList();

        var ordered = query.Sort switch
        {
            ProductSort.PriceAsc => scored.OrderBy(x => x.Product.Price),
            ProductSort.PriceDesc => scored.OrderByDescending(x => x.Product.Price),
            ProductSort.Rating => scored.OrderByDescending(x => x.Product.Rating).ThenBy(x => x.Product.Name),
            ProductSort.Newest => scored.OrderByDescending(x => x.Product.UpdatedAt),
            _ => scored.OrderByDescending(x => x.Score).ThenBy(x => x.Product.Name)
        };

        var items = ordered.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).Select(x => x.Product).ToList();
        var all = scored.Select(x => x.Product).ToList();

        return Task.FromResult(new SearchResult(
            all.Count, query.Page, query.PageSize, items,
            Facet(all, p => p.Category), Facet(all, p => p.Brand),
            all.Count == 0 ? null : all.Min(p => p.Price), all.Count == 0 ? null : all.Max(p => p.Price)));
    }

    public Task<IReadOnlyList<string>> SuggestAsync(string prefix, int size, CancellationToken ct = default)
    {
        IReadOnlyList<string> names = _documents.Values
            .Select(p => p.Name)
            .Where(n => n.Split(' ').Any(w => w.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .Distinct().OrderBy(n => n).Take(size).ToList();
        return Task.FromResult(names);
    }

    private static double Score(Product p, string[] terms)
    {
        double score = 0;
        foreach (var term in terms)
        {
            var termScore = (Contains(p.Name, term) ? 3 : 0) + (Contains(p.Brand, term) ? 2 : 0)
                + (Contains(p.Category, term) ? 1.5 : 0) + (Contains(p.Description, term) ? 1 : 0) + (Contains(p.Sku, term) ? 3 : 0);
            if (termScore == 0) return 0;
            score += termScore;
        }

        return score;
    }

    private static bool Contains(string source, string term) => source.Contains(term, StringComparison.OrdinalIgnoreCase);

    private static List<FacetValue> Facet(List<Product> products, Func<Product, string> selector) =>
        products.GroupBy(selector, StringComparer.OrdinalIgnoreCase)
            .Select(g => new FacetValue(g.Key, g.Count()))
            .OrderByDescending(f => f.Count).ThenBy(f => f.Value).ToList();
}

/// <summary>Delivers the message in-process, used when no RabbitMQ is configured.</summary>
public sealed class InlinePublisher(ProductChangedHandler handler) : IEventPublisher
{
    public Task PublishAsync(ProductChanged message, CancellationToken ct = default) => handler.HandleAsync(message, ct);
}

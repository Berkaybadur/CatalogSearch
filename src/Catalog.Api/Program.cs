using Catalog.Api;
using Elastic.Clients.Elasticsearch;
using MassTransit;
using MongoDB.Driver;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.AddOpenApi();

// Every backing service is optional: when its connection string is absent an in-memory
// implementation is used, so the API runs (and is tested) without any infrastructure.
var mongo = config.GetConnectionString("MongoDb");
if (!string.IsNullOrEmpty(mongo))
{
    builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(mongo));
    builder.Services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase("catalog"));
    builder.Services.AddSingleton<IProductStore, MongoProductStore>();
}
else builder.Services.AddSingleton<IProductStore, InMemoryProductStore>();

var elastic = config.GetConnectionString("Elasticsearch");
if (!string.IsNullOrEmpty(elastic))
{
    builder.Services.AddSingleton(new ElasticsearchClient(new Uri(elastic)));
    builder.Services.AddSingleton<ISearchIndex, ElasticSearchIndex>();
}
else builder.Services.AddSingleton<ISearchIndex, InMemorySearchIndex>();

var redis = config.GetConnectionString("Redis");
if (!string.IsNullOrEmpty(redis)) builder.Services.AddStackExchangeRedisCache(o => o.Configuration = redis);
else builder.Services.AddDistributedMemoryCache();

builder.Services.AddScoped<ProductChangedHandler>();
var rabbit = config.GetConnectionString("RabbitMq");
if (!string.IsNullOrEmpty(rabbit))
{
    builder.Services.AddMassTransit(x =>
    {
        x.AddConsumer<ProductChangedConsumer>();
        x.UsingRabbitMq((context, cfg) =>
        {
            cfg.Host(new Uri(rabbit));
            cfg.UseMessageRetry(r => r.Exponential(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2)));
            cfg.ConfigureEndpoints(context);
        });
    });
    builder.Services.AddScoped<IEventPublisher, MassTransitPublisher>();
}
else builder.Services.AddScoped<IEventPublisher, InlinePublisher>();

builder.Services.AddScoped<ProductService>();
if (config.GetValue<bool>("Catalog:Seed")) builder.Services.AddHostedService<SeedService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(); // API reference UI at /scalar
}

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (ArgumentException e)
    {
        await Results.Problem(title: "Invalid request", detail: e.Message, statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(context);
    }
});

app.MapPost("/products", async (ProductRequest request, ProductService service) =>
{
    var product = await service.SaveAsync(Guid.NewGuid(), request);
    return Results.Created($"/products/{product.Id}", product);
});

app.MapPut("/products/{id:guid}", async (Guid id, ProductRequest request, ProductService service) =>
    Results.Ok(await service.SaveAsync(id, request)));

app.MapGet("/products/{id:guid}", async (Guid id, ProductService service) =>
    await service.GetAsync(id) is { } product ? Results.Ok(product) : Results.NotFound());

app.MapPatch("/products/{id:guid}/stock", async (Guid id, StockRequest request, ProductService service) =>
    await service.UpdateStockAsync(id, request.Stock) is { } product ? Results.Ok(product) : Results.NotFound());

// Storefront search: free text + filters + facets + sorting + paging, e.g.
//   /search?q=running&category=Shoes&brand=Nike&brand=Adidas&minPrice=50&maxPrice=200&inStock=true&sort=priceAsc&page=1
app.MapGet("/search", async (string? q, string? category, string[]? brand, decimal? minPrice, decimal? maxPrice,
    bool? inStock, ProductSort? sort, int? page, int? pageSize, ISearchIndex index) =>
{
    var query = new SearchQuery(q, category, brand ?? [], minPrice, maxPrice, inStock ?? false,
        sort ?? ProductSort.Relevance, Math.Max(page ?? 1, 1), Math.Clamp(pageSize ?? 12, 1, 50));
    return Results.Ok(await index.SearchAsync(query));
});

// Type-ahead for the search box.
app.MapGet("/suggest", async (string q, ISearchIndex index) =>
    string.IsNullOrWhiteSpace(q) ? Results.Ok(Array.Empty<string>()) : Results.Ok(await index.SuggestAsync(q.Trim(), 8)));

app.Run();

/// <summary>Loads demo products on startup when Catalog:Seed=true. Ids derive from the SKU, so seeding is idempotent.</summary>
public sealed class SeedService(IServiceScopeFactory scopes) : IHostedService
{
    private static readonly ProductRequest[] Products =
    [
        new("NK-AM270", "Nike Air Max 270 Running Shoe", "Cushioned everyday running shoe with a large Air unit", "Nike", "Shoes", 149.99m, 24, 4.6),
        new("NK-PEG40", "Nike Pegasus 40 Road Running Shoe", "Responsive road running shoe for daily training", "Nike", "Shoes", 129.99m, 0, 4.7),
        new("AD-UB23", "Adidas Ultraboost 23 Running Shoe", "Energy-returning boost midsole, knit upper", "Adidas", "Shoes", 189.99m, 12, 4.8),
        new("AD-SAM", "Adidas Samba Classic Sneaker", "Leather terrace sneaker with gum sole", "Adidas", "Shoes", 99.99m, 40, 4.5),
        new("PM-RSX", "Puma RS-X Sneaker", "Chunky retro runner with soft cushioning", "Puma", "Shoes", 109.99m, 7, 4.1),
        new("NK-DFJ", "Nike Dri-FIT Running Jacket", "Lightweight water-repellent running jacket", "Nike", "Clothing", 84.99m, 31, 4.3),
        new("AD-TRK", "Adidas Essentials Track Pants", "Tapered fit cotton blend track pants", "Adidas", "Clothing", 54.99m, 58, 4.2),
        new("PM-TEE", "Puma Essentials Logo T-Shirt", "Soft cotton t-shirt with printed logo", "Puma", "Clothing", 24.99m, 120, 4.0),
        new("NK-BPK", "Nike Brasilia Training Backpack", "Durable 24L backpack with padded straps", "Nike", "Bags", 44.99m, 19, 4.4),
        new("AD-DUF", "Adidas Training Duffel Bag", "Roomy gym duffel with shoe compartment", "Adidas", "Bags", 59.99m, 0, 4.3),
        new("GR-WTR", "Garmin Forerunner 265 Running Watch", "GPS running watch with AMOLED display", "Garmin", "Electronics", 449.99m, 9, 4.7),
        new("JB-TUR", "JBL Tour Pro 2 Wireless Earbuds", "Noise cancelling earbuds with smart charging case", "JBL", "Electronics", 249.99m, 22, 4.4)
    ];

    public async Task StartAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ProductService>();
        foreach (var product in Products)
        {
            var id = new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(product.Sku)));
            await service.SaveAsync(id, product, ct);
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

public partial class Program;

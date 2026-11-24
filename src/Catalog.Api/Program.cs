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

app.MapGet("/search", async (string q, string? category, ISearchIndex index) =>
    Results.Ok(await index.SearchAsync(q, category)));

app.Run();

public partial class Program;

# CatalogSearch

An e-commerce **product catalog service** in **C# / .NET 10**: MongoDB as the source of truth, **Elasticsearch** for full-text search, **Redis** cache-aside for reads, and **RabbitMQ (MassTransit)** to keep the search index in sync asynchronously.

![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![MongoDB](https://img.shields.io/badge/MongoDB-47A248?logo=mongodb&logoColor=white)
![Elasticsearch](https://img.shields.io/badge/Elasticsearch-005571?logo=elasticsearch&logoColor=white)
![Redis](https://img.shields.io/badge/Redis-DC382D?logo=redis&logoColor=white)
![RabbitMQ](https://img.shields.io/badge/RabbitMQ-FF6600?logo=rabbitmq&logoColor=white)

## How it fits together

```
POST/PUT /products ──> MongoDB (truth) ──> Redis: invalidate ──> RabbitMQ: ProductChanged
                                                                        │
GET /products/{id}  ──> Redis (cache-aside, 5 min) ──> MongoDB          ▼
GET /search?q=&category= ──────────────────────────> Elasticsearch <── consumer re-indexes
```

- **Write path**: persist, invalidate the cached entry, publish `ProductChanged`. A consumer loads the product and indexes it, so the API never blocks on search indexing. Indexing is an idempotent upsert, so redelivered messages are harmless; the consumer retries with exponential back-off.
- **Read path**: cache-aside with a 5 minute TTL; updates invalidate the key.
- **Ports and adapters**: `IProductStore`, `ISearchIndex`, `IEventPublisher` each have an in-memory implementation and a real adapter. A backing service is enabled simply by providing its connection string; without one the in-memory version is used, so the API and its tests run with zero infrastructure.

| Setting | Enables |
| --- | --- |
| `ConnectionStrings:MongoDb` | MongoDB product store |
| `ConnectionStrings:Elasticsearch` | Elasticsearch index |
| `ConnectionStrings:Redis` | Redis cache |
| `ConnectionStrings:RabbitMq` | MassTransit/RabbitMQ messaging (otherwise delivered in-process) |

## API

| Method | Path | Description |
| --- | --- | --- |
| POST | `/products` | `{name, description, category, price}` -> 201 |
| PUT | `/products/{id}` | Create or replace |
| GET | `/products/{id}` | Cached read |
| GET | `/search?q=&category=` | Full-text search (name boosted over description), optional category filter |

Interactive docs at `/scalar` in Development.

## Run

```bash
docker compose up --build        # API on :8080 with Mongo, Elasticsearch, Redis, RabbitMQ (UI :15672)
dotnet run --project src/Catalog.Api   # or: no infrastructure, everything in memory
dotnet test
```

## Status

The 5 automated tests exercise the full flow against the in-memory adapters. The MongoDB, Elasticsearch, Redis and RabbitMQ adapters compile and are wired through the compose file, but have not been integration-tested against live servers yet.

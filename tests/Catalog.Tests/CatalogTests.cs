using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Catalog.Tests;

[TestClass]
public class CatalogTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [TestInitialize]
    public void Setup()
    {
        // No connection strings => in-memory store, index, cache and in-process messaging.
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<Guid> CreateAsync(string name, string description = "", string category = "shoes", decimal price = 10)
    {
        var response = await _client.PostAsJsonAsync("/products", new { name, description, category, price });
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<string[]> SearchAsync(string url) =>
        (await _client.GetFromJsonAsync<JsonElement>(url)).EnumerateArray().Select(p => p.GetProperty("name").GetString()!).ToArray();

    [TestMethod]
    public async Task Created_product_is_searchable_by_name_and_description()
    {
        await CreateAsync("Trail Runner", "Lightweight running shoe");
        await CreateAsync("Leather Wallet", "Slim wallet");

        CollectionAssert.AreEqual(new[] { "Trail Runner" }, await SearchAsync("/search?q=running"));
        CollectionAssert.AreEqual(new[] { "Leather Wallet" }, await SearchAsync("/search?q=wallet"));
    }

    [TestMethod]
    public async Task Search_can_be_filtered_by_category()
    {
        await CreateAsync("Trail Runner", category: "shoes");
        await CreateAsync("Trail Backpack", category: "bags");

        CollectionAssert.AreEqual(new[] { "Trail Backpack" }, await SearchAsync("/search?q=trail&category=bags"));
    }

    [TestMethod]
    public async Task Update_invalidates_cache_and_refreshes_the_index()
    {
        var id = await CreateAsync("Old Name", price: 10);
        await _client.GetAsync($"/products/{id}"); // warms the cache

        var update = await _client.PutAsJsonAsync($"/products/{id}", new { name = "New Name", description = "", category = "shoes", price = 25 });
        Assert.AreEqual(HttpStatusCode.OK, update.StatusCode);

        var product = await _client.GetFromJsonAsync<JsonElement>($"/products/{id}");
        Assert.AreEqual("New Name", product.GetProperty("name").GetString());
        Assert.AreEqual(25m, product.GetProperty("price").GetDecimal());
        CollectionAssert.AreEqual(new[] { "New Name" }, await SearchAsync("/search?q=name"));
        Assert.IsEmpty(await SearchAsync("/search?q=old"));
    }

    [TestMethod]
    public async Task Invalid_product_returns_bad_request()
    {
        var response = await _client.PostAsJsonAsync("/products", new { name = " ", description = "", category = "shoes", price = 5 });
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);

        response = await _client.PostAsJsonAsync("/products", new { name = "X", description = "", category = "shoes", price = -1 });
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task Unknown_product_returns_not_found() =>
        Assert.AreEqual(HttpStatusCode.NotFound, (await _client.GetAsync($"/products/{Guid.NewGuid()}")).StatusCode);
}

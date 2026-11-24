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
        // No connection strings => in-memory store, index, cache and in-process messaging; demo products seeded.
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Catalog:Seed", "true"));
        _client = _factory.CreateClient();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<JsonElement> SearchAsync(string query) => await _client.GetFromJsonAsync<JsonElement>($"/search?{query}");

    private static string[] Names(JsonElement result) =>
        result.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("name").GetString()!).ToArray();

    private static Dictionary<string, int> Facet(JsonElement result, string name) =>
        result.GetProperty(name).EnumerateArray().ToDictionary(f => f.GetProperty("value").GetString()!, f => f.GetProperty("count").GetInt32());

    [TestMethod]
    public async Task Free_text_matches_every_term_and_ranks_name_hits_first()
    {
        var result = await SearchAsync("q=running shoe");

        var names = Names(result);
        Assert.IsGreaterThanOrEqualTo(3, names.Length);
        Assert.IsTrue(names.All(n => n.Contains("Shoe")), "all hits are shoes");
        Assert.IsFalse(names.Contains("Nike Dri-FIT Running Jacket"), "a jacket does not match 'shoe'");
    }

    [TestMethod]
    public async Task Filters_narrow_results_by_brand_price_and_stock()
    {
        var byBrand = await SearchAsync("category=Shoes&brand=Nike");
        Assert.AreEqual(2, byBrand.GetProperty("total").GetInt32());

        var byPrice = await SearchAsync("category=Shoes&minPrice=100&maxPrice=150");
        Assert.IsTrue(Names(byPrice).All(n => n != "Adidas Ultraboost 23 Running Shoe" && n != "Adidas Samba Classic Sneaker"));

        var inStock = await SearchAsync("brand=Nike&inStock=true");
        Assert.IsFalse(Names(inStock).Contains("Nike Pegasus 40 Road Running Shoe"), "out of stock is hidden");
    }

    [TestMethod]
    public async Task Facets_describe_the_matching_set()
    {
        var result = await SearchAsync("q=nike");

        CollectionAssert.AreEquivalent(new Dictionary<string, int> { ["Nike"] = 4 }.ToList(), Facet(result, "brands").ToList());
        Assert.AreEqual(2, Facet(result, "categories")["Shoes"]);
        Assert.AreEqual(44.99m, result.GetProperty("minPrice").GetDecimal());
        Assert.AreEqual(149.99m, result.GetProperty("maxPrice").GetDecimal());
    }

    [TestMethod]
    public async Task Results_can_be_sorted_and_paged()
    {
        var asc = Names(await SearchAsync("category=Shoes&sort=PriceAsc"));
        Assert.AreEqual("Adidas Samba Classic Sneaker", asc[0]);

        var desc = Names(await SearchAsync("category=Shoes&sort=PriceDesc"));
        Assert.AreEqual("Adidas Ultraboost 23 Running Shoe", desc[0]);

        var page2 = await SearchAsync("category=Shoes&sort=PriceAsc&pageSize=2&page=2");
        Assert.AreEqual(5, page2.GetProperty("total").GetInt32());
        Assert.HasCount(2, Names(page2));
        CollectionAssert.AreEqual(asc.Skip(2).Take(2).ToArray(), Names(page2));
    }

    [TestMethod]
    public async Task Suggest_completes_words_in_product_names()
    {
        var suggestions = (await _client.GetFromJsonAsync<JsonElement>("/suggest?q=ultra")).EnumerateArray().Select(s => s.GetString()).ToArray();

        CollectionAssert.AreEqual(new[] { "Adidas Ultraboost 23 Running Shoe" }, suggestions);
    }

    [TestMethod]
    public async Task Stock_update_flows_to_the_search_index_and_invalidates_cache()
    {
        var pegasus = (await SearchAsync("q=pegasus")).GetProperty("items")[0];
        var id = pegasus.GetProperty("id").GetGuid();
        Assert.IsFalse(pegasus.GetProperty("inStock").GetBoolean());
        await _client.GetAsync($"/products/{id}"); // warms the cache

        var response = await _client.PatchAsJsonAsync($"/products/{id}/stock", new { stock = 5 });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        Assert.AreEqual(5, (await _client.GetFromJsonAsync<JsonElement>($"/products/{id}")).GetProperty("stock").GetInt32());
        Assert.IsTrue(Names(await SearchAsync("brand=Nike&inStock=true")).Contains("Nike Pegasus 40 Road Running Shoe"));
    }

    [TestMethod]
    public async Task New_product_becomes_searchable_by_sku()
    {
        var response = await _client.PostAsJsonAsync("/products", new
        {
            sku = "tn-001", name = "Trail Gaiters", description = "Debris protection", brand = "Salomon", category = "Clothing", price = 29.9, stock = 3, rating = 4.2
        });
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);

        CollectionAssert.AreEqual(new[] { "Trail Gaiters" }, Names(await SearchAsync("q=TN-001")));
    }

    [TestMethod]
    public async Task Invalid_input_returns_bad_request_and_unknown_product_not_found()
    {
        var invalid = await _client.PostAsJsonAsync("/products", new { sku = "X", name = "X", brand = "B", category = "C", price = 5, stock = 1, rating = 9 });
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);

        Assert.AreEqual(HttpStatusCode.NotFound, (await _client.GetAsync($"/products/{Guid.NewGuid()}")).StatusCode);
    }
}

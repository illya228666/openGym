using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenGym.Nutrition.Persistence;
using OpenGym.Nutrition.Api;
using OpenGym.Nutrition.Authentication;
using OpenGym.Nutrition.Services;
using Xunit;

namespace OpenGym.Nutrition.Tests;

public sealed class ExternalCatalogTests
{
    private const string Code = "7622210449283";
    private static JsonObject Food(string code = Code) => JsonNode.Parse("""
        {"code":"7622210449283","product_name":"Alpenmilch","brands":"Milka","quantity":"100 g",
         "nutrition":{"input_sets":[{"source":"packaging","preparation":"as_sold","per":"100g",
          "nutrients":{"energy-kcal":{"value":530,"unit":"kcal"},"proteins":{"value":6.3,"unit":"g"},
          "fat":{"value":29,"unit":"g"},"carbohydrates":{"value":59,"unit":"g"}}}]}}
        """)!.AsObject().Also(p => p["code"] = code);
    private static JsonObject Set(JsonObject food) => food["nutrition"]!["input_sets"]![0]!.AsObject();
    private static JsonObject Nutrient(JsonObject food, string key) => Set(food)["nutrients"]![key]!.AsObject();
    private static ExternalProductResponse Parse(JsonObject food) => OpenFoodFacts.Parse(JsonSerializer.SerializeToElement(food), DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("computed")]
    [InlineData("modifier")]
    [InlineData("negative")]
    [InlineData("range")]
    [InlineData("unit")]
    [InlineData("basis")]
    [InlineData("macro-sum")]
    [InlineData("serving")]
    [InlineData("100ml")]
    [InlineData("prepared")]
    [InlineData("estimate")]
    [InlineData("quality")]
    [InlineData("warning")]
    [InlineData("malformed-quality")]
    [InlineData("conflict")]
    [InlineData("preferred-incomplete")]
    public void RejectsIncompleteAmbiguousAndUnconfirmedNutrition(string failure)
    {
        var p = Food();
        var n = Nutrient(p, "proteins");
        switch (failure)
        {
            case "missing": Set(p)["nutrients"]!.AsObject().Remove("proteins"); break;
            case "null": n["value"] = null; break;
            case "computed": n.Remove("value"); n["value_computed"] = 6.3; break;
            case "modifier": n["modifier"] = "<"; break;
            case "negative": n["value"] = -1; break;
            case "range": n["value"] = 101; break;
            case "unit": n["unit"] = "ml"; break;
            case "basis": Set(p)["per_quantity"] = 250; break;
            case "macro-sum": n["value"] = 90; break;
            case "serving": Set(p)["per"] = "serving"; break;
            case "100ml": Set(p)["per"] = "100ml"; break;
            case "prepared": Set(p)["preparation"] = "prepared"; break;
            case "estimate": Set(p)["source"] = "estimate"; break;
            case "quality": p["data_quality_errors_tags"] = new JsonArray("en:nutrition-value-over-100"); break;
            case "warning": p["data_quality_warnings_tags"] = new JsonArray("en:energy-value-inconsistent"); break;
            case "malformed-quality": p["data_quality_errors_tags"] = "wrong"; break;
            case "conflict":
            case "preferred-incomplete":
                var other = Set(p).DeepClone();
                if (failure == "conflict") other["nutrients"]!["proteins"]!["value"] = 7;
                else { other["source"] = "manufacturer"; other["nutrients"]!.AsObject().Remove("proteins"); }
                p["nutrition"]!["input_sets"]!.AsArray().Add(other);
                break;
        }
        var result = Parse(p);
        Assert.False(result.CanImport);
        Assert.Null(result.Fingerprint);
        Assert.Null(result.Per100Grams);
        Assert.NotEmpty(result.Issues);
    }

    [Fact]
    public void ExplicitZeroUnitsPriorityAndFingerprints()
    {
        var p = Food();
        Nutrient(p, "proteins")["value"] = 0;
        Assert.Equal(0m, Parse(p).Per100Grams!.Protein);
        Nutrient(p, "proteins")["value"] = 6300;
        Nutrient(p, "proteins")["unit"] = "mg";
        Assert.Equal(6.3m, Parse(p).Per100Grams!.Protein);
        var first = Parse(p);
        Assert.True(first.CanImport);
        Assert.Equal(first.Fingerprint, Parse(p.DeepClone().AsObject()).Fingerprint);
        Nutrient(p, "energy-kcal")["value"] = 530.000m;
        Assert.Equal(first.Fingerprint, Parse(p).Fingerprint);
        p["quantity"] = "250 g";
        Assert.NotEqual(first.Fingerprint, Parse(p).Fingerprint);
        var manufacturer = Set(p).DeepClone();
        manufacturer["source"] = "manufacturer";
        manufacturer["nutrients"]!["energy-kcal"]!["value"] = 540;
        p["nutrition"]!["input_sets"]!.AsArray().Add(manufacturer);
        Assert.Equal(540m, Parse(p).Per100Grams!.Calories);
        Assert.Equal("manufacturer", Parse(p).NutritionSource);
    }

    [Theory]
    [InlineData("034000470693", "0034000470693")]
    [InlineData("07622210449283", Code)]
    [InlineData("0000012345678", "12345678")]
    public void BarcodeAliasesUseOffCanonicalIdentity(string barcode, string expected) =>
        Assert.Equal(expected, ProductSearch.CanonicalCode(barcode));

    [Fact]
    public async Task SearchFiltersPaginationAndExactBarcodePreserveVariants()
    {
        using var app = new CatalogFactory();
        using var client = app.Client("alice");
        app.Off.SearchBody = new { products = new[] {
            new { code = Code, product_name = "Alpenmilch", brands = "Milka", quantity = "100 g" },
            new { code = "7622210449284", product_name = "Alpenmilch Oreo", brands = "Milka", quantity = "250 g" }
        }, count = 21 };
        var page = await client.GetFromJsonAsync<ExternalProductPage>("/api/nutrition/external/products?search=%20%EF%BC%ADilka%20%20Oreo&brand=milka&page=1");
        Assert.Equal(2, page!.Products.Count);
        Assert.True(page.HasMore);
        Assert.Contains("search_terms=MILKA%20OREO", app.Off.Paths.Last());
        Assert.Contains("tag_0=MILKA", app.Off.Paths.Last());
        var exact = await client.GetFromJsonAsync<ExternalProductPage>("/api/nutrition/external/products?barcode=" + Code);
        Assert.Single(exact!.Products);
        Assert.Contains("/api/v3.6/product/" + Code, app.Off.Paths.Last());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/nutrition/external/products")).StatusCode);
    }

    [Fact]
    public async Task ConcurrentImportsReusePerUserAndKeepHistoricalSnapshots()
    {
        using var app = new CatalogFactory();
        using var alice = app.Client("alice");
        using var bob = app.Client("bob");
        var candidate = (await alice.GetFromJsonAsync<ExternalProductResponse>("/api/nutrition/external/products/" + Code))!;
        var body = new ImportProductRequest("openfoodfacts", Code, candidate.Fingerprint);
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => alice.PostAsJsonAsync("/api/nutrition/products/import", body)));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        var savedRows = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<ProductResponse>()));
        var saved = savedRows[0]!;
        foreach (var row in savedRows) Assert.Equal(saved.Id, row!.Id);
        var bobSaved = (await (await bob.PostAsJsonAsync("/api/nutrition/products/import", body)).Content.ReadFromJsonAsync<ProductResponse>())!;
        Assert.NotEqual(saved.Id, bobSaved.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync("/api/nutrition/products/" + saved.Id)).StatusCode);
        var entryBody = new CreateEntryRequest(Guid.NewGuid(), saved.Id, 40, "2026-10-14", null);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsJsonAsync("/api/nutrition/entries", entryBody)).StatusCode);
        var entry = (await (await alice.PostAsJsonAsync("/api/nutrition/entries", entryBody)).Content.ReadFromJsonAsync<EntryResponse>())!;
        Assert.Equal(212m, entry.Nutrition.Calories);
        await alice.PutAsJsonAsync("/api/nutrition/products/" + saved.Id, new ProductRequest("My chocolate", "Milka", 500, 6, 25, 60));
        app.Off.Unavailable = true;
        var calls = app.Off.Calls;
        var reused = (await (await alice.PostAsJsonAsync("/api/nutrition/products/import", body)).Content.ReadFromJsonAsync<ProductResponse>())!;
        Assert.Equal(calls, app.Off.Calls);
        var alias = (await (await alice.PostAsJsonAsync("/api/nutrition/products/import", body with { ExternalId = "0" + Code }))
            .Content.ReadFromJsonAsync<ProductResponse>())!;
        Assert.Equal(saved.Id, alias.Id);
        Assert.Equal(calls, app.Off.Calls);
        Assert.Equal("user", reused.NutritionSource);
        Assert.Equal(500m, reused.Calories);
        Assert.Equal(entry, await alice.GetFromJsonAsync<EntryResponse>("/api/nutrition/entries/" + entry.Id));
        Assert.Single((await alice.GetFromJsonAsync<ProductPage>("/api/nutrition/products?barcode=" + Code + "&externalSource=openfoodfacts&externalId=" + Code))!.Products);
        await alice.DeleteAsync("/api/nutrition/products/" + saved.Id);
        Assert.Equal(HttpStatusCode.Gone, (await alice.PostAsJsonAsync("/api/nutrition/products/import", body)).StatusCode);
        Assert.Equal(entry, await alice.GetFromJsonAsync<EntryResponse>("/api/nutrition/entries/" + entry.Id));
    }

    [Fact]
    public async Task ImportRechecksFingerprintAndNeverSavesMissingNutrients()
    {
        using var app = new CatalogFactory();
        using var client = app.Client("alice");
        var candidate = (await client.GetFromJsonAsync<ExternalProductResponse>("/api/nutrition/external/products/" + Code))!;
        var body = new ImportProductRequest("openfoodfacts", Code, candidate.Fingerprint);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/nutrition/products/import", new {
            source = body.Source, externalId = body.ExternalId, fingerprint = body.Fingerprint, calories = 1 })).StatusCode);
        Nutrient(app.Off.Product, "fat")["value"] = 30;
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/nutrition/products/import", body)).StatusCode);
        Set(app.Off.Product)["nutrients"]!.AsObject().Remove("fat");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/api/nutrition/products/import", body)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<ProductPage>("/api/nutrition/products"))!.Products);
    }

    [Fact]
    public async Task LegacyMigrationBackfillsUnicodeSearchWithoutMergingVariants()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nutrition-upgrade-" + Guid.NewGuid());
        var path = Path.Combine(directory, "test.db");
        Directory.CreateDirectory(directory);
        try
        {
            var options = new DbContextOptionsBuilder<NutritionDbContext>().UseSqlite("Data Source=" + path).Options;
            await using (var old = new NutritionDbContext(options))
            {
                await old.GetService<IMigrator>().MigrateAsync("20261013095904_InitialFoodDiary");
                foreach (var name in new[] { "Ｍilka  Oreo 100 g", "Ｍilka  Oreo 250 g" })
                    await old.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Products (Id, UserId, Name, Brand, SearchText, Calories, Protein, Fat, Carbohydrates, IsDeleted) VALUES ({Guid.NewGuid()}, 'alice', {name}, 'Милка', {name}, '530', '6.3', '29', '59', 0)");
            }
            using var app = new CatalogFactory(path);
            using var client = app.Client("alice");
            var page = (await client.GetFromJsonAsync<ProductPage>("/api/nutrition/products?search=milka%20oreo&brand=" + Uri.EscapeDataString("милка")))!;
            Assert.Equal(2, page.Products.Count);
            Assert.Empty((await client.GetFromJsonAsync<ProductPage>("/api/nutrition/products?brand=oreo"))!.Products);
            Assert.Single((await client.GetFromJsonAsync<ProductPage>("/api/nutrition/products?search=milka%20oreo%20100"))!.Products);
            Assert.All(page.Products, p => Assert.Null(p.ExternalId));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CacheCoalescingExpiryBudgetsAndRetryAfter()
    {
        using var app = new CatalogFactory();
        using var client = app.Client("alice");
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => client.GetAsync("/api/nutrition/external/products/" + Code)));
        Assert.Equal(1, app.Off.Calls);
        app.Clock.Now += TimeSpan.FromMinutes(16);
        await client.GetAsync("/api/nutrition/external/products/" + Code);
        Assert.Equal(2, app.Off.Calls);
        for (var i = 0; i < 10; i++) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/nutrition/external/products?search=food" + i)).StatusCode);
        var limited = await client.GetAsync("/api/nutrition/external/products?search=another");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        app.Clock.Now += TimeSpan.FromMinutes(1);
        app.Off.Status = HttpStatusCode.TooManyRequests;
        var remoteLimited = await client.GetAsync("/api/nutrition/external/products/12345678");
        Assert.Equal(HttpStatusCode.TooManyRequests, remoteLimited.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(120), remoteLimited.Headers.RetryAfter!.Delta);
        app.Off.Status = HttpStatusCode.OK;
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/api/nutrition/external/products/12345679")).StatusCode);
    }

    [Theory]
    [InlineData("unavailable", 503)]
    [InlineData("json", 502)]
    [InlineData("oversize", 502)]
    [InlineData("missing", 404)]
    [InlineData("timeout", 503)]
    public async Task HandlesExternalFailuresWithoutWriting(string failure, int status)
    {
        using var app = new CatalogFactory();
        using var client = app.Client("alice");
        app.Off.Unavailable = failure == "unavailable";
        app.Off.Timeout = failure == "timeout";
        app.Off.Raw = failure == "json" ? "{broken" : failure == "oversize" ? new string('x', 2 * 1024 * 1024 + 1) : null;
        if (failure == "missing") app.Off.Status = HttpStatusCode.NotFound;
        Assert.Equal(status, (int)(await client.GetAsync("/api/nutrition/external/products/" + Code)).StatusCode);
        if (failure == "missing")
        {
            await client.GetAsync("/api/nutrition/external/products/" + Code);
            Assert.Equal(1, app.Off.Calls);
        }
        Assert.Empty((await client.GetFromJsonAsync<ProductPage>("/api/nutrition/products"))!.Products);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class CatalogFactory(string? databasePath = null) : WebApplicationFactory<Program>
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "nutrition-external-" + Guid.NewGuid());
        public OffBackend Off { get; } = new();
        public TestClock Clock { get; } = new();
        public HttpClient Client(string user)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", user);
            return client;
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Nutrition:DatabasePath", databasePath ?? Path.Combine(directory, "test.db"));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(Clock);
                services.AddHttpClient(OpenGymAuthenticationHandler.ClientName).ConfigurePrimaryHttpMessageHandler(() => new AuthBackend());
                services.AddHttpClient(OpenFoodFacts.ClientName).ConfigurePrimaryHttpMessageHandler(() => Off);
            });
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
    private sealed class AuthBackend : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new {
                user = new { id = request.Headers.Authorization!.Parameter, name = "Test" } }) });
    }
    private sealed class OffBackend : HttpMessageHandler
    {
        public JsonObject Product { get; } = Food();
        public object SearchBody = new { products = Array.Empty<object>(), count = 0 };
        public bool Unavailable;
        public bool Timeout;
        public string? Raw;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public int Calls;
        public System.Collections.Concurrent.ConcurrentQueue<string> Paths { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Paths.Enqueue(request.RequestUri!.PathAndQuery);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Contains("OpenGym/", request.Headers.UserAgent.ToString());
            await Task.Delay(20, ct);
            if (Unavailable) throw new HttpRequestException("offline");
            if (Timeout) throw new TaskCanceledException("timeout");
            var response = new HttpResponseMessage(Status) { Content = Raw is not null ? new StringContent(Raw)
                : JsonContent.Create(request.RequestUri.AbsolutePath == "/cgi/search.pl" ? SearchBody : new { product = Product }) };
            if (Status == HttpStatusCode.TooManyRequests) response.Headers.RetryAfter = new(TimeSpan.FromSeconds(120));
            return response;
        }
    }
}

internal static class TestObjectExtensions
{
    public static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}

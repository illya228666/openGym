using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using OpenGym.Nutrition.Api;
using OpenGym.Nutrition.Authentication;
using OpenGym.Nutrition.Services;
using OpenGym.Nutrition.Persistence;
using Xunit;

namespace OpenGym.Nutrition.Tests;

public sealed class CustomProductApiTests
{
    private static ProductRequest Chicken() => new("Cooked chicken breast", null, 165, 31.02m, 3.57m, 0,
        Guid.NewGuid(), new("usda", "171077", FoodCompositionClient.SourceUrl("171077")), "cooked", "roasted");

    [Theory]
    [InlineData("ai_estimate")]
    [InlineData("user_provided")]
    [InlineData(null)]
    public async Task OfflineValuesCreateReuseAndSnapshotWithoutSource(string? origin)
    {
        using var factory = new CustomFactory();
        factory.Source.Unreachable = true;
        using var alice = factory.Client("alice");
        using var bob = factory.Client("bob");
        var body = Chicken() with { Source = null, CookingMethod = "air_fried", DataOrigin = origin };
        Assert.Empty((await alice.GetFromJsonAsync<ProductPage>("/api/nutrition/products"))!.Products);
        var product = await Create(alice, body);
        Assert.Equal(origin ?? "ai_estimate", product.Provenance!.DataOrigin);
        Assert.Null(product.Provenance.Source);
        Assert.False(product.Provenance.PreparationAnalog);
        var retry = await alice.PostAsJsonAsync("/api/nutrition/products", body);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(product, await retry.Content.ReadFromJsonAsync<ProductResponse>());
        var aliasBody = body with { Id = Guid.NewGuid(), Name = "  COOKED  CHICKEN BREAST ", Calories = 170 };
        var alias = await alice.PostAsJsonAsync("/api/nutrition/products", aliasBody);
        Assert.Equal(HttpStatusCode.OK, alias.StatusCode);
        Assert.Equal(product, await alias.Content.ReadFromJsonAsync<ProductResponse>());
        Assert.Equal(product, await (await alice.PostAsJsonAsync("/api/nutrition/products", aliasBody)).Content.ReadFromJsonAsync<ProductResponse>());
        Assert.Single((await alice.GetFromJsonAsync<ProductPage>("/api/nutrition/products"))!.Products);
        var consumption = new CreateEntryRequest(Guid.NewGuid(), product.Id, 150, "2026-10-15", null);
        var entryResult = await alice.PostAsJsonAsync("/api/nutrition/entries", consumption);
        Assert.Equal(HttpStatusCode.Created, entryResult.StatusCode);
        var entry = (await entryResult.Content.ReadFromJsonAsync<EntryResponse>())!;
        Assert.Equal(new Nutrients(247.5m, 46.53m, 5.355m, 0), entry.Nutrition);
        Assert.Equal(product.Provenance, entry.Provenance);
        Assert.Equal(entry, await (await alice.PostAsJsonAsync("/api/nutrition/entries", consumption)).Content.ReadFromJsonAsync<EntryResponse>());
        Assert.Single((await alice.GetFromJsonAsync<DiaryResponse>("/api/nutrition/diary?date=2026-10-15"))!.Entries);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync("/api/nutrition/products/" + product.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsJsonAsync("/api/nutrition/entries", consumption)).StatusCode);
        Assert.Empty((await bob.GetFromJsonAsync<ProductPage>("/api/nutrition/products"))!.Products);
        Assert.Equal(product.Id, (await Create(bob, body)).Id);
        await Error(alice, body with { Calories = 170 }, "idempotency_conflict", HttpStatusCode.Conflict);
        var edited = await alice.PutAsJsonAsync("/api/nutrition/products/" + product.Id, new ProductRequest("Updated", null, 200, 30, 5, 0));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal("user_provided", (await edited.Content.ReadFromJsonAsync<ProductResponse>())!.Provenance!.DataOrigin);
        Assert.Equal(entry, await alice.GetFromJsonAsync<EntryResponse>("/api/nutrition/entries/" + entry.Id));
        await alice.DeleteAsync("/api/nutrition/products/" + product.Id);
        await Error(alice, body, "product_deleted", HttpStatusCode.Gone);
        Assert.Equal(0, factory.Source.Calls);
    }

    [Fact]
    public async Task OnlyVerifiedClaimsNeedSourceChecksAndEstimatesStayUnverified()
    {
        using var factory = new CustomFactory();
        using var client = factory.Client("alice");
        var body = Chicken() with { DataOrigin = "verified_reference" };
        await Error(client, body with { Source = null }, "unsupported_source");
        await Error(client, body with { Calories = 166 }, "source_values_mismatch");
        factory.Source.Unreachable = true;
        await Error(client, body, "source_unavailable", HttpStatusCode.ServiceUnavailable);
        var calls = factory.Source.Calls;
        var estimate = await Create(client, body with { DataOrigin = "ai_estimate", Calories = 166, CookingMethod = "air_fried" });
        Assert.Equal("ai_estimate", estimate.Provenance!.DataOrigin);
        Assert.Equal("unverified_reference", estimate.Provenance.Source!.VerificationStatus);
        Assert.Null(estimate.Provenance.Source.VerifiedAtUtc);
        Assert.Equal(calls, factory.Source.Calls);
        await Error(client, body with { Id = Guid.NewGuid(), Source = null, DataOrigin = "laboratory" }, "invalid_input");
        await Error(client, body with { Id = Guid.NewGuid(), Source = null, DataOrigin = "ai_estimate", Protein = null }, "invalid_input");
    }

    [Fact]
    public async Task LegacyFingerprintAndStoredProvenanceRemainCompatible()
    {
        using var factory = new CustomFactory();
        using var client = factory.Client("alice");
        var body = Chicken();
        var product = await Create(client, body);
        var oldRequestJson = JsonSerializer.Serialize(new { body.Name, body.Brand, body.Calories, body.Protein,
            body.Fat, body.Carbohydrates, body.Id, body.Source, body.FoodState, body.CookingMethod, body.ConfirmedPreparationAnalog });
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NutritionDbContext>();
        var key = await db.ProductCreations.FindAsync("alice", body.Id!.Value);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(oldRequestJson))), key!.Fingerprint);
        var oldProvenance = JsonSerializer.Serialize(new { product.Provenance!.Source, product.Provenance.FoodState,
            product.Provenance.CookingMethod, product.Provenance.PreparationAnalog });
        var persisted = await db.Products.FindAsync("alice", product.Id);
        persisted!.ProvenanceJson = oldProvenance;
        await db.SaveChangesAsync();
        factory.Source.Unreachable = true;
        var replay = await client.PostAsJsonAsync("/api/nutrition/products", body);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(product, await replay.Content.ReadFromJsonAsync<ProductResponse>());
        Assert.Equal("verified_reference", (await client.GetFromJsonAsync<ProductResponse>("/api/nutrition/products/" + product.Id))!.Provenance!.DataOrigin);
        persisted.ProvenanceJson = JsonSerializer.Serialize(new { Source = product.Provenance!.Source! with { VerificationStatus = "user_edited" },
            product.Provenance.FoodState, product.Provenance.CookingMethod, product.Provenance.PreparationAnalog });
        await db.SaveChangesAsync();
        Assert.Equal("user_provided", (await client.GetFromJsonAsync<ProductResponse>("/api/nutrition/products/" + product.Id))!.Provenance!.DataOrigin);
    }

    [Fact]
    public async Task VerifiedCreationThen150GramsKeepsHistoricalProvenance()
    {
        using var factory = new CustomFactory();
        using var client = factory.Client("alice");
        var body = Chicken();
        var product = await Create(client, body);
        Assert.Equal(body.Id, product.Id);
        Assert.Equal("source_verified_reference", product.Provenance!.Source!.VerificationStatus);
        Assert.Equal("SR Legacy", product.Provenance.Source.DataType);
        Assert.Equal("cooked", product.Provenance.FoodState);
        Assert.False(product.Provenance.PreparationAnalog);
        Assert.Equal(FoodCompositionClient.SourceUrl("171077"), product.Provenance.Source.Url);
        var entryRequest = new CreateEntryRequest(Guid.NewGuid(), product.Id, 150, "2026-10-15", null);
        var response = await client.PostAsJsonAsync("/api/nutrition/entries", entryRequest);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var entry = (await response.Content.ReadFromJsonAsync<EntryResponse>())!;
        Assert.Equal(new Nutrients(247.5m, 46.53m, 5.355m, 0), entry.Nutrition);
        Assert.Equal(product.Provenance, entry.Provenance);
        Assert.Equal(entry, await (await client.PostAsJsonAsync("/api/nutrition/entries", entryRequest)).Content.ReadFromJsonAsync<EntryResponse>());
        var edit = await client.PutAsJsonAsync("/api/nutrition/products/" + product.Id, new ProductRequest("Changed", null, 200, 30, 5, 1));
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal("user_edited", (await edit.Content.ReadFromJsonAsync<ProductResponse>())!.Provenance!.Source!.VerificationStatus);
        await client.DeleteAsync("/api/nutrition/products/" + product.Id);
        var changedEntry = await client.PutAsJsonAsync("/api/nutrition/entries/" + entry.Id, new UpdateEntryRequest(100, "2026-10-15", null, null));
        var retained = (await changedEntry.Content.ReadFromJsonAsync<EntryResponse>())!;
        Assert.Equal(165, retained.Nutrition.Calories);
        Assert.Equal(entry.Provenance, retained.Provenance);
        Assert.Single((await client.GetFromJsonAsync<DiaryResponse>("/api/nutrition/diary?date=2026-10-15"))!.Entries);
    }

    [Fact]
    public async Task DurableRetryAliasesDeduplicateAndRemainUserScoped()
    {
        var directory = Path.Combine(Path.GetTempPath(), "custom-restart-" + Guid.NewGuid());
        var path = Path.Combine(directory, "test.db");
        var body = Chicken();
        var alias = body with { Id = Guid.NewGuid(), Name = "Куриная грудка" };
        ProductResponse product;
        try
        {
            using (var factory = new CustomFactory(path))
            using (var alice = factory.Client("alice"))
            using (var bob = factory.Client("bob"))
            {
                product = await Create(alice, body);
                var duplicate = await alice.PostAsJsonAsync("/api/nutrition/products", alias);
                Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
                Assert.Equal(product, await duplicate.Content.ReadFromJsonAsync<ProductResponse>());
                Assert.Empty((await bob.GetFromJsonAsync<ProductPage>("/api/nutrition/products?userId=alice"))!.Products);
                Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync("/api/nutrition/products/" + product.Id)).StatusCode);
                var foreignEntry = await bob.PostAsJsonAsync("/api/nutrition/entries", new CreateEntryRequest(Guid.NewGuid(), product.Id, 150, "2026-10-15", null));
                Assert.Equal(HttpStatusCode.NotFound, foreignEntry.StatusCode);
                var bobProduct = await Create(bob, body);
                Assert.Equal(product.Id, bobProduct.Id);
                Assert.Single((await alice.GetFromJsonAsync<ProductPage>("/api/nutrition/products"))!.Products);
            }
            using var reopened = new CustomFactory(path);
            using var client = reopened.Client("alice");
            reopened.Source.Status = HttpStatusCode.ServiceUnavailable;
            foreach (var retry in new[] { body, alias })
            {
                var result = await client.PostAsJsonAsync("/api/nutrition/products", retry);
                Assert.Equal(HttpStatusCode.OK, result.StatusCode);
                Assert.Equal(product, await result.Content.ReadFromJsonAsync<ProductResponse>());
            }
            Assert.Equal(0, reopened.Source.Calls);
            await Error(client, alias with { Calories = 166 }, "idempotency_conflict", HttpStatusCode.Conflict);
            await client.DeleteAsync("/api/nutrition/products/" + product.Id);
            await Error(client, alias, "product_deleted", HttpStatusCode.Gone);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ConcurrentSameAndDifferentKeysCreateOneProduct()
    {
        using var factory = new CustomFactory();
        using var client = factory.Client("alice");
        var body = Chicken();
        var replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => client.PostAsJsonAsync("/api/nutrition/products",
            i < 4 ? body : body with { Id = Guid.NewGuid() })));
        Assert.Single(replies, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(replies, r => Assert.True(r.IsSuccessStatusCode, r.StatusCode.ToString()));
        var products = await Task.WhenAll(replies.Select(r => r.Content.ReadFromJsonAsync<ProductResponse>()));
        Assert.Single(products.Select(p => p!.Id).Distinct());
        Assert.Single((await client.GetFromJsonAsync<ProductPage>("/api/nutrition/products"))!.Products);
    }

    [Fact]
    public async Task InvalidSourceAndValuesNeverFetchOrCreate()
    {
        using var factory = new CustomFactory();
        using var client = factory.Client("alice");
        var body = Chicken();
        foreach (var invalid in new[] { body with { Id = Guid.Empty }, body with { Name = " " }, body with { Name = new string('x', 201) },
            body with { Brand = new string('b', 101) }, body with { Calories = null }, body with { Protein = null },
            body with { Fat = null }, body with { Carbohydrates = null }, body with { Calories = -1 }, body with { Calories = 1000.001m },
            body with { Protein = 100.001m }, body with { Fat = 0.0001m }, body with { Protein = 90, Fat = 20 },
            body with { FoodState = "unknown" }, body with { CookingMethod = null }, body with { CookingMethod = "magic" },
            body with { FoodState = "raw", CookingMethod = "roasted" } })
            await Error(client, invalid, "invalid_input");
        foreach (var url in new[] { "http://127.0.0.1/private", "https://169.254.169.254/latest/meta-data/", "https://fdc.nal.usda.gov.evil/food-details/171077/nutrients",
            "https://user@fdc.nal.usda.gov/food-details/171077/nutrients", FoodCompositionClient.SourceUrl("171077") + "?redirect=evil" })
            await Error(client, body with { Source = body.Source! with { Url = url } }, "unsupported_source");
        await Error(client, body with { Source = null, DataOrigin = "verified_reference" }, "unsupported_source");
        await Error(client, body with { Source = body.Source! with { FoodId = "../private" } }, "unsupported_source");
        await Error(client, body with { Source = body.Source! with { Provider = "ai" } }, "unsupported_source");
        Assert.Equal(0, factory.Source.Calls);
        Assert.Empty((await client.GetFromJsonAsync<ProductPage>("/api/nutrition/products"))!.Products);
    }

    [Fact]
    public async Task PreparationAnalogsNeedConfirmationButRawMismatchCannotBeOverridden()
    {
        using var factory = new CustomFactory();
        using var client = factory.Client("alice");
        var body = Chicken() with { CookingMethod = "air_fried" };
        await Error(client, body, "preparation_confirmation_required");
        var saved = await Create(client, body with { ConfirmedPreparationAnalog = true });
        Assert.True(saved.Provenance!.PreparationAnalog);
        Assert.Equal("air_fried", saved.Provenance.CookingMethod);
        Assert.Contains("roasted", saved.Provenance.Source!.Description);
        await Error(client, Chicken() with { FoodState = "raw", CookingMethod = null, ConfirmedPreparationAnalog = true }, "food_state_mismatch");
        factory.Source.Description = "Chicken breast, raw";
        await Error(client, Chicken() with { ConfirmedPreparationAnalog = true }, "food_state_mismatch");
        var raw = await Create(client, Chicken() with { FoodState = "raw", CookingMethod = null });
        Assert.Equal("raw", raw.Provenance!.FoodState);
    }

    [Fact]
    public async Task MismatchIncompleteOrUnreachableSourceLeavesNoProduct()
    {
        using var factory = new CustomFactory();
        using var client = factory.Client("alice");
        await Error(client, Chicken() with { Calories = 0 }, "source_values_mismatch");
        foreach (var broken in new[] { "{}", "null", "{broken", SourceBackend.FoodJson(omitProtein: true),
            SourceBackend.FoodJson(unit: "kJ"), SourceBackend.FoodJson(type: "Branded"), SourceBackend.FoodJson(id: 123) })
        {
            factory.Source.Body = broken;
            await Error(client, Chicken(), "source_data_incomplete");
        }
        factory.Source.Body = null;
        foreach (var status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable, HttpStatusCode.Redirect })
        {
            factory.Source.Status = status;
            await Error(client, Chicken(), "source_unavailable", HttpStatusCode.ServiceUnavailable);
        }
        factory.Source.Status = HttpStatusCode.NotFound;
        await Error(client, Chicken(), "source_not_found", HttpStatusCode.NotFound);
        factory.Source.Unreachable = true;
        await Error(client, Chicken(), "source_unavailable", HttpStatusCode.ServiceUnavailable);
        Assert.Empty((await client.GetFromJsonAsync<ProductPage>("/api/nutrition/products"))!.Products);
    }

    [Fact]
    public async Task SearchUsesFixedHostAndRequiresAuthentication()
    {
        using var factory = new CustomFactory();
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/nutrition/reference-foods?search=chicken")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/nutrition/products", Chicken())).StatusCode);
        using var client = factory.Client("alice");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/nutrition/reference-foods?search=" )).StatusCode);
        var page = (await client.GetFromJsonAsync<ReferenceFoodPage>("/api/nutrition/reference-foods?search=chicken%26api_key%3Devil"))!;
        var food = Assert.Single(page.Foods);
        Assert.Equal(new Nutrients(165, 31.02m, 3.57m, 0), food.Per100Grams);
        Assert.Equal("https://api.nal.usda.gov", factory.Source.Target!.GetLeftPart(UriPartial.Authority));
        Assert.Equal("", factory.Source.Target.Query);
        Assert.Equal("chicken&api_key=evil", JsonDocument.Parse(factory.Source.SearchBody!).RootElement.GetProperty("query").GetString());
        Assert.Equal("DEMO_KEY", factory.Source.ApiKey);
        Assert.False(factory.Source.HasCredentials);
    }

    private static async Task<ProductResponse> Create(HttpClient client, ProductRequest body)
    {
        var result = await client.PostAsJsonAsync("/api/nutrition/products", body);
        Assert.Equal(HttpStatusCode.Created, result.StatusCode);
        return (await result.Content.ReadFromJsonAsync<ProductResponse>())!;
    }
    private static async Task Error(HttpClient client, ProductRequest body, string code, HttpStatusCode status = HttpStatusCode.BadRequest)
    {
        var result = await client.PostAsJsonAsync("/api/nutrition/products", body);
        Assert.Equal(status, result.StatusCode);
        Assert.Equal(code, (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    private sealed class CustomFactory(string? path = null) : WebApplicationFactory<Program>
    {
        private readonly string database = path ?? Path.Combine(Path.GetTempPath(), "nutrition-custom-" + Guid.NewGuid(), "test.db");
        public SourceBackend Source { get; } = new();
        public HttpClient Client(string user)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", "Bearer " + user);
            return client;
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Nutrition:DatabasePath", database);
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient(OpenGymAuthenticationHandler.ClientName).ConfigurePrimaryHttpMessageHandler(() => new IdentityBackend());
                services.AddHttpClient(FoodCompositionClient.ClientName).ConfigurePrimaryHttpMessageHandler(() => Source);
            });
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            SqliteConnection.ClearAllPools();
            if (path is null && Directory.Exists(Path.GetDirectoryName(database))) Directory.Delete(Path.GetDirectoryName(database)!, true);
        }
    }
    private sealed class IdentityBackend : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var user = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(user is "alice" or "bob" ? HttpStatusCode.OK : HttpStatusCode.Unauthorized)
                { Content = JsonContent.Create(new { user = new { id = user, name = user } }) });
        }
    }
    private sealed class SourceBackend : HttpMessageHandler
    {
        public string? Body { get; set; }
        public string Description { get; set; } = "Chicken, breast, meat only, cooked, roasted";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public bool Unreachable { get; set; }
        public int Calls;
        public Uri? Target { get; private set; }
        public string? ApiKey { get; private set; }
        public bool HasCredentials { get; private set; }
        public string? SearchBody { get; private set; }
        // Synthetic USDA-shaped response: values are fixture data, not a live-source assertion.
        public static string FoodJson(bool omitProtein = false, string unit = "kcal", string type = "SR Legacy", int id = 171077,
            string description = "Chicken, breast, meat only, cooked, roasted", bool search = false) => JsonSerializer.Serialize(new {
                fdcId = id, description, dataType = type,
                foodNutrients = new[] { (1008, 165m, unit), (1003, 31.02m, "g"), (1004, 3.57m, "g"), (1005, 0m, "g") }
                    .Where(n => !omitProtein || n.Item1 != 1003).Select(n => search
                        ? (object)new { nutrientId = n.Item1, value = n.Item2, unitName = n.Item3 }
                        : new { nutrient = new { id = n.Item1, unitName = n.Item3 }, amount = n.Item2 }) });
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Target = request.RequestUri;
            ApiKey = request.Headers.GetValues("X-Api-Key").Single();
            HasCredentials = request.Headers.Authorization is not null || request.Headers.Contains("Cookie");
            SearchBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            if (Unreachable) throw new HttpRequestException("offline");
            var body = Body ?? (Target!.AbsolutePath.EndsWith("/search")
                ? "{\"foods\":[" + FoodJson(description: Description, search: true) + "," + FoodJson(type: "Branded", search: true) + "]}"
                : FoodJson(description: Description));
            return new HttpResponseMessage(Status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }
    }
}

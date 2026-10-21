using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OpenGym.Nutrition.Api;
using OpenGym.Nutrition.Authentication;
using OpenGym.Nutrition.Services;
using OpenGym.Nutrition.Persistence;
using Xunit;

namespace OpenGym.Nutrition.Tests;

public sealed class DiaryApiTests
{
    private static ProductRequest Food(string name = "Гречка", decimal calories = 123.456m) => new(name, "Acme", calories, 12.345m, 6.789m, 45.678m);
    private static async Task<ProductResponse> CreateProduct(HttpClient client, ProductRequest? body = null)
    {
        var response = await client.PostAsJsonAsync("/api/nutrition/products", body ?? Food());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProductResponse>())!;
    }
    private static CreateEntryRequest Consumption(Guid product, string? time = "12:00", decimal quantity = 100) =>
        new(Guid.NewGuid(), product, quantity, "2026-10-13", time);
    private static async Task<EntryResponse> CreateEntry(HttpClient client, CreateEntryRequest body)
    {
        var response = await client.PostAsJsonAsync("/api/nutrition/entries", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EntryResponse>())!;
    }
    private static async Task<DiaryResponse> Day(HttpClient client, string date = "2026-10-13") =>
        (await client.GetFromJsonAsync<DiaryResponse>("/api/nutrition/diary?date=" + date))!;

    [Fact]
    public async Task ProductsCrudSearchAndIsolation()
    {
        using var factory = new DiaryFactory();
        using var alice = factory.Client("alice");
        using var bob = factory.Client("bob");
        var product = await CreateProduct(alice);
        Assert.Equal(product, await alice.GetFromJsonAsync<ProductResponse>("/api/nutrition/products/" + product.Id));
        Assert.Single((await alice.GetFromJsonAsync<ProductPage>("/api/nutrition/products?search=" + Uri.EscapeDataString("грЕЧ")))!.Products);
        Assert.Single((await alice.GetFromJsonAsync<ProductPage>("/api/nutrition/products?search=acme"))!.Products);
        Assert.Empty((await bob.GetFromJsonAsync<ProductPage>("/api/nutrition/products?userId=alice"))!.Products);
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, "/api/nutrition/products/" + product.Id) { Content = JsonContent.Create(Food("Stolen")) };
            Assert.Equal(HttpStatusCode.NotFound, (await bob.SendAsync(request)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await alice.PutAsJsonAsync("/api/nutrition/products/" + product.Id, Food("Updated", 150))).StatusCode);
        Assert.Equal("Updated", (await alice.GetFromJsonAsync<ProductResponse>("/api/nutrition/products/" + product.Id))!.Name);
        Assert.Empty((await alice.GetFromJsonAsync<ProductPage>("/api/nutrition/products?search=" + Uri.EscapeDataString("греч")))!.Products);
        Assert.Equal(HttpStatusCode.OK, (await alice.DeleteAsync("/api/nutrition/products/" + product.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.DeleteAsync("/api/nutrition/products/" + product.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync("/api/nutrition/products/" + product.Id)).StatusCode);
        Assert.Empty((await alice.GetFromJsonAsync<ProductPage>("/api/nutrition/products"))!.Products);
    }

    [Fact]
    public async Task ProductPaginationHasNoMissingOrDuplicateRows()
    {
        using var factory = new DiaryFactory();
        using var client = factory.Client("alice");
        for (var i = 0; i < 51; i++) await CreateProduct(client, Food($"Product {i:00}"));
        var first = (await client.GetFromJsonAsync<ProductPage>("/api/nutrition/products"))!;
        var last = (await client.GetFromJsonAsync<ProductPage>("/api/nutrition/products?offset=50"))!;
        Assert.Equal(50, first.Products.Count);
        Assert.True(first.HasMore);
        Assert.Single(last.Products);
        Assert.False(last.HasMore);
        Assert.Equal(51, first.Products.Concat(last.Products).Select(p => p.Id).Distinct().Count());
        Assert.Equal("Product 50", last.Products[0].Name);
    }

    [Fact]
    public async Task DiarySnapshotsCrudOrderingAndTotals()
    {
        using var factory = new DiaryFactory();
        using var client = factory.Client("alice");
        using var other = factory.Client("bob");
        var product = await CreateProduct(client);
        var late = await CreateEntry(client, Consumption(product.Id, "18:30", 33.333m));
        var untimed = await CreateEntry(client, Consumption(product.Id, null, 50));
        var early = await CreateEntry(client, Consumption(product.Id, "08:00", 100));
        Assert.Equal(new Nutrients(41.15158848m, 4.11495885m, 2.26297737m, 15.22584774m), late.Nutrition);
        var day = await Day(client);
        Assert.Equal(new[] { early.Id, late.Id, untimed.Id }, day.Entries.Select(e => e.Id));
        Assert.Equal(new Nutrients(226.33558848m, 22.63245885m, 12.44647737m, 83.74284774m), day.Totals);
        Assert.Equal(new Nutrients(0, 0, 0, 0), (await Day(client, "2026-10-14")).Totals);
        Assert.Empty((await Day(other)).Entries);
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, "/api/nutrition/entries/" + late.Id)
                { Content = JsonContent.Create(new UpdateEntryRequest(200, "2026-10-13", null, null)) };
            Assert.Equal(HttpStatusCode.NotFound, (await other.SendAsync(request)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await CreateForeignEntry(other, product.Id)).StatusCode);
        await client.PutAsJsonAsync("/api/nutrition/products/" + product.Id, Food("Changed", 999));
        Assert.Equal(late, await client.GetFromJsonAsync<EntryResponse>("/api/nutrition/entries/" + late.Id));
        var updated = await client.PutAsJsonAsync("/api/nutrition/entries/" + late.Id, new UpdateEntryRequest(200, "2026-10-14", "09:15", null));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var retained = (await updated.Content.ReadFromJsonAsync<EntryResponse>())!;
        Assert.Equal("Гречка", retained.Name);
        Assert.Equal(246.912m, retained.Nutrition.Calories);
        Assert.Equal("09:15", retained.Time);
        Assert.Equal(2, (await Day(client)).Entries.Count);
        var replaced = await client.PutAsJsonAsync("/api/nutrition/entries/" + late.Id, new UpdateEntryRequest(200, "2026-10-14", null, product.Id));
        var replacement = (await replaced.Content.ReadFromJsonAsync<EntryResponse>())!;
        Assert.Equal("Changed", replacement.Name);
        Assert.Equal(1998m, replacement.Nutrition.Calories);
        await client.DeleteAsync("/api/nutrition/products/" + product.Id);
        Assert.Equal(123.456m, (await client.GetFromJsonAsync<EntryResponse>("/api/nutrition/entries/" + early.Id))!.Nutrition.Calories);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/nutrition/entries/" + early.Id,
            new UpdateEntryRequest(50, "2026-10-13", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/nutrition/entries", Consumption(product.Id))).StatusCode);
        await client.DeleteAsync("/api/nutrition/entries/" + untimed.Id);
        Assert.Single((await Day(client)).Entries);
        Assert.Equal(61.728m, (await Day(client)).Totals.Calories);
    }

    private static Task<HttpResponseMessage> CreateForeignEntry(HttpClient client, Guid product) =>
        client.PostAsJsonAsync("/api/nutrition/entries", Consumption(product));

    [Fact]
    public async Task CreationRetryConflictAndDeletion()
    {
        using var factory = new DiaryFactory();
        using var client = factory.Client("alice");
        var product = await CreateProduct(client);
        var body = Consumption(product.Id);
        var entry = await CreateEntry(client, body);
        await client.PutAsJsonAsync("/api/nutrition/products/" + product.Id, Food("Changed", 500));
        var retry = await client.PostAsJsonAsync("/api/nutrition/entries", body);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(entry, await retry.Content.ReadFromJsonAsync<EntryResponse>());
        Assert.Single((await Day(client)).Entries);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/nutrition/entries", body with { QuantityGrams = 101 })).StatusCode);
        await client.DeleteAsync("/api/nutrition/products/" + product.Id);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/nutrition/entries", body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync("/api/nutrition/entries/" + entry.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync("/api/nutrition/entries/" + entry.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await client.PostAsJsonAsync("/api/nutrition/entries", body)).StatusCode);
        Assert.Empty((await Day(client)).Entries);
    }

    [Fact]
    public async Task PieceEntriesConvertUnitsKeepProductWeightSnapshotAndAcceptLegacyGrams()
    {
        using var factory = new DiaryFactory();
        using var client = factory.Client("alice");
        var product = await CreateProduct(client, Food("Egg", 155) with { GramsPerPiece = 40 });
        var id = Guid.NewGuid();
        var request = new CreateEntryRequest(id, product.Id, null, "2026-10-13", null, 2.5m, "pieces");
        var created = await client.PostAsJsonAsync("/api/nutrition/entries", request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var entry = (await created.Content.ReadFromJsonAsync<EntryResponse>())!;
        Assert.Equal(2.5m, entry.Quantity);
        Assert.Equal("pieces", entry.Unit);
        Assert.Equal(40m, entry.GramsPerPiece);
        Assert.Equal(100m, entry.QuantityGrams);
        Assert.Equal(155m, entry.Nutrition.Calories);
        var retry = await client.PostAsJsonAsync("/api/nutrition/entries", request);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(entry, await retry.Content.ReadFromJsonAsync<EntryResponse>());
        await client.PutAsJsonAsync("/api/nutrition/products/" + product.Id, Food("Egg", 155) with { GramsPerPiece = 20 });
        Assert.Equal(entry, await client.GetFromJsonAsync<EntryResponse>("/api/nutrition/entries/" + id));

        var switched = await client.PutAsJsonAsync("/api/nutrition/entries/" + id,
            new UpdateEntryRequest(null, "2026-10-13", null, null, 100m, "grams"));
        var grams = (await switched.Content.ReadFromJsonAsync<EntryResponse>())!;
        Assert.Equal((100m, "grams", 100m), (grams.Quantity, grams.Unit, grams.QuantityGrams));
        var switchedBack = await client.PutAsJsonAsync("/api/nutrition/entries/" + id,
            new UpdateEntryRequest(null, "2026-10-13", null, null, 2.5m, "pieces"));
        Assert.Equal(100m, (await switchedBack.Content.ReadFromJsonAsync<EntryResponse>())!.QuantityGrams);

        var legacyId = Guid.NewGuid();
        var legacy = await client.PostAsJsonAsync("/api/nutrition/entries",
            new CreateEntryRequest(legacyId, product.Id, 50, "2026-10-13", null));
        var oldFormat = (await legacy.Content.ReadFromJsonAsync<EntryResponse>())!;
        Assert.Equal((50m, "grams", 50m), (oldFormat.Quantity, oldFormat.Unit, oldFormat.QuantityGrams));
    }

    [Fact]
    public async Task PieceEntriesRequireKnownWeightAndAmountFormatsCannotConflict()
    {
        using var factory = new DiaryFactory();
        using var client = factory.Client("alice");
        var product = await CreateProduct(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/nutrition/products/" + product.Id,
            Food("No weight", 123) with { GramsPerPiece = 0.0001m })).StatusCode);
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/nutrition/entries",
            new CreateEntryRequest(id, product.Id, null, "2026-10-13", null, 1.5m, "pieces"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/nutrition/entries",
            new { id, productId = product.Id, quantity = 1, date = "2026-10-13" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/nutrition/entries",
            new { id, productId = product.Id, quantity = 1, unit = "servings", date = "2026-10-13" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/nutrition/entries",
            new { id, productId = product.Id, quantity = 1.0001m, unit = "grams", date = "2026-10-13" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/nutrition/entries",
            new { id, productId = product.Id, quantityGrams = 100, quantity = 1, unit = "grams", date = "2026-10-13" })).StatusCode);
        Assert.Empty((await Day(client)).Entries);
    }

    [Fact]
    public async Task PortionsSnapshotProductsApplyAsGroupedRowsAndDetachWhenMoved()
    {
        using var factory = new DiaryFactory();
        using var client = factory.Client("alice");
        using var other = factory.Client("bob");
        var eggs = await CreateProduct(client, Food("Egg", 155) with { GramsPerPiece = 40 });
        var oats = await CreateProduct(client, Food("Oats", 380));
        var portionId = Guid.NewGuid();
        var template = new CreatePortionRequest(portionId, "Breakfast", [
            new PortionIngredientRequest(eggs.Id, 2, "pieces"),
            new PortionIngredientRequest(oats.Id, 30, "grams")]);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/nutrition/portions", template)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/nutrition/portions", template)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/nutrition/portions", template with { Name = "Other breakfast" })).StatusCode);
        var portion = await client.GetFromJsonAsync<PortionResponse>("/api/nutrition/portions/" + portionId);
        Assert.Equal(new[] { "pieces", "grams" }, portion!.Ingredients.Select(i => i.Unit));
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync("/api/nutrition/portions/" + portionId)).StatusCode);

        var applicationId = Guid.NewGuid();
        var applicationRequest = new PortionApplicationRequest(applicationId, 1.5m, "2026-10-13", "08:00");
        var appliedResponse = await client.PostAsJsonAsync($"/api/nutrition/portions/{portionId}/apply", applicationRequest);
        Assert.Equal(HttpStatusCode.Created, appliedResponse.StatusCode);
        var applied = (await appliedResponse.Content.ReadFromJsonAsync<PortionApplicationResponse>())!;
        Assert.Equal(2, applied.Entries.Count);
        Assert.Equal(new[] { 3m, 45m }, applied.Entries.Select(e => e.Quantity));
        Assert.Equal(new[] { "pieces", "grams" }, applied.Entries.Select(e => e.Unit));
        Assert.All(applied.Entries, entry => Assert.Equal((applicationId, "Breakfast"), (entry.PortionGroupId, entry.PortionName)));
        Assert.Equal(120m, applied.Entries[0].QuantityGrams);
        Assert.Equal(186m, applied.Entries[0].Nutrition.Calories);

        await client.PutAsJsonAsync("/api/nutrition/products/" + eggs.Id, Food("Egg changed", 300) with { GramsPerPiece = 20 });
        await client.PutAsJsonAsync("/api/nutrition/portions/" + portionId,
            new UpdatePortionRequest("Updated breakfast", [new PortionIngredientRequest(oats.Id, 50, "grams")]));
        Assert.Equal(applied.Entries[0], await client.GetFromJsonAsync<EntryResponse>("/api/nutrition/entries/" + applied.Entries[0].Id));
        var retry = await client.PostAsJsonAsync($"/api/nutrition/portions/{portionId}/apply", applicationRequest);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var retried = (await retry.Content.ReadFromJsonAsync<PortionApplicationResponse>())!;
        Assert.Equal(applied.Id, retried.Id);
        Assert.Equal(applied.Name, retried.Name);
        Assert.Equal(applied.Multiplier, retried.Multiplier);
        Assert.Equal(applied.Entries.Select(e => e.Id), retried.Entries.Select(e => e.Id));
        Assert.Equal(applied.Entries.Select(e => e.Nutrition), retried.Entries.Select(e => e.Nutrition));
        Assert.Equal(2, (await Day(client)).Entries.Count);

        var moved = await client.PutAsJsonAsync("/api/nutrition/entries/" + applied.Entries[0].Id,
            new UpdateEntryRequest(null, "2026-10-14", null, null, applied.Entries[0].Quantity, applied.Entries[0].Unit));
        var detached = (await moved.Content.ReadFromJsonAsync<EntryResponse>())!;
        Assert.Null(detached.PortionGroupId);
        Assert.Null(detached.PortionName);
        Assert.Single((await Day(client)).Entries);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync("/api/nutrition/portions/" + portionId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/nutrition/portions/" + portionId)).StatusCode);
        Assert.Single((await Day(client, "2026-10-14")).Entries);
    }

    [Fact]
    public async Task PortionsRejectMissingPieceWeightAndUnavailableProductsWithoutPartialWrites()
    {
        using var factory = new DiaryFactory();
        using var client = factory.Client("alice");
        var noWeight = await CreateProduct(client);
        var portionId = Guid.NewGuid();
        var invalid = new CreatePortionRequest(portionId, "Invalid", [new PortionIngredientRequest(noWeight.Id, 1, "pieces")]);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/nutrition/portions", invalid)).StatusCode);
        var weighted = await CreateProduct(client, Food("Egg", 155) with { GramsPerPiece = 40 });
        var valid = new CreatePortionRequest(portionId, "Snack", [new PortionIngredientRequest(weighted.Id, 1, "pieces")]);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/nutrition/portions", valid)).StatusCode);
        await client.DeleteAsync("/api/nutrition/products/" + weighted.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/nutrition/portions/{portionId}/apply",
            new PortionApplicationRequest(Guid.NewGuid(), 1, "2026-10-13", null))).StatusCode);
        Assert.Empty((await Day(client)).Entries);
    }

    [Fact]
    public async Task ConcurrentCreationCreatesOnlyOneEntry()
    {
        using var factory = new DiaryFactory();
        using var client = factory.Client("alice");
        var product = await CreateProduct(client);
        var body = Consumption(product.Id);
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.PostAsJsonAsync("/api/nutrition/entries", body)));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Single((await Day(client)).Entries);
    }

    [Theory]
    [InlineData("missing-name", "{\"calories\":1,\"protein\":1,\"fat\":1,\"carbohydrates\":1}")]
    [InlineData("blank-name", "{\"name\":\"  \",\"calories\":1,\"protein\":1,\"fat\":1,\"carbohydrates\":1}")]
    [InlineData("missing-nutrients", "{\"name\":\"Rice\"}")]
    [InlineData("negative", "{\"name\":\"Rice\",\"calories\":-1,\"protein\":1,\"fat\":1,\"carbohydrates\":1}")]
    [InlineData("macro-limit", "{\"name\":\"Rice\",\"calories\":1,\"protein\":100.001,\"fat\":1,\"carbohydrates\":1}")]
    [InlineData("calorie-limit", "{\"name\":\"Rice\",\"calories\":1000.001,\"protein\":1,\"fat\":1,\"carbohydrates\":1}")]
    [InlineData("precision", "{\"name\":\"Rice\",\"calories\":1.0001,\"protein\":1,\"fat\":1,\"carbohydrates\":1}")]
    public async Task InvalidProductsAreRejected(string caseName, string json)
    {
        Assert.NotEmpty(caseName);
        using var factory = new DiaryFactory();
        using var client = factory.Client("alice");
        var response = await client.PostAsync("/api/nutrition/products", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<ProductPage>("/api/nutrition/products"))!.Products);
    }

    [Fact]
    public async Task EntryAndQueryValidationRejectsInvalidValuesWithoutWrites()
    {
        using var factory = new DiaryFactory();
        using var client = factory.Client("alice");
        var product = await CreateProduct(client, new("Water", null, 0, 0, 0, 0));
        var body = Consumption(product.Id);
        foreach (var invalid in new[] { body with { Id = Guid.Empty }, body with { ProductId = Guid.Empty },
            body with { QuantityGrams = 0 }, body with { QuantityGrams = -1 }, body with { QuantityGrams = 100000.001m },
            body with { QuantityGrams = 0.0001m }, body with { Date = "2026-02-30" }, body with { Date = "2026-1-1" },
            body with { Date = null }, body with { Time = "24:00" }, body with { Time = "12:00:00" } })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/nutrition/entries", invalid)).StatusCode);
        foreach (var query in new[] { "/diary", "/diary?date=2026-02-30", "/products?offset=-1", "/products?offset=abc" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/nutrition" + query)).StatusCode);
        Assert.Empty((await Day(client)).Entries);
        var zero = await CreateEntry(client, body with { QuantityGrams = 0.001m, Date = "2024-02-29", Time = "00:00" });
        Assert.Equal(new Nutrients(0, 0, 0, 0), zero.Nutrition);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/nutrition/entries/" + zero.Id,
            new UpdateEntryRequest(0, "2024-02-29", null, null))).StatusCode);
        Assert.Equal(zero, await client.GetFromJsonAsync<EntryResponse>("/api/nutrition/entries/" + zero.Id));
    }

    [Fact]
    public async Task DatabaseSurvivesRestartAndMigrationsAreRepeatable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nutrition-restart-" + Guid.NewGuid());
        var path = Path.Combine(directory, "test.db");
        ProductResponse product;
        EntryResponse entry;
        try
        {
            using (var first = new DiaryFactory(path))
            using (var client = first.Client("alice"))
            {
                product = await CreateProduct(client);
                entry = await CreateEntry(client, Consumption(product.Id, null, 33.333m));
            }
            using var second = new DiaryFactory(path);
            using var reopened = second.Client("alice");
            Assert.Equal(product, await reopened.GetFromJsonAsync<ProductResponse>("/api/nutrition/products/" + product.Id));
            Assert.Equal(entry, Assert.Single((await Day(reopened)).Entries));
            using var connection = new SqliteConnection("Data Source=" + path);
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory";
            Assert.Equal(5L, await command.ExecuteScalarAsync());
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task PieceMigrationKeepsLegacyAmountAndNutritionSnapshot()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nutrition-piece-migration-" + Guid.NewGuid());
        var path = Path.Combine(directory, "test.db");
        var productId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        try
        {
            Directory.CreateDirectory(directory);
            var options = new DbContextOptionsBuilder<NutritionDbContext>().UseSqlite("Data Source=" + path).Options;
            await using (var old = new NutritionDbContext(options))
            {
                await old.GetService<IMigrator>().MigrateAsync("20261013095904_InitialFoodDiary");
                await old.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Products (Id, UserId, Name, Brand, SearchText, Calories, Protein, Fat, Carbohydrates, IsDeleted) VALUES ({productId}, 'alice', 'Old apple', NULL, 'old apple', '52', '0.3', '0.2', '14', 0)");
                await old.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO DiaryEntries (Id, UserId, ProductId, Date, Time, QuantityGrams, Name, Brand, Calories, Protein, Fat, Carbohydrates, CreatedAtUtc, CreationFingerprint, IsDeleted) VALUES ({entryId}, 'alice', {productId}, '2026-10-13', NULL, '125.5', 'Historical apple', NULL, '123.456', '2.5', '3.25', '7.75', '2026-10-13 12:00:00', 'old-fingerprint', 0)");
            }
            using var factory = new DiaryFactory(path);
            using var client = factory.Client("alice");
            var entry = (await client.GetFromJsonAsync<EntryResponse>("/api/nutrition/entries/" + entryId))!;
            Assert.Equal((125.5m, 125.5m, "grams", (decimal?)null),
                (entry.Quantity, entry.QuantityGrams, entry.Unit, entry.GramsPerPiece));
            Assert.Equal(new Nutrients(154.93728m, 3.1375m, 4.07875m, 9.72625m), entry.Nutrition);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task EveryNutritionOperationRequiresAuthentication()
    {
        using var factory = new DiaryFactory();
        using var client = factory.CreateClient();
        foreach (var (method, path) in new[] { (HttpMethod.Get, "external/products"),
            (HttpMethod.Get, "external/products/7622210449283"), (HttpMethod.Post, "products/import"),
            (HttpMethod.Get, "products"), (HttpMethod.Post, "products"),
            (HttpMethod.Get, "diary?date=2026-10-13"), (HttpMethod.Post, "entries"),
            (HttpMethod.Get, "products/" + Guid.NewGuid()), (HttpMethod.Put, "products/" + Guid.NewGuid()),
            (HttpMethod.Delete, "products/" + Guid.NewGuid()), (HttpMethod.Get, "entries/" + Guid.NewGuid()),
            (HttpMethod.Put, "entries/" + Guid.NewGuid()), (HttpMethod.Delete, "entries/" + Guid.NewGuid()) })
        {
            using var request = new HttpRequestMessage(method, "/api/nutrition/" + path) { Content = JsonContent.Create(new { userId = "alice" }) };
            request.Headers.Add("X-User-Id", "alice");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request)).StatusCode);
        }
    }

    [Fact]
    public async Task ProductLimitsAndNormalizationAreExplicit()
    {
        using var factory = new DiaryFactory();
        using var client = factory.Client("alice");
        var body = new ProductRequest("  " + new string('x', 200) + "  ", new string('b', 100), 1000, 100, 100, 100);
        var valid = await CreateProduct(client, body);
        Assert.Equal(new string('x', 200), valid.Name);
        Assert.Equal(body.Brand, valid.Brand);
        Assert.Equal(1000m, valid.Calories);
        foreach (var invalid in new[] { body with { Name = new string('x', 201) }, body with { Brand = new string('b', 101) },
            body with { Fat = -0.001m }, body with { Carbohydrates = 100.001m }, body with { Fat = 0.0001m } })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/nutrition/products", invalid)).StatusCode);
        var normalized = await CreateProduct(client, new("  Water  ", "  ", 0, 0, 0, 0));
        Assert.Equal("Water", normalized.Name);
        Assert.Null(normalized.Brand);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/nutrition/products?search=" + new string('x', 201))).StatusCode);
    }

    [Fact]
    public async Task MalformedBodiesReturnJsonValidationErrors()
    {
        using var factory = new DiaryFactory();
        using var client = factory.Client("alice");
        foreach (var (path, json) in new[] { ("products", "null"), ("products", "[]"), ("products", "{broken"),
            ("products", "{\"name\":\"Rice\",\"calories\":\"wrong\"}"), ("entries", "{\"id\":\"invalid\"}") })
        {
            var response = await client.PostAsync("/api/nutrition/" + path, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            Assert.Equal("invalid_input", error.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task RetryIdentityIsPerUserAndKeepsEditedEntryState()
    {
        using var factory = new DiaryFactory();
        using var alice = factory.Client("alice");
        using var bob = factory.Client("bob");
        var firstProduct = await CreateProduct(alice);
        var secondProduct = await CreateProduct(bob, Food("Bob food", 500));
        var body = Consumption(firstProduct.Id);
        var first = await CreateEntry(alice, body);
        var second = await CreateEntry(bob, body with { ProductId = secondProduct.Id });
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(500m, second.Nutrition.Calories);
        var updated = await alice.PutAsJsonAsync("/api/nutrition/entries/" + first.Id, new UpdateEntryRequest(250, "2026-10-14", null, null));
        var edited = await updated.Content.ReadFromJsonAsync<EntryResponse>();
        var retry = await alice.PostAsJsonAsync("/api/nutrition/entries", body);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(edited, await retry.Content.ReadFromJsonAsync<EntryResponse>());
        Assert.Single((await Day(alice, "2026-10-14")).Entries);
        Assert.Empty((await Day(alice)).Entries);
        Assert.Single((await Day(bob)).Entries);
    }

    [Fact]
    public void ManagedStartupDoesNotCreateMissingDatabase()
    {
        var path = Path.Combine(Path.GetTempPath(), "nutrition-missing-" + Guid.NewGuid(), "test.db");
        using var factory = new DiaryFactory(path, requireExisting: true);
        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("database is missing", error.Message);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("uninitialized")]
    [InlineData("newer-schema")]
    public async Task ManagedStartupRejectsUnsafeDatabaseWithoutReplacingIt(string scenario)
    {
        var directory = Path.Combine(Path.GetTempPath(), "nutrition-unsafe-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "test.db");
        try
        {
            if (scenario == "corrupt") File.WriteAllText(path, "not a database");
            else
            {
                using (var initial = new DiaryFactory(path))
                using (var client = initial.Client("alice")) await CreateProduct(client);
                using var db = new SqliteConnection("Data Source=" + path);
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = scenario == "uninitialized" ? "DELETE FROM __EFMigrationsHistory" :
                    "INSERT INTO __EFMigrationsHistory VALUES ('20990101000000_FutureSchema', '10.0.0')";
                command.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            var before = File.ReadAllBytes(path);
            using (var factory = new DiaryFactory(path, requireExisting: true))
            {
                if (scenario == "corrupt") Assert.Throws<SqliteException>(() => factory.CreateClient());
                else Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
            }
            SqliteConnection.ClearAllPools();
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ManagedRestartPreservesEntriesAndUserIsolation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nutrition-managed-" + Guid.NewGuid());
        var path = Path.Combine(directory, "test.db");
        try
        {
            EntryResponse entry;
            using (var initial = new DiaryFactory(path))
            using (var alice = initial.Client("alice")) entry = await CreateEntry(alice, Consumption((await CreateProduct(alice)).Id));
            using var restarted = new DiaryFactory(path, requireExisting: true);
            using var aliceAgain = restarted.Client("alice");
            using var bob = restarted.Client("bob");
            Assert.Equal(entry, Assert.Single((await Day(aliceAgain)).Entries));
            Assert.Empty((await Day(bob)).Entries);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    private sealed class DiaryFactory : WebApplicationFactory<Program>
    {
        private readonly bool ownsDirectory;
        private readonly string database;
        private readonly bool requireExisting;
        public DiaryFactory(string? path = null, bool requireExisting = false)
        {
            ownsDirectory = path is null;
            database = path ?? Path.Combine(Path.GetTempPath(), "nutrition-diary-" + Guid.NewGuid(), "test.db");
            this.requireExisting = requireExisting;
        }
        public HttpClient Client(string user)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", "Bearer " + user);
            return client;
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Nutrition:DatabasePath", database);
            builder.UseSetting("Nutrition:RequireExistingDatabase", requireExisting.ToString());
            builder.ConfigureServices(services => services.AddHttpClient(OpenGymAuthenticationHandler.ClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new AuthBackend()));
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            SqliteConnection.ClearAllPools();
            if (ownsDirectory && Directory.Exists(Path.GetDirectoryName(database))) Directory.Delete(Path.GetDirectoryName(database)!, true);
        }
    }
    private sealed class AuthBackend : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var user = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(user is "alice" or "bob" ? HttpStatusCode.OK : HttpStatusCode.Unauthorized)
                { Content = JsonContent.Create(new { user = new { id = user, name = user } }) });
        }
    }
}

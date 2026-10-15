using System.Security.Claims;
using OpenGym.Nutrition.Services;

namespace OpenGym.Nutrition.Api;

public static class NutritionEndpoints
{
    public static void MapNutrition(this WebApplication app)
    {
        var api = app.MapGroup("/api/nutrition").RequireAuthorization();
        api.MapGet("/products", (ClaimsPrincipal user, NutritionService service, string? search, string? brand,
            string? barcode, string? externalId, string? externalSource, int? offset, CancellationToken ct) =>
            service.Products(UserId(user), search, offset ?? 0, ct, brand, barcode, externalId, externalSource));
        api.MapGet("/external/products", (OpenFoodFacts external, string? search, string? brand, string? barcode, int? page, CancellationToken ct) =>
            external.Search(search, brand, barcode, page ?? 1, ct));
        api.MapGet("/external/products/{externalId}", (OpenFoodFacts external, string externalId, CancellationToken ct) =>
            external.Product(externalId, ct));
        api.MapPost("/products/import", async (ClaimsPrincipal user, NutritionService service,
            OpenFoodFacts external, ImportProductRequest request, CancellationToken ct) =>
        {
            var result = await service.ImportProduct(UserId(user), request, external, ct);
            return result.Created ? Results.Created($"/api/nutrition/products/{result.Product.Id}", result.Product)
                : Results.Ok(result.Product);
        });
        api.MapGet("/reference-foods", (FoodCompositionClient source, string? search, CancellationToken ct) => source.Search(search, ct));
        api.MapGet("/products/{id:guid}", (ClaimsPrincipal user, NutritionService service, Guid id, CancellationToken ct) =>
            service.Product(UserId(user), id, ct));
        api.MapPost("/products", async (ClaimsPrincipal user, NutritionService service, ProductRequest request, CancellationToken ct) =>
        {
            if (request.Id is not null || request.Source is not null || request.FoodState is not null
                || request.CookingMethod is not null || request.ConfirmedPreparationAnalog || request.DataOrigin is not null)
            {
                var result = await service.CreateCustomProduct(UserId(user), request, ct);
                return result.Created ? Results.Created($"/api/nutrition/products/{result.Product.Id}", result.Product) : Results.Ok(result.Product);
            }
            var product = await service.SaveProduct(UserId(user), null, request, ct);
            return Results.Created($"/api/nutrition/products/{product.Id}", product);
        });
        api.MapPut("/products/{id:guid}", (ClaimsPrincipal user, NutritionService service, Guid id, ProductRequest request, CancellationToken ct) =>
            service.SaveProduct(UserId(user), id, request, ct));
        api.MapDelete("/products/{id:guid}", async (ClaimsPrincipal user, NutritionService service, Guid id, CancellationToken ct) =>
        {
            await service.DeleteProduct(UserId(user), id, ct);
            return Results.Ok(new { ok = true });
        });
        api.MapGet("/diary", (ClaimsPrincipal user, NutritionService service, string? date, CancellationToken ct) =>
            service.Diary(UserId(user), date, ct));
        api.MapGet("/entries/{id:guid}", (ClaimsPrincipal user, NutritionService service, Guid id, CancellationToken ct) =>
            service.Entry(UserId(user), id, ct));
        api.MapPost("/entries", async (ClaimsPrincipal user, NutritionService service, CreateEntryRequest request, CancellationToken ct) =>
        {
            var result = await service.CreateEntry(UserId(user), request, ct);
            return result.Created ? Results.Created($"/api/nutrition/entries/{result.Entry.Id}", result.Entry) : Results.Ok(result.Entry);
        });
        api.MapPut("/entries/{id:guid}", (ClaimsPrincipal user, NutritionService service, Guid id, UpdateEntryRequest request, CancellationToken ct) =>
            service.UpdateEntry(UserId(user), id, request, ct));
        api.MapDelete("/entries/{id:guid}", async (ClaimsPrincipal user, NutritionService service, Guid id, CancellationToken ct) =>
        {
            await service.DeleteEntry(UserId(user), id, ct);
            return Results.Ok(new { ok = true });
        });
    }

    private static string UserId(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)!;
}

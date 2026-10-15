using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenGym.Nutrition.Api;
using OpenGym.Nutrition.Domain;

namespace OpenGym.Nutrition.Services;

public sealed partial class NutritionService
{
    /// <summary>Creates or reuses a verified reference food without changing existing definitions or history.</summary>
    public async Task<(ProductResponse Product, bool Created)> CreateCustomProduct(string userId, ProductRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        var brand = string.IsNullOrWhiteSpace(request.Brand) ? null : request.Brand.Trim();
        if (request.Id is null || request.Id == Guid.Empty || string.IsNullOrEmpty(name) || name.Length > 200 || brand?.Length > 100
            || !ValidValue(request.Calories, 1000) || !ValidValue(request.Protein, 100) || !ValidValue(request.Fat, 100)
            || !ValidValue(request.Carbohydrates, 100) || request.Protein + request.Fat + request.Carbohydrates > 100.003m
            || request.FoodState is not ("raw" or "ready" or "cooked")) throw Invalid();
        string[] methods = ["cooked", "roasted", "baked", "grilled", "boiled", "fried", "air_fried", "steamed", "stewed", "braised"];
        if (request.FoodState == "cooked" ? !methods.Contains(request.CookingMethod) : request.CookingMethod is not null) throw Invalid();
        FoodCompositionClient.ValidateSource(request.Source);
        var normalized = request with { Name = name, Brand = brand, Source = request.Source! with { Url = FoodCompositionClient.SourceUrl(request.Source!.FoodId) } };
        var fingerprint = Hash(JsonSerializer.Serialize(normalized));
        var replay = await ReplayProduct(userId, request.Id.Value, fingerprint, ct);
        if (replay is not null) return (replay, false);

        var verified = await foodComposition.Get(normalized.Source!, ct);
        var nutrients = new Nutrients(request.Calories!.Value, request.Protein!.Value, request.Fat!.Value, request.Carbohydrates!.Value);
        if (verified.Nutrients != nutrients) throw new NutritionException(400, "source_values_mismatch");
        var analog = FoodCompositionClient.PreparationAnalog(verified.Source, request.FoodState!, request.CookingMethod, request.ConfirmedPreparationAnalog);
        var provenance = new ProductProvenance(verified.Source, request.FoodState!, request.CookingMethod, analog);
        // Names may be translated. Source identity, brand and preparation determine reuse, not display text.
        var catalogKey = Hash(JsonSerializer.Serialize(new { normalized.Source!.Provider, normalized.Source.FoodId,
            normalized.FoodState, normalized.CookingMethod, Brand = brand?.ToUpperInvariant() }));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            replay = await ReplayProduct(userId, request.Id.Value, fingerprint, ct);
            if (replay is not null) return (replay, false);
            var product = await db.Products.SingleOrDefaultAsync(p => p.UserId == userId && p.CatalogKey == catalogKey, ct);
            var created = product is null;
            if (product is not null)
            {
                if (product.IsDeleted) throw new NutritionException(410, "product_deleted");
                if (ReadProvenance(product.ProvenanceJson)?.Source.VerificationStatus != "source_verified_reference"
                    || product.Calories != nutrients.Calories || product.Protein != nutrients.Protein
                    || product.Fat != nutrients.Fat || product.Carbohydrates != nutrients.Carbohydrates)
                    throw new NutritionException(409, "product_definition_conflict");
            }
            else
            {
                product = new Product { Id = request.Id.Value, UserId = userId, Name = name, Brand = brand,
                    SearchText = ProductSearch.Normalize(name + " " + brand + " " + verified.Source.Description),
                    BrandSearchText = ProductSearch.Normalize(brand ?? ""),
                    Calories = nutrients.Calories, Protein = nutrients.Protein, Fat = nutrients.Fat, Carbohydrates = nutrients.Carbohydrates,
                    CatalogKey = catalogKey, ProvenanceJson = JsonSerializer.Serialize(provenance) };
                db.Products.Add(product);
            }
            db.ProductCreations.Add(new ProductCreation { UserId = userId, Id = request.Id.Value,
                ProductId = product.Id, Fingerprint = fingerprint });
            try
            {
                // SaveChanges commits the product and retry alias in one transaction.
                await db.SaveChangesAsync(ct);
                return (ProductView(product), created);
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
            {
                db.ChangeTracker.Clear();
                if (attempt == 2) throw new NutritionException(409, "idempotency_conflict");
            }
        }
        throw new NutritionException(503, "nutrition_unavailable");
    }

    private async Task<ProductResponse?> ReplayProduct(string userId, Guid id, string fingerprint, CancellationToken ct)
    {
        var creation = await db.ProductCreations.AsNoTracking().SingleOrDefaultAsync(c => c.UserId == userId && c.Id == id, ct);
        if (creation is null) return null;
        if (creation.Fingerprint != fingerprint) throw new NutritionException(409, "idempotency_conflict");
        var product = await db.Products.AsNoTracking().SingleAsync(p => p.UserId == userId && p.Id == creation.ProductId, ct);
        if (product.IsDeleted) throw new NutritionException(410, "product_deleted");
        return ProductView(product);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

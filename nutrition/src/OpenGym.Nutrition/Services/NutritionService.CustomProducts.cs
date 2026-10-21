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
    /// <summary>Creates or reuses personal food with honest provenance, without changing existing history.</summary>
    public async Task<(ProductResponse Product, bool Created)> CreateCustomProduct(string userId, ProductRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        var brand = string.IsNullOrWhiteSpace(request.Brand) ? null : request.Brand.Trim();
        if (request.Id is null || request.Id == Guid.Empty || string.IsNullOrEmpty(name) || name.Length > 200 || brand?.Length > 100
            || !ValidValue(request.Calories, 1000) || !ValidValue(request.Protein, 100) || !ValidValue(request.Fat, 100)
            || !ValidValue(request.Carbohydrates, 100) || request.Protein + request.Fat + request.Carbohydrates > 100.003m
            || !ValidPieceWeight(request.GramsPerPiece) || request.FoodState is not ("raw" or "ready" or "cooked")) throw Invalid();
        string[] methods = ["cooked", "roasted", "baked", "grilled", "boiled", "fried", "air_fried", "steamed", "stewed", "braised"];
        if (request.FoodState == "cooked" ? !methods.Contains(request.CookingMethod) : request.CookingMethod is not null) throw Invalid();
        var dataOrigin = request.DataOrigin ?? (request.Source is null ? "ai_estimate" : "verified_reference");
        if (dataOrigin is not ("verified_reference" or "ai_estimate" or "user_provided")) throw Invalid();
        var reference = dataOrigin == "verified_reference";
        if (reference || request.Source is not null) FoodCompositionClient.ValidateSource(request.Source);
        // Keep the legacy serialization (including absent DataOrigin) for existing retry fingerprints.
        var normalized = request with { Name = name, Brand = brand, Source = request.Source is null ? null
            : request.Source with { Url = FoodCompositionClient.SourceUrl(request.Source.FoodId) } };
        var fingerprint = Hash(JsonSerializer.Serialize(normalized));
        var replay = await ReplayProduct(userId, request.Id.Value, fingerprint, ct);
        if (replay is not null) return (replay, false);

        var nutrients = new Nutrients(request.Calories!.Value, request.Protein!.Value, request.Fat!.Value, request.Carbohydrates!.Value);
        FoodSource? source = normalized.Source is null ? null : new(normalized.Source.Provider, normalized.Source.FoodId,
            normalized.Source.Url, "", "", "unverified_reference", null);
        var analog = false;
        if (reference)
        {
            var verified = await foodComposition.Get(normalized.Source!, ct);
            if (verified.Nutrients != nutrients) throw new NutritionException(400, "source_values_mismatch");
            source = verified.Source;
            analog = FoodCompositionClient.PreparationAnalog(source, request.FoodState!, request.CookingMethod, request.ConfirmedPreparationAnalog);
        }
        var provenance = new ProductProvenance(source, request.FoodState!, request.CookingMethod, analog, dataOrigin);
        // Reference keys retain their old identity; estimates reuse normalized food identity and preparation.
        var catalogKey = reference
            ? Hash(JsonSerializer.Serialize(new { normalized.Source!.Provider, normalized.Source.FoodId,
                normalized.FoodState, normalized.CookingMethod, Brand = brand?.ToUpperInvariant() }))
            : Hash(JsonSerializer.Serialize(new { DataOrigin = dataOrigin, Name = ProductSearch.Normalize(name),
                Brand = ProductSearch.Normalize(brand ?? ""), normalized.FoodState, normalized.CookingMethod }));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            replay = await ReplayProduct(userId, request.Id.Value, fingerprint, ct);
            if (replay is not null) return (replay, false);
            var product = await db.Products.SingleOrDefaultAsync(p => p.UserId == userId && p.CatalogKey == catalogKey, ct);
            var created = product is null;
            if (product is not null)
            {
                if (product.IsDeleted) throw new NutritionException(410, "product_deleted");
                if (reference && (ReadProvenance(product.ProvenanceJson)?.Source?.VerificationStatus != "source_verified_reference"
                    || product.Calories != nutrients.Calories || product.Protein != nutrients.Protein
                    || product.Fat != nutrients.Fat || product.Carbohydrates != nutrients.Carbohydrates))
                    throw new NutritionException(409, "product_definition_conflict");
                if (request.GramsPerPiece is not null) product.GramsPerPiece = request.GramsPerPiece;
            }
            else
            {
                product = new Product { Id = request.Id.Value, UserId = userId, Name = name, Brand = brand,
                    SearchText = ProductSearch.Normalize(name + " " + brand + " " + source?.Description),
                    BrandSearchText = ProductSearch.Normalize(brand ?? ""),
                    Calories = nutrients.Calories, Protein = nutrients.Protein, Fat = nutrients.Fat, Carbohydrates = nutrients.Carbohydrates,
                    GramsPerPiece = request.GramsPerPiece,
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

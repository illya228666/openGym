using OpenGym.Nutrition.Services;
using System.Text.Json.Serialization;

namespace OpenGym.Nutrition.Api;

public sealed record ProductRequest(string? Name, string? Brand, decimal? Calories,
    decimal? Protein, decimal? Fat, decimal? Carbohydrates, Guid? Id = null,
    FoodSourceRequest? Source = null, string? FoodState = null, string? CookingMethod = null,
    bool ConfirmedPreparationAnalog = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DataOrigin = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? GramsPerPiece = null);
public sealed record FoodSourceRequest(string Provider, string FoodId, string Url);
public sealed record FoodSource(string Provider, string FoodId, string Url, string Description,
    string DataType, string VerificationStatus, DateTime? VerifiedAtUtc);
// Default preserves old source-bearing provenance JSON; estimates do not need a Source.
public sealed record ProductProvenance(FoodSource? Source, string FoodState, string? CookingMethod,
    bool PreparationAnalog, string DataOrigin = "verified_reference");
public sealed record ReferenceFood(string FoodId, string Description, string DataType, string Url, Nutrients? Per100Grams);
public sealed record ReferenceFoodPage(IReadOnlyList<ReferenceFood> Foods);
public sealed record CreateEntryRequest(Guid Id, Guid ProductId, decimal? QuantityGrams, string? Date, string? Time,
    decimal? Quantity = null, string? Unit = null);
// Null ProductId keeps the historical snapshot; an explicit id replaces it with today's product.
public sealed record UpdateEntryRequest(decimal? QuantityGrams, string? Date, string? Time, Guid? ProductId,
    decimal? Quantity = null, string? Unit = null);
public sealed record ProductResponse(Guid Id, string Name, string? Brand, decimal Calories,
    decimal Protein, decimal Fat, decimal Carbohydrates, string? ExternalSource = null,
    string? ExternalId = null, string? Barcode = null, string? PackageQuantity = null,
    string? SourceUrl = null, string? NutritionSource = null, DateTimeOffset? RetrievedAtUtc = null,
    string? SourceFingerprint = null, ProductProvenance? Provenance = null, decimal? GramsPerPiece = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ImportProductRequest(string? Source, string? ExternalId, string? Fingerprint);
public sealed record ExternalProductCandidate(string Source, string ExternalId, string Name,
    string? Brand, string? PackageQuantity, string SourceUrl);
public sealed record ExternalProductPage(IReadOnlyList<ExternalProductCandidate> Products, bool HasMore, int Page);
public sealed record ExternalProductResponse(string Source, string ExternalId, string Name, string? Brand,
    string? PackageQuantity, string SourceUrl, Nutrients? Per100Grams, string? NutritionSource,
    string? Fingerprint, DateTimeOffset RetrievedAtUtc, bool CanImport, IReadOnlyList<string> Issues);
public sealed record EntryResponse(Guid Id, Guid ProductId, string Name, string? Brand,
    decimal QuantityGrams, string Date, string? Time, Nutrients Per100Grams, Nutrients Nutrition,
    ProductProvenance? Provenance = null, decimal Quantity = 0, string Unit = "grams", decimal? GramsPerPiece = null,
    Guid? PortionGroupId = null, string? PortionName = null);
public sealed record DiaryResponse(string Date, IReadOnlyList<EntryResponse> Entries, Nutrients Totals);
public sealed record ProductPage(IReadOnlyList<ProductResponse> Products, bool HasMore);
public sealed record PortionIngredientRequest(Guid ProductId, decimal Quantity, string Unit);
public sealed record CreatePortionRequest(Guid Id, string? Name, IReadOnlyList<PortionIngredientRequest>? Ingredients);
public sealed record UpdatePortionRequest(string? Name, IReadOnlyList<PortionIngredientRequest>? Ingredients);
public sealed record PortionApplicationRequest(Guid Id, decimal Multiplier, string? Date, string? Time);
public sealed record PortionIngredientResponse(Guid ProductId, string ProductName, string? ProductBrand,
    decimal Quantity, string Unit, decimal? GramsPerPiece, bool ProductAvailable);
public sealed record PortionResponse(Guid Id, string Name, IReadOnlyList<PortionIngredientResponse> Ingredients);
public sealed record PortionPage(IReadOnlyList<PortionResponse> Portions);
public sealed record PortionApplicationResponse(Guid Id, string Name, decimal Multiplier, string Date,
    string? Time, IReadOnlyList<EntryResponse> Entries, Nutrients Nutrition);

public sealed class NutritionException(int status, string code, int? retryAfterSeconds = null) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

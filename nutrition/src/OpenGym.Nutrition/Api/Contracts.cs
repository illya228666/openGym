using OpenGym.Nutrition.Services;
using System.Text.Json.Serialization;

namespace OpenGym.Nutrition.Api;

public sealed record ProductRequest(string? Name, string? Brand, decimal? Calories,
    decimal? Protein, decimal? Fat, decimal? Carbohydrates, Guid? Id = null,
    FoodSourceRequest? Source = null, string? FoodState = null, string? CookingMethod = null,
    bool ConfirmedPreparationAnalog = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DataOrigin = null);
public sealed record FoodSourceRequest(string Provider, string FoodId, string Url);
public sealed record FoodSource(string Provider, string FoodId, string Url, string Description,
    string DataType, string VerificationStatus, DateTime? VerifiedAtUtc);
// Default preserves old source-bearing provenance JSON; estimates do not need a Source.
public sealed record ProductProvenance(FoodSource? Source, string FoodState, string? CookingMethod,
    bool PreparationAnalog, string DataOrigin = "verified_reference");
public sealed record ReferenceFood(string FoodId, string Description, string DataType, string Url, Nutrients? Per100Grams);
public sealed record ReferenceFoodPage(IReadOnlyList<ReferenceFood> Foods);
public sealed record CreateEntryRequest(Guid Id, Guid ProductId, decimal QuantityGrams, string? Date, string? Time);
// Null ProductId keeps the historical snapshot; an explicit id replaces it with today's product.
public sealed record UpdateEntryRequest(decimal QuantityGrams, string? Date, string? Time, Guid? ProductId);
public sealed record ProductResponse(Guid Id, string Name, string? Brand, decimal Calories,
    decimal Protein, decimal Fat, decimal Carbohydrates, string? ExternalSource = null,
    string? ExternalId = null, string? Barcode = null, string? PackageQuantity = null,
    string? SourceUrl = null, string? NutritionSource = null, DateTimeOffset? RetrievedAtUtc = null,
    string? SourceFingerprint = null, ProductProvenance? Provenance = null);
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
    ProductProvenance? Provenance = null);
public sealed record DiaryResponse(string Date, IReadOnlyList<EntryResponse> Entries, Nutrients Totals);
public sealed record ProductPage(IReadOnlyList<ProductResponse> Products, bool HasMore);

public sealed class NutritionException(int status, string code, int? retryAfterSeconds = null) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

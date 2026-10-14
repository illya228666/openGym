using OpenGym.Nutrition.Services;
using System.Text.Json.Serialization;

namespace OpenGym.Nutrition.Api;

public sealed record ProductRequest(string? Name, string? Brand, decimal? Calories,
    decimal? Protein, decimal? Fat, decimal? Carbohydrates);
public sealed record CreateEntryRequest(Guid Id, Guid ProductId, decimal QuantityGrams, string? Date, string? Time);
// Null ProductId keeps the historical snapshot; an explicit id replaces it with today's product.
public sealed record UpdateEntryRequest(decimal QuantityGrams, string? Date, string? Time, Guid? ProductId);
public sealed record ProductResponse(Guid Id, string Name, string? Brand, decimal Calories,
    decimal Protein, decimal Fat, decimal Carbohydrates, string? ExternalSource = null,
    string? ExternalId = null, string? Barcode = null, string? PackageQuantity = null,
    string? SourceUrl = null, string? NutritionSource = null, DateTimeOffset? RetrievedAtUtc = null,
    string? SourceFingerprint = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ImportProductRequest(string? Source, string? ExternalId, string? Fingerprint);
public sealed record ExternalProductCandidate(string Source, string ExternalId, string Name,
    string? Brand, string? PackageQuantity, string SourceUrl);
public sealed record ExternalProductPage(IReadOnlyList<ExternalProductCandidate> Products, bool HasMore, int Page);
public sealed record ExternalProductResponse(string Source, string ExternalId, string Name, string? Brand,
    string? PackageQuantity, string SourceUrl, Nutrients? Per100Grams, string? NutritionSource,
    string? Fingerprint, DateTimeOffset RetrievedAtUtc, bool CanImport, IReadOnlyList<string> Issues);
public sealed record EntryResponse(Guid Id, Guid ProductId, string Name, string? Brand,
    decimal QuantityGrams, string Date, string? Time, Nutrients Per100Grams, Nutrients Nutrition);
public sealed record DiaryResponse(string Date, IReadOnlyList<EntryResponse> Entries, Nutrients Totals);
public sealed record ProductPage(IReadOnlyList<ProductResponse> Products, bool HasMore);

public sealed class NutritionException(int status, string code, int? retryAfterSeconds = null) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

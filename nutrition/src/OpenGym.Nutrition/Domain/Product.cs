namespace OpenGym.Nutrition.Domain;

/// <summary>A user's reusable food definition; nutritional values are per 100 grams.</summary>
public sealed class Product
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Brand { get; set; }
    public string SearchText { get; set; } = "";
    public string? BrandSearchText { get; set; }
    public decimal Calories { get; set; }
    public decimal Protein { get; set; }
    public decimal Fat { get; set; }
    public decimal Carbohydrates { get; set; }
    public bool IsDeleted { get; set; }
    public string? ExternalSource { get; set; }
    public string? ExternalId { get; set; }
    public string? Barcode { get; set; }
    public string? PackageQuantity { get; set; }
    public string? SourceUrl { get; set; }
    public string? NutritionSource { get; set; }
    public DateTimeOffset? RetrievedAtUtc { get; set; }
    public string? SourceFingerprint { get; set; }
    public string? CatalogKey { get; set; }
    public string? ProvenanceJson { get; set; }
}

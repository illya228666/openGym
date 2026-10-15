namespace OpenGym.Nutrition.Domain;

/// <summary>Retains every creation key, including aliases deduplicated to an existing product.</summary>
public sealed class ProductCreation
{
    public string UserId { get; set; } = "";
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public string Fingerprint { get; set; } = "";
}

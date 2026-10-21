namespace OpenGym.Nutrition.Domain;

/// <summary>A reusable, user-owned list of products and their per-ingredient quantities.</summary>
public sealed class PortionTemplate
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string CreationFingerprint { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public bool IsDeleted { get; set; }
    public List<PortionIngredient> Ingredients { get; set; } = [];
}

/// <summary>One ordered product and quantity in a saved portion template.</summary>
public sealed class PortionIngredient
{
    public string UserId { get; set; } = "";
    public Guid PortionId { get; set; }
    public int Position { get; set; }
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = "";
    public string? ProductBrand { get; set; }
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = "grams";
}

/// <summary>Idempotency record and stable group identity for one application of a template.</summary>
public sealed class PortionGroup
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateOnly Date { get; set; }
    public TimeOnly? Time { get; set; }
    public decimal Multiplier { get; set; }
    public string CreationFingerprint { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenGym.Nutrition.Api;
using OpenGym.Nutrition.Domain;

namespace OpenGym.Nutrition.Services;

public sealed partial class NutritionService
{
    private const int MaxPortionIngredients = 100;

    public async Task<PortionPage> Portions(string userId, CancellationToken ct)
    {
        var portions = await db.PortionTemplates.AsNoTracking().Include(p => p.Ingredients)
            .Where(p => p.UserId == userId && !p.IsDeleted).OrderBy(p => p.Name).ThenBy(p => p.Id).ToListAsync(ct);
        var productIds = portions.SelectMany(p => p.Ingredients).Select(i => i.ProductId).Distinct().ToArray();
        var products = await db.Products.AsNoTracking().Where(p => p.UserId == userId && productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);
        return new(portions.Select(p => PortionView(p, products)).ToArray());
    }

    public async Task<PortionResponse> Portion(string userId, Guid id, CancellationToken ct)
    {
        var portion = await FindPortion(userId, id, ct);
        var products = await db.Products.AsNoTracking().Where(p => p.UserId == userId
            && portion.Ingredients.Select(i => i.ProductId).Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        return PortionView(portion, products);
    }

    public async Task<(PortionResponse Portion, bool Created)> CreatePortion(string userId, CreatePortionRequest request, CancellationToken ct)
    {
        if (request.Id == Guid.Empty) throw Invalid();
        var name = NormalizePortionName(request.Name);
        var ingredients = NormalizePortionIngredients(request.Ingredients);
        var fingerprint = PortionFingerprint(name, ingredients);
        var existing = await db.PortionTemplates.AsNoTracking().Include(p => p.Ingredients)
            .SingleOrDefaultAsync(p => p.UserId == userId && p.Id == request.Id, ct);
        if (existing is not null)
        {
            if (existing.CreationFingerprint != fingerprint) throw new NutritionException(409, "idempotency_conflict");
            if (existing.IsDeleted) throw new NutritionException(410, "portion_deleted");
            return (await Portion(userId, existing.Id, ct), false);
        }

        var portion = new PortionTemplate { Id = request.Id, UserId = userId, Name = name,
            CreationFingerprint = fingerprint, CreatedAtUtc = DateTime.UtcNow };
        portion.Ingredients = await BuildIngredients(userId, portion.Id, ingredients, ct);
        db.PortionTemplates.Add(portion);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            db.ChangeTracker.Clear();
            existing = await db.PortionTemplates.AsNoTracking().Include(p => p.Ingredients)
                .SingleOrDefaultAsync(p => p.UserId == userId && p.Id == request.Id, ct);
            if (existing is null) throw;
            if (existing.CreationFingerprint != fingerprint) throw new NutritionException(409, "idempotency_conflict");
            if (existing.IsDeleted) throw new NutritionException(410, "portion_deleted");
            return (await Portion(userId, existing.Id, ct), false);
        }
        return (await Portion(userId, portion.Id, ct), true);
    }

    public async Task<PortionResponse> UpdatePortion(string userId, Guid id, UpdatePortionRequest request, CancellationToken ct)
    {
        var portion = await FindPortion(userId, id, ct);
        var name = NormalizePortionName(request.Name);
        var ingredients = NormalizePortionIngredients(request.Ingredients);
        var replacements = await BuildIngredients(userId, portion.Id, ingredients, ct);
        db.PortionIngredients.RemoveRange(portion.Ingredients);
        portion.Name = name;
        portion.Ingredients = replacements;
        await db.SaveChangesAsync(ct);
        return await Portion(userId, portion.Id, ct);
    }

    public async Task DeletePortion(string userId, Guid id, CancellationToken ct)
    {
        var portion = await db.PortionTemplates.SingleOrDefaultAsync(p => p.UserId == userId && p.Id == id, ct);
        if (portion is null) throw Missing();
        portion.IsDeleted = true;
        await db.SaveChangesAsync(ct);
    }

    public async Task<(PortionApplicationResponse Application, bool Created)> ApplyPortion(
        string userId, Guid portionId, PortionApplicationRequest request, CancellationToken ct)
    {
        if (portionId == Guid.Empty || request.Id == Guid.Empty) throw Invalid();
        ValidateQuantity(request.Multiplier);
        var date = ParseDate(request.Date);
        var time = ParseTime(request.Time);
        var fingerprint = ApplicationFingerprint(portionId, request.Multiplier, date, time);
        var existing = await db.PortionGroups.AsNoTracking()
            .SingleOrDefaultAsync(g => g.UserId == userId && g.Id == request.Id, ct);
        if (existing is not null) return (await ReplayApplication(userId, existing, fingerprint, ct), false);

        var portion = await FindPortion(userId, portionId, ct);
        var ingredients = portion.Ingredients.OrderBy(i => i.Position).ToArray();
        var products = new List<Product>(ingredients.Length);
        foreach (var ingredient in ingredients) products.Add(await FindProduct(userId, ingredient.ProductId, ct));

        var group = new PortionGroup { Id = request.Id, UserId = userId, Name = portion.Name, Date = date,
            Time = time, Multiplier = request.Multiplier, CreationFingerprint = fingerprint, CreatedAtUtc = DateTime.UtcNow };
        for (var index = 0; index < ingredients.Length; index++)
        {
            var ingredient = ingredients[index];
            var product = products[index];
            var quantity = ingredient.Quantity * request.Multiplier;
            ValidateQuantity(quantity);
            if (ingredient.Unit == "pieces" && product.GramsPerPiece is not > 0) throw Invalid();
            var entry = new DiaryEntry { Id = Guid.NewGuid(), UserId = userId, Date = date, Time = time,
                Quantity = quantity, Unit = ingredient.Unit, GramsPerPiece = product.GramsPerPiece,
                PortionGroupId = group.Id, PortionName = group.Name, CreatedAtUtc = DateTime.UtcNow,
                CreationFingerprint = fingerprint + ":" + index.ToString("D3", CultureInfo.InvariantCulture) };
            _ = EquivalentGrams(entry);
            Snapshot(entry, product);
            db.DiaryEntries.Add(entry);
        }
        db.PortionGroups.Add(group);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            db.ChangeTracker.Clear();
            existing = await db.PortionGroups.AsNoTracking()
                .SingleOrDefaultAsync(g => g.UserId == userId && g.Id == request.Id, ct);
            if (existing is null) throw;
            return (await ReplayApplication(userId, existing, fingerprint, ct), false);
        }
        return (await ReplayApplication(userId, group, fingerprint, ct), true);
    }

    private async Task<PortionApplicationResponse> ReplayApplication(string userId, PortionGroup group,
        string fingerprint, CancellationToken ct)
    {
        if (group.CreationFingerprint != fingerprint) throw new NutritionException(409, "idempotency_conflict");
        var entries = await db.DiaryEntries.AsNoTracking().Where(e => e.UserId == userId
                && e.PortionGroupId == group.Id && !e.IsDeleted)
            .OrderBy(e => e.CreatedAtUtc).ThenBy(e => e.CreationFingerprint).ThenBy(e => e.Id).ToListAsync(ct);
        foreach (var entry in entries) entry.PortionName = group.Name;
        return ApplicationView(group, entries.Select(EntryView).ToArray());
    }

    private static PortionApplicationResponse ApplicationView(PortionGroup group, IReadOnlyList<EntryResponse> entries) =>
        new(group.Id, group.Name, group.Multiplier, group.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            group.Time?.ToString("HH:mm", CultureInfo.InvariantCulture), entries,
            NutritionCalculator.Total(entries.Select(e => e.Nutrition)));

    private async Task<PortionTemplate> FindPortion(string userId, Guid id, CancellationToken ct) =>
        await db.PortionTemplates.Include(p => p.Ingredients)
            .SingleOrDefaultAsync(p => p.UserId == userId && p.Id == id && !p.IsDeleted, ct) ?? throw Missing();

    private async Task<List<PortionIngredient>> BuildIngredients(string userId, Guid portionId,
        IReadOnlyList<PortionIngredientRequest> inputs, CancellationToken ct)
    {
        var result = new List<PortionIngredient>(inputs.Count);
        for (var index = 0; index < inputs.Count; index++)
        {
            var input = inputs[index];
            var amount = ResolveAmount(null, input.Quantity, input.Unit);
            var product = await FindProduct(userId, input.ProductId, ct);
            if (amount.Unit == "pieces" && product.GramsPerPiece is not > 0) throw Invalid();
            result.Add(new PortionIngredient { UserId = userId, PortionId = portionId, Position = index,
                ProductId = product.Id, ProductName = product.Name, ProductBrand = product.Brand,
                Quantity = amount.Quantity, Unit = amount.Unit });
        }
        return result;
    }

    private static string NormalizePortionName(string? name)
    {
        var normalized = name?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > 200) throw Invalid();
        return normalized;
    }

    private static IReadOnlyList<PortionIngredientRequest> NormalizePortionIngredients(
        IReadOnlyList<PortionIngredientRequest>? ingredients)
    {
        if (ingredients is null || ingredients.Count is < 1 or > MaxPortionIngredients
            || ingredients.Any(i => i is null || i.ProductId == Guid.Empty)) throw Invalid();
        return ingredients;
    }

    private static string PortionFingerprint(string name, IReadOnlyList<PortionIngredientRequest> ingredients)
    {
        var payload = new StringBuilder(name);
        foreach (var ingredient in ingredients)
            payload.Append('|').Append(ingredient.ProductId.ToString("D")).Append('|')
                .Append(ingredient.Unit).Append('|').Append(ingredient.Quantity.ToString("G29", CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString())));
    }

    private static string ApplicationFingerprint(Guid portionId, decimal multiplier, DateOnly date, TimeOnly? time) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant(
            $"{portionId:D}|{multiplier:G29}|{date:yyyy-MM-dd}|{time:HH:mm}"))));

    private static PortionResponse PortionView(PortionTemplate portion, IReadOnlyDictionary<Guid, Product> products) =>
        new(portion.Id, portion.Name, portion.Ingredients.OrderBy(i => i.Position).Select(i =>
        {
            var available = products.TryGetValue(i.ProductId, out var product) && !product.IsDeleted;
            return new PortionIngredientResponse(i.ProductId,
                available ? product!.Name : i.ProductName, available ? product!.Brand : i.ProductBrand,
                i.Quantity, i.Unit, available ? product!.GramsPerPiece : null, available);
        }).ToArray());
}

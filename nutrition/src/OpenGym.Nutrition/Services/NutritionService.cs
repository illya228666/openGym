using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenGym.Nutrition.Api;
using OpenGym.Nutrition.Domain;
using OpenGym.Nutrition.Persistence;

namespace OpenGym.Nutrition.Services;

/// <summary>User-scoped operations shared by HTTP clients; no UI or gateway policy lives here.</summary>
public sealed class NutritionService(NutritionDbContext db)
{
    public async Task<ProductPage> Products(string userId, string? search, int offset, CancellationToken ct,
        string? brand = null, string? barcode = null, string? externalId = null, string? externalSource = null)
    {
        if (offset < 0 || search?.Length > 200 || brand?.Length > 100
            || (barcode is not null && !ProductSearch.ValidCode(barcode))
            || (externalId is not null && !ProductSearch.ValidCode(externalId))
            || (externalSource is not null && externalSource != OpenFoodFacts.Source)) throw Invalid();
        var query = db.Products.AsNoTracking().Where(p => p.UserId == userId && !p.IsDeleted);
        if (!string.IsNullOrWhiteSpace(search))
        {
            foreach (var term in ProductSearch.Normalize(search).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                query = query.Where(p => p.SearchText.Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(brand))
        {
            var term = ProductSearch.Normalize(brand);
            query = query.Where(p => p.BrandSearchText != null && p.BrandSearchText.Contains(term));
        }
        if (barcode is not null)
        {
            barcode = ProductSearch.CanonicalCode(barcode);
            query = query.Where(p => p.Barcode == barcode);
        }
        if (externalId is not null)
        {
            externalId = ProductSearch.CanonicalCode(externalId);
            query = query.Where(p => p.ExternalId == externalId);
        }
        if (externalSource is not null) query = query.Where(p => p.ExternalSource == externalSource);
        var rows = await query.OrderBy(p => p.SearchText).ThenBy(p => p.Id).Skip(offset).Take(51).ToListAsync(ct);
        return new(rows.Take(50).Select(ProductView).ToArray(), rows.Count > 50);
    }

    public async Task<ProductResponse> Product(string userId, Guid id, CancellationToken ct) =>
        ProductView(await FindProduct(userId, id, ct));

    public async Task<ProductResponse> SaveProduct(string userId, Guid? id, ProductRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        var brand = string.IsNullOrWhiteSpace(request.Brand) ? null : request.Brand.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200 || brand?.Length > 100
            || !ValidValue(request.Calories, 1000) || !ValidValue(request.Protein, 100)
            || !ValidValue(request.Fat, 100) || !ValidValue(request.Carbohydrates, 100)) throw Invalid();
        var product = id.HasValue ? await FindProduct(userId, id.Value, ct) : new Product { UserId = userId, Id = Guid.NewGuid() };
        product.Name = name;
        product.Brand = brand;
        product.SearchText = ProductSearch.Normalize(name + " " + brand + " " + product.PackageQuantity);
        product.BrandSearchText = ProductSearch.Normalize(brand ?? "");
        if (id.HasValue && (product.Calories != request.Calories || product.Protein != request.Protein
            || product.Fat != request.Fat || product.Carbohydrates != request.Carbohydrates))
            product.NutritionSource = "user";
        product.Calories = request.Calories!.Value;
        product.Protein = request.Protein!.Value;
        product.Fat = request.Fat!.Value;
        product.Carbohydrates = request.Carbohydrates!.Value;
        if (!id.HasValue) db.Products.Add(product);
        await db.SaveChangesAsync(ct);
        return ProductView(product);
    }

    public async Task<(ProductResponse Product, bool Created)> ImportProduct(string userId, ImportProductRequest request,
        OpenFoodFacts external, CancellationToken ct)
    {
        if (request.Source != OpenFoodFacts.Source || !ProductSearch.ValidCode(request.ExternalId)
            || request.Fingerprint is null || request.Fingerprint.Length != 64
            || !request.Fingerprint.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) throw Invalid();
        request = request with { ExternalId = ProductSearch.CanonicalCode(request.ExternalId!) };
        Task<Product?> Existing() => db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId
            && p.ExternalSource == request.Source && p.ExternalId == request.ExternalId, ct);
        static (ProductResponse, bool) Reuse(Product product) => product.IsDeleted
            ? throw new NutritionException(410, "product_deleted") : (ProductView(product), false);
        var existing = await Existing();
        if (existing is not null) return Reuse(existing);
        var food = await external.Product(request.ExternalId!, ct, refresh: true);
        if (!food.CanImport || food.Per100Grams is null) throw new NutritionException(422, "external_product_unverified");
        if (food.Fingerprint != request.Fingerprint) throw new NutritionException(409, "external_product_changed");
        var product = new Product { Id = Guid.NewGuid(), UserId = userId, Name = food.Name, Brand = food.Brand,
            SearchText = ProductSearch.Normalize(food.Name + " " + food.Brand + " " + food.PackageQuantity),
            BrandSearchText = ProductSearch.Normalize(food.Brand ?? ""),
            Calories = food.Per100Grams.Calories, Protein = food.Per100Grams.Protein,
            Fat = food.Per100Grams.Fat, Carbohydrates = food.Per100Grams.Carbohydrates,
            ExternalSource = food.Source, ExternalId = food.ExternalId, Barcode = food.ExternalId,
            PackageQuantity = food.PackageQuantity, SourceUrl = food.SourceUrl,
            NutritionSource = food.NutritionSource, SourceFingerprint = food.Fingerprint, RetrievedAtUtc = food.RetrievedAtUtc };
        db.Products.Add(product);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            db.ChangeTracker.Clear();
            existing = await Existing();
            if (existing is null) throw;
            return Reuse(existing);
        }
        return (ProductView(product), true);
    }

    public async Task DeleteProduct(string userId, Guid id, CancellationToken ct)
    {
        var product = await db.Products.SingleOrDefaultAsync(p => p.UserId == userId && p.Id == id, ct);
        if (product is null) throw Missing();
        product.IsDeleted = true;
        await db.SaveChangesAsync(ct);
    }

    public async Task<DiaryResponse> Diary(string userId, string? date, CancellationToken ct)
    {
        var day = ParseDate(date);
        var rows = await db.DiaryEntries.AsNoTracking()
            .Where(e => e.UserId == userId && e.Date == day && !e.IsDeleted).ToListAsync(ct);
        // Untimed records follow timed ones; insertion order breaks ties deterministically.
        var entries = rows.OrderBy(e => e.Time is null).ThenBy(e => e.Time)
            .ThenBy(e => e.CreatedAtUtc).ThenBy(e => e.Id).Select(EntryView).ToArray();
        return new(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), entries,
            NutritionCalculator.Total(entries.Select(e => e.Nutrition)));
    }

    public async Task<EntryResponse> Entry(string userId, Guid id, CancellationToken ct) =>
        EntryView(await FindEntry(userId, id, ct));

    public async Task<(EntryResponse Entry, bool Created)> CreateEntry(string userId, CreateEntryRequest request, CancellationToken ct)
    {
        if (request.Id == Guid.Empty || request.ProductId == Guid.Empty) throw Invalid();
        var date = ParseDate(request.Date);
        var time = ParseTime(request.Time);
        ValidateQuantity(request.QuantityGrams);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            FormattableString.Invariant($"{request.ProductId:D}|{request.QuantityGrams:G29}|{date:yyyy-MM-dd}|{time:HH:mm}"))));
        var existing = await db.DiaryEntries.AsNoTracking().SingleOrDefaultAsync(e => e.UserId == userId && e.Id == request.Id, ct);
        if (existing is not null) return Replay(existing, fingerprint);
        var product = await FindProduct(userId, request.ProductId, ct);
        var entry = new DiaryEntry { Id = request.Id, UserId = userId, QuantityGrams = request.QuantityGrams,
            Date = date, Time = time, CreatedAtUtc = DateTime.UtcNow, CreationFingerprint = fingerprint };
        Snapshot(entry, product);
        db.DiaryEntries.Add(entry);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            // A concurrent retry may win the unique key race; compare its original payload.
            db.ChangeTracker.Clear();
            existing = await db.DiaryEntries.AsNoTracking().SingleOrDefaultAsync(e => e.UserId == userId && e.Id == request.Id, ct);
            if (existing is null) throw;
            return Replay(existing, fingerprint);
        }
        return (EntryView(entry), true);
    }

    public async Task<EntryResponse> UpdateEntry(string userId, Guid id, UpdateEntryRequest request, CancellationToken ct)
    {
        var date = ParseDate(request.Date);
        var time = ParseTime(request.Time);
        ValidateQuantity(request.QuantityGrams);
        var entry = await FindEntry(userId, id, ct);
        if (request.ProductId.HasValue) Snapshot(entry, await FindProduct(userId, request.ProductId.Value, ct));
        entry.QuantityGrams = request.QuantityGrams;
        entry.Date = date;
        entry.Time = time;
        await db.SaveChangesAsync(ct);
        return EntryView(entry);
    }

    public async Task DeleteEntry(string userId, Guid id, CancellationToken ct)
    {
        var entry = await db.DiaryEntries.SingleOrDefaultAsync(e => e.UserId == userId && e.Id == id, ct);
        if (entry is null) throw Missing();
        entry.IsDeleted = true;
        await db.SaveChangesAsync(ct);
    }

    private async Task<Product> FindProduct(string userId, Guid id, CancellationToken ct) =>
        await db.Products.SingleOrDefaultAsync(p => p.UserId == userId && p.Id == id && !p.IsDeleted, ct) ?? throw Missing();

    private async Task<DiaryEntry> FindEntry(string userId, Guid id, CancellationToken ct) =>
        await db.DiaryEntries.SingleOrDefaultAsync(e => e.UserId == userId && e.Id == id && !e.IsDeleted, ct) ?? throw Missing();

    private static (EntryResponse, bool) Replay(DiaryEntry entry, string fingerprint)
    {
        if (entry.CreationFingerprint != fingerprint) throw new NutritionException(409, "idempotency_conflict");
        if (entry.IsDeleted) throw new NutritionException(410, "entry_deleted");
        return (EntryView(entry), false);
    }

    private static void Snapshot(DiaryEntry entry, Product product)
    {
        entry.ProductId = product.Id;
        entry.Name = product.Name;
        entry.Brand = product.Brand;
        entry.Calories = product.Calories;
        entry.Protein = product.Protein;
        entry.Fat = product.Fat;
        entry.Carbohydrates = product.Carbohydrates;
    }

    private static ProductResponse ProductView(Product p) => new(p.Id, p.Name, p.Brand, p.Calories, p.Protein, p.Fat, p.Carbohydrates,
        p.ExternalSource, p.ExternalId, p.Barcode, p.PackageQuantity, p.SourceUrl, p.NutritionSource, p.RetrievedAtUtc, p.SourceFingerprint);
    private static EntryResponse EntryView(DiaryEntry e)
    {
        var per100 = new Nutrients(e.Calories, e.Protein, e.Fat, e.Carbohydrates);
        return new(e.Id, e.ProductId, e.Name, e.Brand, e.QuantityGrams, e.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            e.Time?.ToString("HH:mm", CultureInfo.InvariantCulture), per100, NutritionCalculator.ForQuantity(per100, e.QuantityGrams));
    }

    private static bool ValidValue(decimal? value, decimal max) => value is >= 0 && value <= max && decimal.Round(value.Value, 3) == value;
    private static void ValidateQuantity(decimal value)
    {
        if (value <= 0 || value > 100000 || decimal.Round(value, 3) != value) throw Invalid();
    }
    private static DateOnly ParseDate(string? value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var result) ? result : throw Invalid();
    private static TimeOnly? ParseTime(string? value) => value is null or "" ? null
        : TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result) ? result : throw Invalid();
    private static NutritionException Invalid() => new(400, "invalid_input");
    private static NutritionException Missing() => new(404, "not_found");
}

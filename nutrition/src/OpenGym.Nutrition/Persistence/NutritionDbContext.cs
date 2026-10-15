using Microsoft.EntityFrameworkCore;
using OpenGym.Nutrition.Domain;

namespace OpenGym.Nutrition.Persistence;

public sealed class NutritionDbContext(DbContextOptions<NutritionDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<DiaryEntry> DiaryEntries => Set<DiaryEntry>();
    public DbSet<ProductCreation> ProductCreations => Set<ProductCreation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var products = modelBuilder.Entity<Product>();
        products.HasKey(p => new { p.UserId, p.Id });
        products.HasIndex(p => new { p.UserId, p.IsDeleted });
        // Includes tombstones: an import retry must never silently resurrect deleted food.
        products.HasIndex(p => new { p.UserId, p.ExternalSource, p.ExternalId }).IsUnique();
        products.HasIndex(p => new { p.UserId, p.CatalogKey }).IsUnique();
        products.Property(p => p.Name).HasMaxLength(200);
        products.Property(p => p.Brand).HasMaxLength(100);

        var entries = modelBuilder.Entity<DiaryEntry>();
        entries.HasKey(e => new { e.UserId, e.Id });
        entries.HasIndex(e => new { e.UserId, e.Date, e.IsDeleted });
        modelBuilder.Entity<ProductCreation>().HasKey(c => new { c.UserId, c.Id });
        // No foreign key: removing a product must not remove or rewrite its snapshots.
        // SQLite stores decimals as TEXT, preserving precision without a double conversion.
    }
}

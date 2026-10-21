using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenGym.Nutrition.Persistence.Migrations;

[DbContext(typeof(NutritionDbContext))]
[Migration("20261021120000_StandardPortions")]
public sealed class StandardPortions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PortionGroups",
            columns: table => new
            {
                UserId = table.Column<string>(type: "TEXT", nullable: false),
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                Time = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                Multiplier = table.Column<decimal>(type: "TEXT", nullable: false),
                CreationFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_PortionGroups", x => new { x.UserId, x.Id }));

        migrationBuilder.CreateTable(
            name: "PortionTemplates",
            columns: table => new
            {
                UserId = table.Column<string>(type: "TEXT", nullable: false),
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                CreationFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_PortionTemplates", x => new { x.UserId, x.Id }));

        migrationBuilder.CreateTable(
            name: "PortionIngredients",
            columns: table => new
            {
                UserId = table.Column<string>(type: "TEXT", nullable: false),
                PortionId = table.Column<Guid>(type: "TEXT", nullable: false),
                Position = table.Column<int>(type: "INTEGER", nullable: false),
                ProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                ProductName = table.Column<string>(type: "TEXT", nullable: false),
                ProductBrand = table.Column<string>(type: "TEXT", nullable: true),
                Quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                Unit = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PortionIngredients", x => new { x.UserId, x.PortionId, x.Position });
                table.ForeignKey("FK_PortionIngredients_PortionTemplates_UserId_PortionId",
                    x => new { x.UserId, x.PortionId }, "PortionTemplates", new[] { "UserId", "Id" }, onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex("IX_PortionTemplates_UserId_IsDeleted", "PortionTemplates", new[] { "UserId", "IsDeleted" });
        migrationBuilder.AddColumn<Guid>(name: "PortionGroupId", table: "DiaryEntries", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>(name: "PortionName", table: "DiaryEntries", type: "TEXT", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "PortionGroupId", table: "DiaryEntries");
        migrationBuilder.DropColumn(name: "PortionName", table: "DiaryEntries");
        migrationBuilder.DropTable(name: "PortionIngredients");
        migrationBuilder.DropTable(name: "PortionGroups");
        migrationBuilder.DropTable(name: "PortionTemplates");
    }
}

using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenGym.Nutrition.Persistence.Migrations;

[DbContext(typeof(NutritionDbContext))]
[Migration("20261021090000_PieceQuantities")]
public sealed class PieceQuantities : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "GramsPerPiece",
            table: "Products",
            type: "TEXT",
            nullable: true);

        migrationBuilder.RenameColumn(
            name: "QuantityGrams",
            table: "DiaryEntries",
            newName: "Quantity");

        migrationBuilder.AddColumn<string>(
            name: "Unit",
            table: "DiaryEntries",
            type: "TEXT",
            nullable: false,
            defaultValue: "grams");

        migrationBuilder.AddColumn<decimal>(
            name: "GramsPerPiece",
            table: "DiaryEntries",
            type: "TEXT",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "GramsPerPiece", table: "DiaryEntries");
        migrationBuilder.DropColumn(name: "Unit", table: "DiaryEntries");
        migrationBuilder.RenameColumn(name: "Quantity", table: "DiaryEntries", newName: "QuantityGrams");
        migrationBuilder.DropColumn(name: "GramsPerPiece", table: "Products");
    }
}

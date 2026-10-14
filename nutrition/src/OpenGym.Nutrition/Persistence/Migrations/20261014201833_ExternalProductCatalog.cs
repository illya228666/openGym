using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenGym.Nutrition.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExternalProductCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BrandSearchText",
                table: "Products",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Barcode",
                table: "Products",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "Products",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSource",
                table: "Products",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NutritionSource",
                table: "Products",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PackageQuantity",
                table: "Products",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetrievedAtUtc",
                table: "Products",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceFingerprint",
                table: "Products",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceUrl",
                table: "Products",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_UserId_ExternalSource_ExternalId",
                table: "Products",
                columns: new[] { "UserId", "ExternalSource", "ExternalId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Products_UserId_ExternalSource_ExternalId",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "BrandSearchText",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "Barcode",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ExternalSource",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "NutritionSource",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "PackageQuantity",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "RetrievedAtUtc",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "SourceFingerprint",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "SourceUrl",
                table: "Products");
        }
    }
}

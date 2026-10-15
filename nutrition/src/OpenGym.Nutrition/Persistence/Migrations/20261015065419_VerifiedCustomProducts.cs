using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenGym.Nutrition.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VerifiedCustomProducts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CatalogKey",
                table: "Products",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProvenanceJson",
                table: "Products",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProvenanceJson",
                table: "DiaryEntries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProductCreations",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductCreations", x => new { x.UserId, x.Id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Products_UserId_CatalogKey",
                table: "Products",
                columns: new[] { "UserId", "CatalogKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProductCreations");

            migrationBuilder.DropIndex(
                name: "IX_Products_UserId_CatalogKey",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "CatalogKey",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ProvenanceJson",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ProvenanceJson",
                table: "DiaryEntries");
        }
    }
}

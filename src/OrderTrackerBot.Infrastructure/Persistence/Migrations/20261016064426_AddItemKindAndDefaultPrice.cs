using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderTrackerBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddItemKindAndDefaultPrice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DefaultPriceGroup",
                table: "Products",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ItemKind",
                table: "Products",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultPriceGroup",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ItemKind",
                table: "Products");
        }
    }
}

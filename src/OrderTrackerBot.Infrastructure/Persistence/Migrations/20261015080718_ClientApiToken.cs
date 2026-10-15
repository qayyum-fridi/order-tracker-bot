using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderTrackerBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ClientApiToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClientApiTokenHash",
                table: "Sellers",
                type: "nvarchar(450)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sellers_ClientApiTokenHash",
                table: "Sellers",
                column: "ClientApiTokenHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sellers_ClientApiTokenHash",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "ClientApiTokenHash",
                table: "Sellers");
        }
    }
}

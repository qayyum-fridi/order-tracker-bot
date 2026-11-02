using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderTrackerBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUnplacedUtterances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UnplacedUtterances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SellerHash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MaskedText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Stage = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RouterIntent = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RouterConfidence = table.Column<double>(type: "float", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnplacedUtterances", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UnplacedUtterances_CreatedAt",
                table: "UnplacedUtterances",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UnplacedUtterances");
        }
    }
}

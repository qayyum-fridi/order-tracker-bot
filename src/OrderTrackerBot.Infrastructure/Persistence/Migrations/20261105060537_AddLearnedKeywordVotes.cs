using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderTrackerBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLearnedKeywordVotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LearnedKeywordVotes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Word = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    IntentId = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    SellerId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LearnedKeywordVotes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LearnedKeywordVotes_Word_IntentId_SellerId",
                table: "LearnedKeywordVotes",
                columns: new[] { "Word", "IntentId", "SellerId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LearnedKeywordVotes");
        }
    }
}

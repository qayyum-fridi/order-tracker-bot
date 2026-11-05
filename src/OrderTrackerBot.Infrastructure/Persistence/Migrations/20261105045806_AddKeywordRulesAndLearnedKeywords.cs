using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderTrackerBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddKeywordRulesAndLearnedKeywords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KeywordRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Word = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    IntentId = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Active = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KeywordRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LearnedKeywords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Word = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    IntentId = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Votes = table.Column<int>(type: "int", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LearnedKeywords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KeywordRules_Word_IntentId",
                table: "KeywordRules",
                columns: new[] { "Word", "IntentId" },
                unique: true,
                filter: "[IntentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LearnedKeywords_Word_IntentId",
                table: "LearnedKeywords",
                columns: new[] { "Word", "IntentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KeywordRules");

            migrationBuilder.DropTable(
                name: "LearnedKeywords");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderTrackerBot.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Stage 6: the PendingActions table only. Additive (expand-only): nothing reads or writes it until the clarification store is wired in.
    /// Deliberately excludes the earlier unmigrated model changes (Sellers.SalesMode, Products.Barcode, Discounts.Category, Categories,
    /// CounterReturns); those need their own reviewed migration.
    /// </summary>
    public partial class AddPendingActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PendingActions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SellerId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    ResolvedReferencesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MissingFieldsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AnswersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CurrentQuestion = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingActions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingActions_ExpiresAt",
                table: "PendingActions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_PendingActions_SellerId_UserId_Status",
                table: "PendingActions",
                columns: new[] { "SellerId", "UserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingActions");
        }
    }
}

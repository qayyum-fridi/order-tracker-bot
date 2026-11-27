using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderTrackerBot.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Stage 7 (batch 1): the ConfirmationRecords table for the Safety and Authorization gateway. Additive only. Does not include the earlier
    /// unmigrated model changes (Sellers.SalesMode, Products.Barcode, Discounts.Category, Categories, CounterReturns), which still need their own reviewed migration.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261127100000_AddConfirmationRecords")]
    public partial class AddConfirmationRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConfirmationRecords",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SellerId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    OrderId = table.Column<int>(type: "int", nullable: true),
                    Value = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    RecordVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfirmationRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConfirmationRecords_SellerId_UserId_Status",
                table: "ConfirmationRecords",
                columns: new[] { "SellerId", "UserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConfirmationRecords");
        }
    }
}

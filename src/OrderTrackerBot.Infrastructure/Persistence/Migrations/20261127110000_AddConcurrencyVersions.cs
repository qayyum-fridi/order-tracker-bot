using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderTrackerBot.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Stage 7 (batch 1, revised): concurrency version columns for Orders and ConfirmationRecords. Additive only, default 0. Does not include the
    /// earlier unmigrated model changes (Sellers.SalesMode, Products.Barcode, Discounts.Category, Categories, CounterReturns).
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261127110000_AddConcurrencyVersions")]
    public partial class AddConcurrencyVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Orders",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "ConfirmationRecords",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "Version", table: "Orders");
            migrationBuilder.DropColumn(name: "Version", table: "ConfirmationRecords");
        }
    }
}

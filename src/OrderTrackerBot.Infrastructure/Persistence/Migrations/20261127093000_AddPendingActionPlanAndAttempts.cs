using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderTrackerBot.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Stage 6: the original command (ActionJson) and the count of invalid replies (Attempts) on PendingActions.
    /// Additive only. Does not include the earlier unmigrated model changes (Sellers.SalesMode, Products.Barcode, Discounts.Category,
    /// Categories, CounterReturns), which still need their own reviewed migration.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261127093000_AddPendingActionPlanAndAttempts")]
    public partial class AddPendingActionPlanAndAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActionJson",
                table: "PendingActions",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                table: "PendingActions",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "ActionJson", table: "PendingActions");
            migrationBuilder.DropColumn(name: "Attempts", table: "PendingActions");
        }
    }
}

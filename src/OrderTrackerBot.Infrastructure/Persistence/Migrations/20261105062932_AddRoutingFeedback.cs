using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderTrackerBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRoutingFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CorrectedWithinHour",
                table: "Orders",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RoutedIntent",
                table: "Orders",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Counted",
                table: "LearnedKeywordVotes",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "OrderId",
                table: "LearnedKeywordVotes",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CorrectedWithinHour",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "RoutedIntent",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "Counted",
                table: "LearnedKeywordVotes");

            migrationBuilder.DropColumn(
                name: "OrderId",
                table: "LearnedKeywordVotes");
        }
    }
}

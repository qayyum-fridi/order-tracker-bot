using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderTrackerBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProcessedWebhookMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                table: "Sellers",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "ProcessedWebhookMessages",
                columns: table => new
                {
                    MessageId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedWebhookMessages", x => x.MessageId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedWebhookMessages_ProcessedAt",
                table: "ProcessedWebhookMessages",
                column: "ProcessedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProcessedWebhookMessages");

            migrationBuilder.DropColumn(
                name: "TimeZoneId",
                table: "Sellers");
        }
    }
}

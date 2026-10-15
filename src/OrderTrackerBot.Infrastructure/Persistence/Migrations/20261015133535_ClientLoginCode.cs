using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderTrackerBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ClientLoginCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClientLoginAttempts",
                table: "Sellers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClientLoginCodeExpiresAt",
                table: "Sellers",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientLoginCodeHash",
                table: "Sellers",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClientLoginCodeSentAt",
                table: "Sellers",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClientLoginAttempts",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "ClientLoginCodeExpiresAt",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "ClientLoginCodeHash",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "ClientLoginCodeSentAt",
                table: "Sellers");
        }
    }
}

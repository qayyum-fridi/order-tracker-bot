using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderTrackerBot.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Stage 9: the PipelineTraces table, one telemetry row per handled message. Additive only. Does not include the earlier unmigrated model
    /// changes (Sellers.SalesMode, Products.Barcode, Discounts.Category, Categories, CounterReturns).
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261127120000_AddPipelineTraces")]
    public partial class AddPipelineTraces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PipelineTraces",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TraceId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TotalMs = table.Column<int>(type: "int", nullable: false),
                    AiCalls = table.Column<int>(type: "int", nullable: false),
                    AiMs = table.Column<int>(type: "int", nullable: false),
                    ClarificationCount = table.Column<int>(type: "int", nullable: false),
                    SelectedAction = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Stage = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineTraces", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PipelineTraces_CreatedAt",
                table: "PipelineTraces",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PipelineTraces");
        }
    }
}

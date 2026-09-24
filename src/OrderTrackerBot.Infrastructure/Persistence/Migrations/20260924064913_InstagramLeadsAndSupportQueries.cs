using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderTrackerBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InstagramLeadsAndSupportQueries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CommentLeads",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SellerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    IgCommentId = table.Column<string>(type: "TEXT", nullable: false),
                    CommenterUsername = table.Column<string>(type: "TEXT", nullable: true),
                    CommentText = table.Column<string>(type: "TEXT", nullable: false),
                    PostId = table.Column<string>(type: "TEXT", nullable: true),
                    ClassifiedIntent = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    OrderId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommentLeads", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InstagramConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SellerId = table.Column<int>(type: "INTEGER", nullable: false),
                    IgUserId = table.Column<string>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: true),
                    PageId = table.Column<string>(type: "TEXT", nullable: true),
                    AccessToken = table.Column<string>(type: "TEXT", nullable: false),
                    ConnectedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TokenExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstagramConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InstagramConnections_Sellers_SellerId",
                        column: x => x.SellerId,
                        principalTable: "Sellers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SupportQueries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SellerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    CustomerName = table.Column<string>(type: "TEXT", nullable: true),
                    CustomerPhone = table.Column<string>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    QueryText = table.Column<string>(type: "TEXT", nullable: false),
                    SuggestedReply = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    LinkedOrderId = table.Column<int>(type: "INTEGER", nullable: true),
                    IgCommentId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupportQueries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CommentLeads_IgCommentId",
                table: "CommentLeads",
                column: "IgCommentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommentLeads_SellerId_Number",
                table: "CommentLeads",
                columns: new[] { "SellerId", "Number" });

            migrationBuilder.CreateIndex(
                name: "IX_InstagramConnections_IgUserId",
                table: "InstagramConnections",
                column: "IgUserId");

            migrationBuilder.CreateIndex(
                name: "IX_InstagramConnections_SellerId",
                table: "InstagramConnections",
                column: "SellerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupportQueries_SellerId_Number",
                table: "SupportQueries",
                columns: new[] { "SellerId", "Number" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CommentLeads");

            migrationBuilder.DropTable(
                name: "InstagramConnections");

            migrationBuilder.DropTable(
                name: "SupportQueries");
        }
    }
}

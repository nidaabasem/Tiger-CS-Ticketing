using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerSatisfactionReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TicketReviewInvitations",
                columns: table => new
                {
                    TicketReviewInvitationId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TicketId = table.Column<long>(type: "bigint", nullable: false),
                    ReopenCycle = table.Column<int>(type: "int", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    IsTest = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketReviewInvitations", x => x.TicketReviewInvitationId);
                    table.ForeignKey(
                        name: "FK_TicketReviewInvitations_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "TicketId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TicketReviews",
                columns: table => new
                {
                    TicketReviewId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TicketId = table.Column<long>(type: "bigint", nullable: false),
                    ReopenCycle = table.Column<int>(type: "int", nullable: false),
                    TicketReviewInvitationId = table.Column<long>(type: "bigint", nullable: false),
                    IsTest = table.Column<bool>(type: "bit", nullable: false),
                    Rating = table.Column<byte>(type: "tinyint", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SubmittedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketReviews", x => x.TicketReviewId);
                    table.CheckConstraint("CK_TicketReviews_Rating", "[Rating] BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "FK_TicketReviews_TicketReviewInvitations_TicketReviewInvitationId",
                        column: x => x.TicketReviewInvitationId,
                        principalTable: "TicketReviewInvitations",
                        principalColumn: "TicketReviewInvitationId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketReviews_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "TicketId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TicketReviewInvitations_TicketId_ReopenCycle",
                table: "TicketReviewInvitations",
                columns: new[] { "TicketId", "ReopenCycle" });

            migrationBuilder.CreateIndex(
                name: "UX_TicketReviewInvitations_TokenHash",
                table: "TicketReviewInvitations",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TicketReviews_IsTest_SubmittedAtUtc",
                table: "TicketReviews",
                columns: new[] { "IsTest", "SubmittedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketReviews_TicketReviewInvitationId",
                table: "TicketReviews",
                column: "TicketReviewInvitationId");

            migrationBuilder.CreateIndex(
                name: "UX_TicketReviews_TicketId_ReopenCycle_IsTest",
                table: "TicketReviews",
                columns: new[] { "TicketId", "ReopenCycle", "IsTest" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TicketReviews");

            migrationBuilder.DropTable(
                name: "TicketReviewInvitations");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChatbotInactivityAndCrmDocumentCopies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AwaitingCustomerReplyReportedByEmployeeId",
                table: "TicketInteractions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AwaitingCustomerReplySinceUtc",
                table: "TicketInteractions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "InactivityClosedAtUtc",
                table: "TicketInteractions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CrmDocumentDeliveryRequests",
                columns: table => new
                {
                    CrmDocumentDeliveryRequestId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CallerEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    VerificationSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentType = table.Column<byte>(type: "tinyint", nullable: false),
                    CrmRecordId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Channel = table.Column<byte>(type: "tinyint", nullable: false),
                    MaskedDestination = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    FailureCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrmDocumentDeliveryRequests", x => x.CrmDocumentDeliveryRequestId);
                    table.ForeignKey(
                        name: "FK_CrmDocumentDeliveryRequests_AspNetUsers_CallerEmployeeId",
                        column: x => x.CallerEmployeeId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TicketInteractions_AwaitingCustomerReplySinceUtc",
                table: "TicketInteractions",
                column: "AwaitingCustomerReplySinceUtc",
                filter: "[AwaitingCustomerReplySinceUtc] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CrmDocumentDeliveryRequests_SessionDocument",
                table: "CrmDocumentDeliveryRequests",
                columns: new[] { "VerificationSessionId", "DocumentType", "CrmRecordId", "Channel", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_CrmDocumentDeliveryRequests_CallerKey",
                table: "CrmDocumentDeliveryRequests",
                columns: new[] { "CallerEmployeeId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CrmDocumentDeliveryRequests");

            migrationBuilder.DropIndex(
                name: "IX_TicketInteractions_AwaitingCustomerReplySinceUtc",
                table: "TicketInteractions");

            migrationBuilder.DropColumn(
                name: "AwaitingCustomerReplyReportedByEmployeeId",
                table: "TicketInteractions");

            migrationBuilder.DropColumn(
                name: "AwaitingCustomerReplySinceUtc",
                table: "TicketInteractions");

            migrationBuilder.DropColumn(
                name: "InactivityClosedAtUtc",
                table: "TicketInteractions");
        }
    }
}

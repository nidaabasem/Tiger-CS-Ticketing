using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCollectionsReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CollectionsReminders",
                columns: table => new
                {
                    CollectionsReminderId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CrmCustomerId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CrmUnitId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Type = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CycleKey = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    DeduplicationKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    AmountIncludesFines = table.Column<bool>(type: "bit", nullable: false),
                    SourceAsOfUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DispatchAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    DispatchSourceAsOfUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    StatusReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ProviderReference = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Trigger = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    RequestedByEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeliveredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SuppressedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionsReminders", x => x.CollectionsReminderId);
                });

            migrationBuilder.CreateTable(
                name: "CollectionsReminderEvents",
                columns: table => new
                {
                    CollectionsReminderEventId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CollectionsReminderId = table.Column<long>(type: "bigint", nullable: false),
                    ExternalEventId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReportedByEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ResponseKind = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: true),
                    ConversationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CustomerPhone = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    PromisedPaymentDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PromisedAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    VerificationFollowUpRequired = table.Column<bool>(type: "bit", nullable: false),
                    HumanFollowUpRequired = table.Column<bool>(type: "bit", nullable: false),
                    TicketStatus = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    TicketId = table.Column<long>(type: "bigint", nullable: true),
                    TicketNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TicketLinkedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TicketAttempts = table.Column<int>(type: "int", nullable: false),
                    TicketLastError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionsReminderEvents", x => x.CollectionsReminderEventId);
                    table.ForeignKey(
                        name: "FK_CollectionsReminderEvents_CollectionsReminders_CollectionsReminderId",
                        column: x => x.CollectionsReminderId,
                        principalTable: "CollectionsReminders",
                        principalColumn: "CollectionsReminderId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CollectionsReminderEvents_TicketId",
                table: "CollectionsReminderEvents",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "UX_CollectionsReminderEvents_Reminder_EventId",
                table: "CollectionsReminderEvents",
                columns: new[] { "CollectionsReminderId", "ExternalEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CollectionsReminders_Customer_Created",
                table: "CollectionsReminders",
                columns: new[] { "CrmCustomerId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_CollectionsReminders_DeduplicationKey",
                table: "CollectionsReminders",
                column: "DeduplicationKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CollectionsReminderEvents");

            migrationBuilder.DropTable(
                name: "CollectionsReminders");
        }
    }
}

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
                    CrmCustomerId = table.Column<long>(type: "bigint", nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UnitId = table.Column<long>(type: "bigint", nullable: true),
                    Type = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    CycleKey = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    AmountBasis = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    InstalmentIds = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    SourceAsOfUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Language = table.Column<string>(type: "varchar(2)", unicode: false, maxLength: 2, nullable: false),
                    Trigger = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    RequestedByEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    RequestHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    QueuedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionsReminders", x => x.CollectionsReminderId);
                });

            migrationBuilder.CreateTable(
                name: "CollectionsReminderChannels",
                columns: table => new
                {
                    CollectionsReminderChannelId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CollectionsReminderId = table.Column<long>(type: "bigint", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    DeduplicationKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    StatusReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ProviderMessageId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    LastEventAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    DispatchAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    DispatchSourceAsOfUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionsReminderChannels", x => x.CollectionsReminderChannelId);
                    table.ForeignKey(
                        name: "FK_CollectionsReminderChannels_CollectionsReminders_CollectionsReminderId",
                        column: x => x.CollectionsReminderId,
                        principalTable: "CollectionsReminders",
                        principalColumn: "CollectionsReminderId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CollectionsReminderEvents",
                columns: table => new
                {
                    CollectionsReminderEventId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CollectionsReminderId = table.Column<long>(type: "bigint", nullable: false),
                    ExternalEventId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    RequestHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    Channel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    DeliveryStatus = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    ProviderMessageId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ConversationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReportedByEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CustomerResponded = table.Column<bool>(type: "bit", nullable: false),
                    CustomerIntent = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: true),
                    CustomerPhone = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    RequiresHumanFollowUp = table.Column<bool>(type: "bit", nullable: false),
                    VerificationFollowUpRequired = table.Column<bool>(type: "bit", nullable: false),
                    TicketResult = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
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
                name: "IX_CollectionsReminderChannels_CollectionsReminderId",
                table: "CollectionsReminderChannels",
                column: "CollectionsReminderId");

            migrationBuilder.CreateIndex(
                name: "UX_CollectionsReminderChannels_DeduplicationKey",
                table: "CollectionsReminderChannels",
                column: "DeduplicationKey",
                unique: true);

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
                name: "IX_CollectionsReminders_Customer_Queued",
                table: "CollectionsReminders",
                columns: new[] { "CrmCustomerId", "QueuedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_CollectionsReminders_IdempotencyKey",
                table: "CollectionsReminders",
                column: "IdempotencyKey",
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CollectionsReminderChannels");

            migrationBuilder.DropTable(
                name: "CollectionsReminderEvents");

            migrationBuilder.DropTable(
                name: "CollectionsReminders");
        }
    }
}

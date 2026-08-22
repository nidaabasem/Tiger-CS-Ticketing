using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Forward-only. Adds the two remaining tables of
    /// MVP-Data-Dictionary.md §2.21 and §2.23 — <c>Notifications</c> and
    /// <c>OutboxMessages</c> (ADR-0013/ADR-0014) — taking backlog S-04 from
    /// 25 to 27 of 27 entity groups in scope.
    ///
    /// <para>
    /// <b>Purely additive.</b> No previously merged migration is edited and
    /// no existing table, column, index or constraint is altered or dropped:
    /// every statement below is a CREATE. <c>Tickets.AcknowledgementSentAtUtc</c>
    /// — the column the acknowledgement path writes — already shipped with
    /// <c>20260820140000_AddTicketing</c> and needs no change here.
    /// </para>
    ///
    /// <para>
    /// <b>Every foreign key is <c>Restrict</c>/NO_ACTION</b>, matching
    /// MVP-ERD.md §2.21/§2.23: a notification and its Outbox message are
    /// permanent records of an attempted customer contact, and an idempotency
    /// record is the proof a logical event was already handled — cascading a
    /// delete through any of them would silently destroy the evidence that
    /// FR-NOT-05's operational queue and ADR-0018's audit trail depend on.
    /// </para>
    ///
    /// <para>
    /// Two of the four indexes are filtered, which the EF Core InMemory
    /// provider silently ignores — the real-SQL-Server migration-validation
    /// workflow asserts both, plus their exact filter expressions.
    /// </para>
    /// </summary>
    public partial class AddNotificationsAndOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    OutboxMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IdempotencyRecordId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.OutboxMessageId);
                    table.ForeignKey(
                        name: "FK_OutboxMessages_IdempotencyRecords_IdempotencyRecordId",
                        column: x => x.IdempotencyRecordId,
                        principalTable: "IdempotencyRecords",
                        principalColumn: "IdempotencyRecordId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    NotificationId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TicketId = table.Column<long>(type: "bigint", nullable: true),
                    NotificationType = table.Column<byte>(type: "tinyint", nullable: false),
                    RecipientEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecipientAddress = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    Channel = table.Column<byte>(type: "tinyint", nullable: false),
                    DeliveryStatus = table.Column<byte>(type: "tinyint", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OutboxMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.NotificationId);
                    table.ForeignKey(
                        name: "FK_Notifications_OutboxMessages_OutboxMessageId",
                        column: x => x.OutboxMessageId,
                        principalTable: "OutboxMessages",
                        principalColumn: "OutboxMessageId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Notifications_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "TicketId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_DeliveryStatus",
                table: "Notifications",
                column: "DeliveryStatus");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_TicketId",
                table: "Notifications",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "UX_Notifications_OutboxMessageId_NotificationType",
                table: "Notifications",
                columns: new[] { "OutboxMessageId", "NotificationType" },
                unique: true,
                filter: "[OutboxMessageId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_CorrelationId",
                table: "OutboxMessages",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_DeadLettered",
                table: "OutboxMessages",
                column: "Status",
                filter: "[Status] = 3");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_IdempotencyRecordId",
                table: "OutboxMessages",
                column: "IdempotencyRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Status_OccurredAtUtc",
                table: "OutboxMessages",
                columns: new[] { "Status", "OccurredAtUtc" },
                filter: "[Status] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "OutboxMessages");
        }
    }
}

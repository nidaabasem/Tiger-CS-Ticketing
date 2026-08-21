using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSlaAndEscalation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BusinessCalendars",
                columns: table => new
                {
                    BusinessCalendarId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    BusinessDayStartLocal = table.Column<TimeOnly>(type: "time", nullable: false),
                    BusinessDayEndLocal = table.Column<TimeOnly>(type: "time", nullable: false),
                    TimeZone = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    EffectiveFromUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessCalendars", x => x.BusinessCalendarId);
                    table.CheckConstraint("CK_BusinessCalendars_WindowOrder", "[BusinessDayEndLocal] > [BusinessDayStartLocal]");
                });

            migrationBuilder.CreateTable(
                name: "IdempotencyRecords",
                columns: table => new
                {
                    IdempotencyRecordId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    FirstSeenAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ResultReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdempotencyRecords", x => x.IdempotencyRecordId);
                });

            migrationBuilder.CreateTable(
                name: "SlaPolicies",
                columns: table => new
                {
                    PriorityId = table.Column<byte>(type: "tinyint", nullable: false),
                    FirstResponseTargetMinutes = table.Column<int>(type: "int", nullable: false),
                    ResolutionTargetMinutes = table.Column<int>(type: "int", nullable: false),
                    ClockBasis = table.Column<byte>(type: "tinyint", nullable: false),
                    WarningThresholdPercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    Level2ToGmWindowValue = table.Column<int>(type: "int", nullable: false),
                    Level2ToGmWindowUnit = table.Column<byte>(type: "tinyint", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlaPolicies", x => x.PriorityId);
                    table.CheckConstraint("CK_SlaPolicies_PositiveTargets", "[FirstResponseTargetMinutes] > 0 AND [ResolutionTargetMinutes] > 0");
                    table.ForeignKey(
                        name: "FK_SlaPolicies_Priorities_PriorityId",
                        column: x => x.PriorityId,
                        principalTable: "Priorities",
                        principalColumn: "PriorityId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TicketEscalations",
                columns: table => new
                {
                    TicketEscalationId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TicketId = table.Column<long>(type: "bigint", nullable: false),
                    Level = table.Column<byte>(type: "tinyint", nullable: false),
                    TriggerType = table.Column<byte>(type: "tinyint", nullable: false),
                    NotifiedRoles = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RaisedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RespondedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RespondingEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketEscalations", x => x.TicketEscalationId);
                    table.CheckConstraint("CK_TicketEscalations_Level4IsManualOnly", "([Level] = 4 AND [TriggerType] = 4) OR ([Level] <> 4 AND [TriggerType] <> 4)");
                    table.CheckConstraint("CK_TicketEscalations_LevelRange", "[Level] BETWEEN 1 AND 4");
                    table.ForeignKey(
                        name: "FK_TicketEscalations_Employees_RespondingEmployeeId",
                        column: x => x.RespondingEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketEscalations_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "TicketId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TicketSlaInstances",
                columns: table => new
                {
                    TicketSlaInstanceId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TicketId = table.Column<long>(type: "bigint", nullable: false),
                    PriorityId = table.Column<byte>(type: "tinyint", nullable: false),
                    PeriodStartAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PeriodEndAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FirstResponseDueAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ResolutionDueAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FirstResponseBreached = table.Column<bool>(type: "bit", nullable: false),
                    ResolutionBreached = table.Column<bool>(type: "bit", nullable: false),
                    ChangeReason = table.Column<byte>(type: "tinyint", nullable: false),
                    ApprovedByEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketSlaInstances", x => x.TicketSlaInstanceId);
                    table.CheckConstraint("CK_TicketSlaInstances_DowngradeRequiresApprover", "[ChangeReason] <> 3 OR [ApprovedByEmployeeId] IS NOT NULL");
                    table.CheckConstraint("CK_TicketSlaInstances_PeriodOrder", "[PeriodEndAtUtc] IS NULL OR [PeriodEndAtUtc] >= [PeriodStartAtUtc]");
                    table.ForeignKey(
                        name: "FK_TicketSlaInstances_Employees_ApprovedByEmployeeId",
                        column: x => x.ApprovedByEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketSlaInstances_Priorities_PriorityId",
                        column: x => x.PriorityId,
                        principalTable: "Priorities",
                        principalColumn: "PriorityId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketSlaInstances_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "TicketId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BusinessCalendarWorkingDays",
                columns: table => new
                {
                    BusinessCalendarWorkingDayId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BusinessCalendarId = table.Column<int>(type: "int", nullable: false),
                    DayOfWeek = table.Column<byte>(type: "tinyint", nullable: false),
                    IsWorkingDay = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessCalendarWorkingDays", x => x.BusinessCalendarWorkingDayId);
                    table.CheckConstraint("CK_BusinessCalendarWorkingDays_DayOfWeekRange", "[DayOfWeek] BETWEEN 0 AND 6");
                    table.ForeignKey(
                        name: "FK_BusinessCalendarWorkingDays_BusinessCalendars_BusinessCalendarId",
                        column: x => x.BusinessCalendarId,
                        principalTable: "BusinessCalendars",
                        principalColumn: "BusinessCalendarId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Holidays",
                columns: table => new
                {
                    HolidayId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BusinessCalendarId = table.Column<int>(type: "int", nullable: false),
                    HolidayDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    EnteredByEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConfirmedByEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConfirmedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Holidays", x => x.HolidayId);
                    table.ForeignKey(
                        name: "FK_Holidays_BusinessCalendars_BusinessCalendarId",
                        column: x => x.BusinessCalendarId,
                        principalTable: "BusinessCalendars",
                        principalColumn: "BusinessCalendarId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Holidays_Employees_ConfirmedByEmployeeId",
                        column: x => x.ConfirmedByEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Holidays_Employees_EnteredByEmployeeId",
                        column: x => x.EnteredByEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessCalendarWorkingDays_BusinessCalendarId_DayOfWeek",
                table: "BusinessCalendarWorkingDays",
                columns: new[] { "BusinessCalendarId", "DayOfWeek" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Holidays_BusinessCalendarId_HolidayDate",
                table: "Holidays",
                columns: new[] { "BusinessCalendarId", "HolidayDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Holidays_ConfirmedByEmployeeId",
                table: "Holidays",
                column: "ConfirmedByEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_Holidays_EnteredByEmployeeId",
                table: "Holidays",
                column: "EnteredByEmployeeId");

            migrationBuilder.CreateIndex(
                name: "UX_IdempotencyRecords_IdempotencyKey",
                table: "IdempotencyRecords",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TicketEscalations_RespondingEmployeeId",
                table: "TicketEscalations",
                column: "RespondingEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketEscalations_TicketRaisedAt",
                table: "TicketEscalations",
                columns: new[] { "TicketId", "RaisedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_TicketEscalations_OneAutoBreachPerTicket",
                table: "TicketEscalations",
                column: "TicketId",
                unique: true,
                filter: "[TriggerType] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_TicketSlaInstances_ApprovedByEmployeeId",
                table: "TicketSlaInstances",
                column: "ApprovedByEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketSlaInstances_FirstResponseSweep",
                table: "TicketSlaInstances",
                columns: new[] { "PeriodEndAtUtc", "FirstResponseBreached", "FirstResponseDueAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketSlaInstances_PriorityId",
                table: "TicketSlaInstances",
                column: "PriorityId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketSlaInstances_ResolutionSweep",
                table: "TicketSlaInstances",
                columns: new[] { "PeriodEndAtUtc", "ResolutionBreached", "ResolutionDueAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_TicketSlaInstances_CurrentPeriodPerTicket",
                table: "TicketSlaInstances",
                column: "TicketId",
                unique: true,
                filter: "[PeriodEndAtUtc] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BusinessCalendarWorkingDays");

            migrationBuilder.DropTable(
                name: "Holidays");

            migrationBuilder.DropTable(
                name: "IdempotencyRecords");

            migrationBuilder.DropTable(
                name: "SlaPolicies");

            migrationBuilder.DropTable(
                name: "TicketEscalations");

            migrationBuilder.DropTable(
                name: "TicketSlaInstances");

            migrationBuilder.DropTable(
                name: "BusinessCalendars");
        }
    }
}

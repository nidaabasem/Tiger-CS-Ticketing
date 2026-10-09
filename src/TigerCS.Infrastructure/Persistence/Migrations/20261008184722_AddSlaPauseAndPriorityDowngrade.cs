using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSlaPauseAndPriorityDowngrade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AppliedFirstResponseTargetMinutes",
                table: "TicketSlaInstances",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AppliedResolutionTargetMinutes",
                table: "TicketSlaInstances",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PausesOnPendingCustomerOverride",
                table: "TicketSlaInstances",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestTypeSlaNote",
                table: "TicketSlaInstances",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RequestTypeSlaPolicyId",
                table: "TicketSlaInstances",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "ResolutionClockBasis",
                table: "TicketSlaInstances",
                type: "tinyint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PriorityDowngradeRequests",
                columns: table => new
                {
                    PriorityDowngradeRequestId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TicketId = table.Column<long>(type: "bigint", nullable: false),
                    CurrentPriorityId = table.Column<byte>(type: "tinyint", nullable: false),
                    RequestedPriorityId = table.Column<byte>(type: "tinyint", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    RequestedByEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DecidedByEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecidedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriorityDowngradeRequests", x => x.PriorityDowngradeRequestId);
                    table.CheckConstraint("CK_PriorityDowngradeRequests_DecisionConsistent", "([Status] IN (2, 3) AND [DecidedByEmployeeId] IS NOT NULL AND [DecidedByEmployeeId] <> [RequestedByEmployeeId]) OR [Status] NOT IN (2, 3)");
                    table.CheckConstraint("CK_PriorityDowngradeRequests_IsDowngrade", "[RequestedPriorityId] > [CurrentPriorityId]");
                    table.ForeignKey(
                        name: "FK_PriorityDowngradeRequests_Employees_DecidedByEmployeeId",
                        column: x => x.DecidedByEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PriorityDowngradeRequests_Employees_RequestedByEmployeeId",
                        column: x => x.RequestedByEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PriorityDowngradeRequests_Priorities_CurrentPriorityId",
                        column: x => x.CurrentPriorityId,
                        principalTable: "Priorities",
                        principalColumn: "PriorityId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PriorityDowngradeRequests_Priorities_RequestedPriorityId",
                        column: x => x.RequestedPriorityId,
                        principalTable: "Priorities",
                        principalColumn: "PriorityId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PriorityDowngradeRequests_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "TicketId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TicketSlaPausePeriods",
                columns: table => new
                {
                    TicketSlaPausePeriodId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TicketId = table.Column<long>(type: "bigint", nullable: false),
                    TicketSlaInstanceId = table.Column<long>(type: "bigint", nullable: false),
                    Reason = table.Column<byte>(type: "tinyint", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ResumedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolutionDueBeforeAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ResolutionDueAfterAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EndedByResolution = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketSlaPausePeriods", x => x.TicketSlaPausePeriodId);
                    table.CheckConstraint("CK_TicketSlaPausePeriods_Order", "[ResumedAtUtc] IS NULL OR [ResumedAtUtc] >= [StartedAtUtc]");
                    table.ForeignKey(
                        name: "FK_TicketSlaPausePeriods_TicketSlaInstances_TicketSlaInstanceId",
                        column: x => x.TicketSlaInstanceId,
                        principalTable: "TicketSlaInstances",
                        principalColumn: "TicketSlaInstanceId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketSlaPausePeriods_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "TicketId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PriorityDowngradeRequests_CurrentPriorityId",
                table: "PriorityDowngradeRequests",
                column: "CurrentPriorityId");

            migrationBuilder.CreateIndex(
                name: "IX_PriorityDowngradeRequests_DecidedByEmployeeId",
                table: "PriorityDowngradeRequests",
                column: "DecidedByEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_PriorityDowngradeRequests_RequestedByEmployeeId",
                table: "PriorityDowngradeRequests",
                column: "RequestedByEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_PriorityDowngradeRequests_RequestedPriorityId",
                table: "PriorityDowngradeRequests",
                column: "RequestedPriorityId");

            migrationBuilder.CreateIndex(
                name: "IX_PriorityDowngradeRequests_StatusExpiry",
                table: "PriorityDowngradeRequests",
                columns: new[] { "Status", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_PriorityDowngradeRequests_OnePendingPerTicket",
                table: "PriorityDowngradeRequests",
                column: "TicketId",
                unique: true,
                filter: "[Status] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_TicketSlaPausePeriods_Instance",
                table: "TicketSlaPausePeriods",
                column: "TicketSlaInstanceId");

            migrationBuilder.CreateIndex(
                name: "UX_TicketSlaPausePeriods_OneOpenPerTicket",
                table: "TicketSlaPausePeriods",
                column: "TicketId",
                unique: true,
                filter: "[ResumedAtUtc] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PriorityDowngradeRequests");

            migrationBuilder.DropTable(
                name: "TicketSlaPausePeriods");

            migrationBuilder.DropColumn(
                name: "AppliedFirstResponseTargetMinutes",
                table: "TicketSlaInstances");

            migrationBuilder.DropColumn(
                name: "AppliedResolutionTargetMinutes",
                table: "TicketSlaInstances");

            migrationBuilder.DropColumn(
                name: "PausesOnPendingCustomerOverride",
                table: "TicketSlaInstances");

            migrationBuilder.DropColumn(
                name: "RequestTypeSlaNote",
                table: "TicketSlaInstances");

            migrationBuilder.DropColumn(
                name: "RequestTypeSlaPolicyId",
                table: "TicketSlaInstances");

            migrationBuilder.DropColumn(
                name: "ResolutionClockBasis",
                table: "TicketSlaInstances");
        }
    }
}

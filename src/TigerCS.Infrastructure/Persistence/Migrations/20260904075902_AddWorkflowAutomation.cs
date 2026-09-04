using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowAutomation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RequestTypeId",
                table: "Tickets",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupervisorRoleName",
                table: "DepartmentWorkflowSettings",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                // Existing rows (the Phase 1 provisional settings) get the
                // same provisional default the domain uses: Department Head —
                // the department-scoped supervisory authority that exists in
                // the fixed role set today. Never an empty string, which no
                // role validates against.
                defaultValue: "Department Head");

            migrationBuilder.CreateTable(
                name: "RequestTypeAssignmentRules",
                columns: table => new
                {
                    RequestTypeAssignmentRuleId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestTypeId = table.Column<int>(type: "int", nullable: false),
                    Mode = table.Column<byte>(type: "tinyint", nullable: false),
                    PrimaryEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TeamName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestTypeAssignmentRules", x => x.RequestTypeAssignmentRuleId);
                    table.ForeignKey(
                        name: "FK_RequestTypeAssignmentRules_Employees_PrimaryEmployeeId",
                        column: x => x.PrimaryEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequestTypeAssignmentRules_RequestTypes_RequestTypeId",
                        column: x => x.RequestTypeId,
                        principalTable: "RequestTypes",
                        principalColumn: "RequestTypeId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TicketInteractionContexts",
                columns: table => new
                {
                    TicketId = table.Column<long>(type: "bigint", nullable: false),
                    Source = table.Column<byte>(type: "tinyint", nullable: false),
                    ChannelId = table.Column<byte>(type: "tinyint", nullable: false),
                    CustomerPhone = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CalledNumber = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    GenesysConversationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    GenesysQueueId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    GenesysQueueName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    GenesysAgentId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    GenesysAgentName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    InteractionStartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Direction = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketInteractionContexts", x => x.TicketId);
                    table.ForeignKey(
                        name: "FK_TicketInteractionContexts_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "TicketId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TicketPendingRecords",
                columns: table => new
                {
                    TicketPendingRecordId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TicketId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    PreviousStatus = table.Column<byte>(type: "tinyint", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedByEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResumedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResumedByEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketPendingRecords", x => x.TicketPendingRecordId);
                    table.ForeignKey(
                        name: "FK_TicketPendingRecords_Employees_ResumedByEmployeeId",
                        column: x => x.ResumedByEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketPendingRecords_Employees_StartedByEmployeeId",
                        column: x => x.StartedByEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketPendingRecords_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "TicketId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RequestTypeAssignmentRuleMembers",
                columns: table => new
                {
                    RequestTypeAssignmentRuleMemberId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestTypeAssignmentRuleId = table.Column<int>(type: "int", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestTypeAssignmentRuleMembers", x => x.RequestTypeAssignmentRuleMemberId);
                    table.ForeignKey(
                        name: "FK_RequestTypeAssignmentRuleMembers_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequestTypeAssignmentRuleMembers_RequestTypeAssignmentRules_RequestTypeAssignmentRuleId",
                        column: x => x.RequestTypeAssignmentRuleId,
                        principalTable: "RequestTypeAssignmentRules",
                        principalColumn: "RequestTypeAssignmentRuleId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_RequestTypeId",
                table: "Tickets",
                column: "RequestTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestTypeAssignmentRuleMembers_EmployeeId",
                table: "RequestTypeAssignmentRuleMembers",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestTypeAssignmentRuleMembers_RequestTypeAssignmentRuleId_EmployeeId",
                table: "RequestTypeAssignmentRuleMembers",
                columns: new[] { "RequestTypeAssignmentRuleId", "EmployeeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RequestTypeAssignmentRules_PrimaryEmployeeId",
                table: "RequestTypeAssignmentRules",
                column: "PrimaryEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestTypeAssignmentRules_RequestTypeId",
                table: "RequestTypeAssignmentRules",
                column: "RequestTypeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TicketInteractionContexts_GenesysConversationId",
                table: "TicketInteractionContexts",
                column: "GenesysConversationId",
                filter: "[GenesysConversationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TicketPendingRecords_ResumedByEmployeeId",
                table: "TicketPendingRecords",
                column: "ResumedByEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketPendingRecords_StartedByEmployeeId",
                table: "TicketPendingRecords",
                column: "StartedByEmployeeId");

            migrationBuilder.CreateIndex(
                name: "UX_TicketPendingRecords_OpenPerTicket",
                table: "TicketPendingRecords",
                column: "TicketId",
                unique: true,
                filter: "[ResumedAtUtc] IS NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Tickets_RequestTypes_RequestTypeId",
                table: "Tickets",
                column: "RequestTypeId",
                principalTable: "RequestTypes",
                principalColumn: "RequestTypeId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tickets_RequestTypes_RequestTypeId",
                table: "Tickets");

            migrationBuilder.DropTable(
                name: "RequestTypeAssignmentRuleMembers");

            migrationBuilder.DropTable(
                name: "TicketInteractionContexts");

            migrationBuilder.DropTable(
                name: "TicketPendingRecords");

            migrationBuilder.DropTable(
                name: "RequestTypeAssignmentRules");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_RequestTypeId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "RequestTypeId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "SupervisorRoleName",
                table: "DepartmentWorkflowSettings");
        }
    }
}

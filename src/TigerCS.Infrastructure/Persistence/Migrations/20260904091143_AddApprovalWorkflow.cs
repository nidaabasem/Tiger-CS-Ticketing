using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddApprovalWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RequestTypeApprovalRequirements",
                columns: table => new
                {
                    RequestTypeApprovalRequirementId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestTypeId = table.Column<int>(type: "int", nullable: false),
                    ApprovalType = table.Column<byte>(type: "tinyint", nullable: false),
                    TargetKind = table.Column<byte>(type: "tinyint", nullable: false),
                    TargetDepartmentId = table.Column<int>(type: "int", nullable: true),
                    TargetRoleName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TargetEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BlocksWorkUntilApproved = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestTypeApprovalRequirements", x => x.RequestTypeApprovalRequirementId);
                    table.ForeignKey(
                        name: "FK_RequestTypeApprovalRequirements_Departments_TargetDepartmentId",
                        column: x => x.TargetDepartmentId,
                        principalTable: "Departments",
                        principalColumn: "DepartmentId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequestTypeApprovalRequirements_Employees_TargetEmployeeId",
                        column: x => x.TargetEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequestTypeApprovalRequirements_RequestTypes_RequestTypeId",
                        column: x => x.RequestTypeId,
                        principalTable: "RequestTypes",
                        principalColumn: "RequestTypeId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TicketApprovals",
                columns: table => new
                {
                    TicketApprovalId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TicketId = table.Column<long>(type: "bigint", nullable: false),
                    ApprovalType = table.Column<byte>(type: "tinyint", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    TargetKind = table.Column<byte>(type: "tinyint", nullable: false),
                    TargetDepartmentId = table.Column<int>(type: "int", nullable: true),
                    TargetRoleName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TargetEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RequestedByEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RequestComment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DecidedByEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecisionAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DecisionComment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsCurrent = table.Column<bool>(type: "bit", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketApprovals", x => x.TicketApprovalId);
                    table.ForeignKey(
                        name: "FK_TicketApprovals_Departments_TargetDepartmentId",
                        column: x => x.TargetDepartmentId,
                        principalTable: "Departments",
                        principalColumn: "DepartmentId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketApprovals_Employees_DecidedByEmployeeId",
                        column: x => x.DecidedByEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketApprovals_Employees_RequestedByEmployeeId",
                        column: x => x.RequestedByEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketApprovals_Employees_TargetEmployeeId",
                        column: x => x.TargetEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketApprovals_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "TicketId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TicketWorkflowEvents",
                columns: table => new
                {
                    TicketWorkflowEventId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TicketId = table.Column<long>(type: "bigint", nullable: false),
                    EventType = table.Column<byte>(type: "tinyint", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ActorEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TicketApprovalId = table.Column<long>(type: "bigint", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketWorkflowEvents", x => x.TicketWorkflowEventId);
                    table.ForeignKey(
                        name: "FK_TicketWorkflowEvents_Employees_ActorEmployeeId",
                        column: x => x.ActorEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketWorkflowEvents_TicketApprovals_TicketApprovalId",
                        column: x => x.TicketApprovalId,
                        principalTable: "TicketApprovals",
                        principalColumn: "TicketApprovalId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketWorkflowEvents_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "TicketId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RequestTypeApprovalRequirements_RequestTypeId_ApprovalType",
                table: "RequestTypeApprovalRequirements",
                columns: new[] { "RequestTypeId", "ApprovalType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RequestTypeApprovalRequirements_TargetDepartmentId",
                table: "RequestTypeApprovalRequirements",
                column: "TargetDepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestTypeApprovalRequirements_TargetEmployeeId",
                table: "RequestTypeApprovalRequirements",
                column: "TargetEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketApprovals_DecidedByEmployeeId",
                table: "TicketApprovals",
                column: "DecidedByEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketApprovals_RequestedByEmployeeId",
                table: "TicketApprovals",
                column: "RequestedByEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketApprovals_TargetDepartmentId",
                table: "TicketApprovals",
                column: "TargetDepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketApprovals_TargetEmployeeId",
                table: "TicketApprovals",
                column: "TargetEmployeeId");

            migrationBuilder.CreateIndex(
                name: "UX_TicketApprovals_OneCurrentPerType",
                table: "TicketApprovals",
                columns: new[] { "TicketId", "ApprovalType" },
                unique: true,
                filter: "[IsCurrent] = 1");

            migrationBuilder.CreateIndex(
                name: "UX_TicketApprovals_OnePendingPerType",
                table: "TicketApprovals",
                columns: new[] { "TicketId", "ApprovalType" },
                unique: true,
                filter: "[Status] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_TicketWorkflowEvents_ActorEmployeeId",
                table: "TicketWorkflowEvents",
                column: "ActorEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketWorkflowEvents_TicketApprovalId",
                table: "TicketWorkflowEvents",
                column: "TicketApprovalId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketWorkflowEvents_TicketId_EventType",
                table: "TicketWorkflowEvents",
                columns: new[] { "TicketId", "EventType" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RequestTypeApprovalRequirements");

            migrationBuilder.DropTable(
                name: "TicketWorkflowEvents");

            migrationBuilder.DropTable(
                name: "TicketApprovals");
        }
    }
}

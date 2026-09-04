using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DepartmentWorkflowSettings",
                columns: table => new
                {
                    DepartmentId = table.Column<int>(type: "int", nullable: false),
                    AllowAssignment = table.Column<bool>(type: "bit", nullable: false),
                    AllowInternalReassignment = table.Column<bool>(type: "bit", nullable: false),
                    AllowTransferToOtherDepartments = table.Column<bool>(type: "bit", nullable: false),
                    HeadRoleName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DepartmentWorkflowSettings", x => x.DepartmentId);
                    table.ForeignKey(
                        name: "FK_DepartmentWorkflowSettings_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "DepartmentId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowTemplates",
                columns: table => new
                {
                    WorkflowTemplateId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AllowsPendingCustomer = table.Column<bool>(type: "bit", nullable: false),
                    AllowsPendingInternal = table.Column<bool>(type: "bit", nullable: false),
                    RequiresApproval = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowTemplates", x => x.WorkflowTemplateId);
                });

            migrationBuilder.CreateTable(
                name: "RequestTypes",
                columns: table => new
                {
                    RequestTypeId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DepartmentId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    WorkflowTemplateId = table.Column<int>(type: "int", nullable: false),
                    DefaultPriorityId = table.Column<byte>(type: "tinyint", nullable: false),
                    AllowAgentPriorityChange = table.Column<bool>(type: "bit", nullable: false),
                    AllowPendingCustomer = table.Column<bool>(type: "bit", nullable: false),
                    AllowPendingInternal = table.Column<bool>(type: "bit", nullable: false),
                    AllowReopen = table.Column<bool>(type: "bit", nullable: false),
                    RequiredFieldsJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestTypes", x => x.RequestTypeId);
                    table.ForeignKey(
                        name: "FK_RequestTypes_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "DepartmentId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequestTypes_Priorities_DefaultPriorityId",
                        column: x => x.DefaultPriorityId,
                        principalTable: "Priorities",
                        principalColumn: "PriorityId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequestTypes_WorkflowTemplates_WorkflowTemplateId",
                        column: x => x.WorkflowTemplateId,
                        principalTable: "WorkflowTemplates",
                        principalColumn: "WorkflowTemplateId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowTemplateSteps",
                columns: table => new
                {
                    WorkflowTemplateStepId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkflowTemplateId = table.Column<int>(type: "int", nullable: false),
                    Sequence = table.Column<byte>(type: "tinyint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    IsOptional = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowTemplateSteps", x => x.WorkflowTemplateStepId);
                    table.ForeignKey(
                        name: "FK_WorkflowTemplateSteps_WorkflowTemplates_WorkflowTemplateId",
                        column: x => x.WorkflowTemplateId,
                        principalTable: "WorkflowTemplates",
                        principalColumn: "WorkflowTemplateId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RequestTypeSlaPolicies",
                columns: table => new
                {
                    RequestTypeSlaPolicyId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestTypeId = table.Column<int>(type: "int", nullable: false),
                    PriorityId = table.Column<byte>(type: "tinyint", nullable: false),
                    Trigger = table.Column<byte>(type: "tinyint", nullable: false),
                    Unit = table.Column<byte>(type: "tinyint", nullable: false),
                    FirstResponseTargetValue = table.Column<int>(type: "int", nullable: true),
                    FirstResponseMaximumValue = table.Column<int>(type: "int", nullable: true),
                    ResolutionTargetValue = table.Column<int>(type: "int", nullable: true),
                    ResolutionMaximumValue = table.Column<int>(type: "int", nullable: true),
                    IsImmediate = table.Column<bool>(type: "bit", nullable: false),
                    ClockBasis = table.Column<byte>(type: "tinyint", nullable: true),
                    PausesOnPendingCustomer = table.Column<bool>(type: "bit", nullable: true),
                    PausesOnPendingInternal = table.Column<bool>(type: "bit", nullable: true),
                    WarningThresholdPercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestTypeSlaPolicies", x => x.RequestTypeSlaPolicyId);
                    table.ForeignKey(
                        name: "FK_RequestTypeSlaPolicies_Priorities_PriorityId",
                        column: x => x.PriorityId,
                        principalTable: "Priorities",
                        principalColumn: "PriorityId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequestTypeSlaPolicies_RequestTypes_RequestTypeId",
                        column: x => x.RequestTypeId,
                        principalTable: "RequestTypes",
                        principalColumn: "RequestTypeId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RequestTypes_DefaultPriorityId",
                table: "RequestTypes",
                column: "DefaultPriorityId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestTypes_DepartmentId_Name",
                table: "RequestTypes",
                columns: new[] { "DepartmentId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RequestTypes_WorkflowTemplateId",
                table: "RequestTypes",
                column: "WorkflowTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestTypeSlaPolicies_PriorityId",
                table: "RequestTypeSlaPolicies",
                column: "PriorityId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestTypeSlaPolicies_RequestTypeId_PriorityId",
                table: "RequestTypeSlaPolicies",
                columns: new[] { "RequestTypeId", "PriorityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTemplates_Code",
                table: "WorkflowTemplates",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTemplateSteps_WorkflowTemplateId_Sequence",
                table: "WorkflowTemplateSteps",
                columns: new[] { "WorkflowTemplateId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DepartmentWorkflowSettings");

            migrationBuilder.DropTable(
                name: "RequestTypeSlaPolicies");

            migrationBuilder.DropTable(
                name: "WorkflowTemplateSteps");

            migrationBuilder.DropTable(
                name: "RequestTypes");

            migrationBuilder.DropTable(
                name: "WorkflowTemplates");
        }
    }
}

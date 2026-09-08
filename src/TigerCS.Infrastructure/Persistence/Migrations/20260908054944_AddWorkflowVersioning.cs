using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowVersioning : Migration
    {
        /// <inheritdoc />
        /// <summary>
        /// Administration / Workflow Designer phase — introduces workflow
        /// versioning WITHOUT destroying any existing configuration or ticket
        /// data. Order matters and is documented step by step:
        ///
        /// <list type="number">
        ///   <item>Create <c>Workflows</c> (logical workflows) and insert one
        ///   row per existing <c>WorkflowTemplates</c> row (same Code/Name/
        ///   Description/IsActive).</item>
        ///   <item>Add the version columns to <c>WorkflowTemplates</c> as
        ///   NULLABLE, backfill every existing template as <b>version 1,
        ///   Published</b> of its same-code workflow (created/published by the
        ///   system — no actor), then tighten the columns to NOT NULL. No
        ///   lingering DEFAULT constraints.</item>
        ///   <item><c>RequestTypes.WorkflowTemplateId</c> becomes
        ///   <c>RequestTypes.WorkflowId</c>: the column is renamed, and its
        ///   values are REMAPPED from template id to logical workflow id
        ///   (a rename alone would leave template ids in a workflow-id
        ///   column).</item>
        ///   <item><c>Tickets.WorkflowTemplateId</c> (nullable) is backfilled
        ///   for tickets that already carry a RequestTypeId with the exact
        ///   template that governed them (pre-versioning there was only one
        ///   per request type) — factual, not fabricated. Tickets without a
        ///   request type stay NULL.</item>
        ///   <item>Step configuration (<c>ApprovalType</c>), the
        ///   <c>WorkflowStepTransitions</c> table, indexes and foreign keys.</item>
        /// </list>
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---- 1. Logical workflows --------------------------------------------
            migrationBuilder.CreateTable(
                name: "Workflows",
                columns: table => new
                {
                    WorkflowId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workflows", x => x.WorkflowId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Workflows_Code",
                table: "Workflows",
                column: "Code",
                unique: true);

            migrationBuilder.Sql(
      """
    INSERT INTO [Workflows] ([Code], [Name], [Description], [IsActive], [CreatedAtUtc])
    SELECT [Code], [Name], [Description], [IsActive], SYSUTCDATETIME()
    FROM [WorkflowTemplates];
    """);

            // ---- 2. Templates become versions -------------------------------------
            migrationBuilder.AddColumn<int>(
                name: "WorkflowId",
                table: "WorkflowTemplates",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VersionNumber",
                table: "WorkflowTemplates",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "Status",
                table: "WorkflowTemplates",
                type: "tinyint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAtUtc",
                table: "WorkflowTemplates",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByEmployeeId",
                table: "WorkflowTemplates",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PublishedAtUtc",
                table: "WorkflowTemplates",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PublishedByEmployeeId",
                table: "WorkflowTemplates",
                type: "uniqueidentifier",
                nullable: true);

            // Every pre-versioning template is version 1, Published (Status 2),
            // of the workflow that shares its Code. System-published: no actor.
            migrationBuilder.Sql(
                """
                UPDATE t
                SET t.[WorkflowId] = w.[WorkflowId],
                    t.[VersionNumber] = 1,
                    t.[Status] = 2,
                    t.[CreatedAtUtc] = SYSUTCDATETIME(),
                    t.[PublishedAtUtc] = SYSUTCDATETIME()
                FROM [WorkflowTemplates] t
                INNER JOIN [Workflows] w ON w.[Code] = t.[Code];
                """);

            migrationBuilder.AlterColumn<int>(
                name: "WorkflowId",
                table: "WorkflowTemplates",
                type: "int",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "VersionNumber",
                table: "WorkflowTemplates",
                type: "int",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AlterColumn<byte>(
                name: "Status",
                table: "WorkflowTemplates",
                type: "tinyint",
                nullable: false,
                oldClrType: typeof(byte),
                oldType: "tinyint",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAtUtc",
                table: "WorkflowTemplates",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldNullable: true);

            // ---- 3. RequestTypes: template id -> logical workflow id -----------------
            migrationBuilder.DropForeignKey(
                name: "FK_RequestTypes_WorkflowTemplates_WorkflowTemplateId",
                table: "RequestTypes");

            // ---- 4. Tickets: pin legacy tickets to the template that governed them --
            // Done BEFORE the RequestTypes remap below, while
            // RequestTypes.WorkflowTemplateId still holds the template id.
            migrationBuilder.AddColumn<int>(
                name: "WorkflowTemplateId",
                table: "Tickets",
                type: "int",
                nullable: true);

            migrationBuilder.Sql(
                """
    EXEC(N'
        UPDATE tk
        SET tk.[WorkflowTemplateId] = rt.[WorkflowTemplateId]
        FROM [Tickets] tk
        INNER JOIN [RequestTypes] rt
            ON rt.[RequestTypeId] = tk.[RequestTypeId]
        WHERE tk.[RequestTypeId] IS NOT NULL
          AND tk.[WorkflowTemplateId] IS NULL;
    ');
    """);

            migrationBuilder.RenameColumn(
                name: "WorkflowTemplateId",
                table: "RequestTypes",
                newName: "WorkflowId");

            migrationBuilder.RenameIndex(
                name: "IX_RequestTypes_WorkflowTemplateId",
                table: "RequestTypes",
                newName: "IX_RequestTypes_WorkflowId");

            // The renamed column still holds template ids: remap each to the
            // logical workflow of that template (the join reads the OLD value).
            migrationBuilder.Sql(
                """
                UPDATE rt
                SET rt.[WorkflowId] = t.[WorkflowId]
                FROM [RequestTypes] rt
                INNER JOIN [WorkflowTemplates] t ON t.[WorkflowTemplateId] = rt.[WorkflowId];
                """);

            // ---- 5. Step configuration, transitions, indexes, foreign keys ----------
            migrationBuilder.DropIndex(
                name: "IX_WorkflowTemplateSteps_WorkflowTemplateId_Sequence",
                table: "WorkflowTemplateSteps");

            migrationBuilder.AddColumn<byte>(
                name: "ApprovalType",
                table: "WorkflowTemplateSteps",
                type: "tinyint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WorkflowStepTransitions",
                columns: table => new
                {
                    WorkflowStepTransitionId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkflowTemplateStepId = table.Column<int>(type: "int", nullable: false),
                    Outcome = table.Column<byte>(type: "tinyint", nullable: false),
                    TargetWorkflowTemplateStepId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowStepTransitions", x => x.WorkflowStepTransitionId);
                    table.ForeignKey(
                        name: "FK_WorkflowStepTransitions_WorkflowTemplateSteps_TargetWorkflowTemplateStepId",
                        column: x => x.TargetWorkflowTemplateStepId,
                        principalTable: "WorkflowTemplateSteps",
                        principalColumn: "WorkflowTemplateStepId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkflowStepTransitions_WorkflowTemplateSteps_WorkflowTemplateStepId",
                        column: x => x.WorkflowTemplateStepId,
                        principalTable: "WorkflowTemplateSteps",
                        principalColumn: "WorkflowTemplateStepId",
                        onDelete: ReferentialAction.Cascade);
                });

            // Non-unique now: the designer reorders a Draft by swapping two
            // sequences in one save. Uniqueness is enforced by the aggregate.
            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTemplateSteps_WorkflowTemplateId_Sequence",
                table: "WorkflowTemplateSteps",
                columns: new[] { "WorkflowTemplateId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTemplates_CreatedByEmployeeId",
                table: "WorkflowTemplates",
                column: "CreatedByEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTemplates_PublishedByEmployeeId",
                table: "WorkflowTemplates",
                column: "PublishedByEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTemplates_WorkflowId_VersionNumber",
                table: "WorkflowTemplates",
                columns: new[] { "WorkflowId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_WorkflowTemplates_OneDraftPerWorkflow",
                table: "WorkflowTemplates",
                column: "WorkflowId",
                unique: true,
                filter: "[Status] = 1");

            migrationBuilder.CreateIndex(
                name: "UX_WorkflowTemplates_OnePublishedPerWorkflow",
                table: "WorkflowTemplates",
                column: "WorkflowId",
                unique: true,
                filter: "[Status] = 2");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_WorkflowTemplateId",
                table: "Tickets",
                column: "WorkflowTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowStepTransitions_TargetWorkflowTemplateStepId",
                table: "WorkflowStepTransitions",
                column: "TargetWorkflowTemplateStepId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowStepTransitions_WorkflowTemplateStepId_Outcome",
                table: "WorkflowStepTransitions",
                columns: new[] { "WorkflowTemplateStepId", "Outcome" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_RequestTypes_Workflows_WorkflowId",
                table: "RequestTypes",
                column: "WorkflowId",
                principalTable: "Workflows",
                principalColumn: "WorkflowId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Tickets_WorkflowTemplates_WorkflowTemplateId",
                table: "Tickets",
                column: "WorkflowTemplateId",
                principalTable: "WorkflowTemplates",
                principalColumn: "WorkflowTemplateId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkflowTemplates_Employees_CreatedByEmployeeId",
                table: "WorkflowTemplates",
                column: "CreatedByEmployeeId",
                principalTable: "Employees",
                principalColumn: "EmployeeId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkflowTemplates_Employees_PublishedByEmployeeId",
                table: "WorkflowTemplates",
                column: "PublishedByEmployeeId",
                principalTable: "Employees",
                principalColumn: "EmployeeId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkflowTemplates_Workflows_WorkflowId",
                table: "WorkflowTemplates",
                column: "WorkflowId",
                principalTable: "Workflows",
                principalColumn: "WorkflowId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RequestTypes_Workflows_WorkflowId",
                table: "RequestTypes");

            migrationBuilder.DropForeignKey(
                name: "FK_Tickets_WorkflowTemplates_WorkflowTemplateId",
                table: "Tickets");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkflowTemplates_Employees_CreatedByEmployeeId",
                table: "WorkflowTemplates");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkflowTemplates_Employees_PublishedByEmployeeId",
                table: "WorkflowTemplates");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkflowTemplates_Workflows_WorkflowId",
                table: "WorkflowTemplates");

            migrationBuilder.DropTable(
                name: "Workflows");

            migrationBuilder.DropTable(
                name: "WorkflowStepTransitions");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowTemplateSteps_WorkflowTemplateId_Sequence",
                table: "WorkflowTemplateSteps");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowTemplates_CreatedByEmployeeId",
                table: "WorkflowTemplates");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowTemplates_PublishedByEmployeeId",
                table: "WorkflowTemplates");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowTemplates_WorkflowId_VersionNumber",
                table: "WorkflowTemplates");

            migrationBuilder.DropIndex(
                name: "UX_WorkflowTemplates_OneDraftPerWorkflow",
                table: "WorkflowTemplates");

            migrationBuilder.DropIndex(
                name: "UX_WorkflowTemplates_OnePublishedPerWorkflow",
                table: "WorkflowTemplates");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_WorkflowTemplateId",
                table: "Tickets");

            // Reverse remap: point each request type back at a template id —
            // the workflow's Published version if any, else its version 1.
            // Runs while WorkflowTemplates still carries WorkflowId/Status.
            migrationBuilder.Sql(
                """
                UPDATE rt
                SET rt.[WorkflowId] = COALESCE(
                    (SELECT TOP 1 t.[WorkflowTemplateId] FROM [WorkflowTemplates] t
                     WHERE t.[WorkflowId] = rt.[WorkflowId] AND t.[Status] = 2),
                    (SELECT TOP 1 t.[WorkflowTemplateId] FROM [WorkflowTemplates] t
                     WHERE t.[WorkflowId] = rt.[WorkflowId] ORDER BY t.[VersionNumber]))
                FROM [RequestTypes] rt;
                """);

            migrationBuilder.RenameColumn(
                name: "WorkflowId",
                table: "RequestTypes",
                newName: "WorkflowTemplateId");

            migrationBuilder.RenameIndex(
                name: "IX_RequestTypes_WorkflowId",
                table: "RequestTypes",
                newName: "IX_RequestTypes_WorkflowTemplateId");

            migrationBuilder.DropColumn(
                name: "ApprovalType",
                table: "WorkflowTemplateSteps");

            migrationBuilder.DropColumn(
                name: "CreatedAtUtc",
                table: "WorkflowTemplates");

            migrationBuilder.DropColumn(
                name: "CreatedByEmployeeId",
                table: "WorkflowTemplates");

            migrationBuilder.DropColumn(
                name: "PublishedAtUtc",
                table: "WorkflowTemplates");

            migrationBuilder.DropColumn(
                name: "PublishedByEmployeeId",
                table: "WorkflowTemplates");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "WorkflowTemplates");

            migrationBuilder.DropColumn(
                name: "VersionNumber",
                table: "WorkflowTemplates");

            migrationBuilder.DropColumn(
                name: "WorkflowId",
                table: "WorkflowTemplates");

            migrationBuilder.DropColumn(
                name: "WorkflowTemplateId",
                table: "Tickets");


            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTemplateSteps_WorkflowTemplateId_Sequence",
                table: "WorkflowTemplateSteps",
                columns: new[] { "WorkflowTemplateId", "Sequence" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_RequestTypes_WorkflowTemplates_WorkflowTemplateId",
                table: "RequestTypes",
                column: "WorkflowTemplateId",
                principalTable: "WorkflowTemplates",
                principalColumn: "WorkflowTemplateId",
                onDelete: ReferentialAction.Restrict);
        }
    }
}

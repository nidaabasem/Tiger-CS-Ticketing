using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConfiguredRuntimeEnforcement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CurrentWorkflowStepId",
                table: "Tickets",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ConfigurationEnforced",
                table: "RequestTypes",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "RequestTypeCatalogDecisions",
                columns: table => new
                {
                    RequestTypeCatalogDecisionId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestTypeId = table.Column<int>(type: "int", nullable: false),
                    Area = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Question = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolvedByEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Resolution = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestTypeCatalogDecisions", x => x.RequestTypeCatalogDecisionId);
                    table.ForeignKey(
                        name: "FK_RequestTypeCatalogDecisions_RequestTypes_RequestTypeId",
                        column: x => x.RequestTypeId,
                        principalTable: "RequestTypes",
                        principalColumn: "RequestTypeId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_CurrentWorkflowStepId",
                table: "Tickets",
                column: "CurrentWorkflowStepId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestTypeCatalogDecisions_RequestTypeId",
                table: "RequestTypeCatalogDecisions",
                column: "RequestTypeId");

            migrationBuilder.AddForeignKey(
                name: "FK_Tickets_WorkflowTemplateSteps_CurrentWorkflowStepId",
                table: "Tickets",
                column: "CurrentWorkflowStepId",
                principalTable: "WorkflowTemplateSteps",
                principalColumn: "WorkflowTemplateStepId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tickets_WorkflowTemplateSteps_CurrentWorkflowStepId",
                table: "Tickets");

            migrationBuilder.DropTable(
                name: "RequestTypeCatalogDecisions");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_CurrentWorkflowStepId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "CurrentWorkflowStepId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ConfigurationEnforced",
                table: "RequestTypes");
        }
    }
}

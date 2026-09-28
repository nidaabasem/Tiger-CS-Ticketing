using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRequestTypeCatalogImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DepartmentId",
                table: "WorkflowTemplateSteps",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "FirstResponseUnit",
                table: "RequestTypeSlaPolicies",
                type: "tinyint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Code",
                table: "RequestTypes",
                type: "nvarchar(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "RequestTypes",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestGroup",
                table: "RequestTypes",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequiredDocumentsJson",
                table: "RequestTypes",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTemplateSteps_DepartmentId",
                table: "WorkflowTemplateSteps",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestTypes_Code",
                table: "RequestTypes",
                column: "Code",
                unique: true,
                filter: "[Code] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_WorkflowTemplateSteps_Departments_DepartmentId",
                table: "WorkflowTemplateSteps",
                column: "DepartmentId",
                principalTable: "Departments",
                principalColumn: "DepartmentId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkflowTemplateSteps_Departments_DepartmentId",
                table: "WorkflowTemplateSteps");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowTemplateSteps_DepartmentId",
                table: "WorkflowTemplateSteps");

            migrationBuilder.DropIndex(
                name: "IX_RequestTypes_Code",
                table: "RequestTypes");

            migrationBuilder.DropColumn(
                name: "DepartmentId",
                table: "WorkflowTemplateSteps");

            migrationBuilder.DropColumn(
                name: "FirstResponseUnit",
                table: "RequestTypeSlaPolicies");

            migrationBuilder.DropColumn(
                name: "Code",
                table: "RequestTypes");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "RequestTypes");

            migrationBuilder.DropColumn(
                name: "RequestGroup",
                table: "RequestTypes");

            migrationBuilder.DropColumn(
                name: "RequiredDocumentsJson",
                table: "RequestTypes");
        }
    }
}

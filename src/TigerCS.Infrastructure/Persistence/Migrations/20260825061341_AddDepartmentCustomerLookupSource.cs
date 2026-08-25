using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDepartmentCustomerLookupSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DepartmentId",
                table: "IntakeRecords",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DepartmentCustomerLookupSources",
                columns: table => new
                {
                    DepartmentCustomerLookupSourceId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DepartmentId = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<byte>(type: "tinyint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DepartmentCustomerLookupSources", x => x.DepartmentCustomerLookupSourceId);
                    table.ForeignKey(
                        name: "FK_DepartmentCustomerLookupSources_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "DepartmentId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IntakeRecords_DepartmentId",
                table: "IntakeRecords",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_DepartmentCustomerLookupSources_DepartmentId_Source",
                table: "DepartmentCustomerLookupSources",
                columns: new[] { "DepartmentId", "Source" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_IntakeRecords_Departments_DepartmentId",
                table: "IntakeRecords",
                column: "DepartmentId",
                principalTable: "Departments",
                principalColumn: "DepartmentId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_IntakeRecords_Departments_DepartmentId",
                table: "IntakeRecords");

            migrationBuilder.DropTable(
                name: "DepartmentCustomerLookupSources");

            migrationBuilder.DropIndex(
                name: "IX_IntakeRecords_DepartmentId",
                table: "IntakeRecords");

            migrationBuilder.DropColumn(
                name: "DepartmentId",
                table: "IntakeRecords");
        }
    }
}

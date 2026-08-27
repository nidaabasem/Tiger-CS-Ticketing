using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCrmBuyerLookupToTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CrmBuyerCustomerId",
                table: "Tickets",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CrmBuyerCustomerName",
                table: "Tickets",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CrmBuyerLeadId",
                table: "Tickets",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CrmBuyerProjectId",
                table: "Tickets",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CrmBuyerProjectName",
                table: "Tickets",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CrmBuyerUnitId",
                table: "Tickets",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CrmBuyerUnitNumber",
                table: "Tickets",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManualProjectName",
                table: "Tickets",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManualUnitNumber",
                table: "Tickets",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CrmBuyerCustomerId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "CrmBuyerCustomerName",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "CrmBuyerLeadId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "CrmBuyerProjectId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "CrmBuyerProjectName",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "CrmBuyerUnitId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "CrmBuyerUnitNumber",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ManualProjectName",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ManualUnitNumber",
                table: "Tickets");
        }
    }
}

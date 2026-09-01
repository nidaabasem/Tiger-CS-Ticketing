using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalCustomerVerificationToTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CustomerVerificationSource",
                table: "Tickets",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalCustomerId",
                table: "Tickets",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalUnitId",
                table: "Tickets",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_CustomerVerificationSource_ExternalCustomerId",
                table: "Tickets",
                columns: new[] { "CustomerVerificationSource", "ExternalCustomerId" },
                filter: "[ExternalCustomerId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tickets_CustomerVerificationSource_ExternalCustomerId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "CustomerVerificationSource",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ExternalCustomerId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ExternalUnitId",
                table: "Tickets");
        }
    }
}

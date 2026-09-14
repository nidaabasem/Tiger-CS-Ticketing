using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalCustomerSnapshotToTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalCustomerEmail",
                table: "Tickets",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalCustomerName",
                table: "Tickets",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExternalCustomerEmail",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ExternalCustomerName",
                table: "Tickets");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLastCustomerInteractionToTicketInteractions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastCustomerInteractionAtUtc",
                table: "TicketInteractions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LastCustomerInteractionReportedByEmployeeId",
                table: "TicketInteractions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TicketInteractions_LastCustomerInteractionAtUtc",
                table: "TicketInteractions",
                column: "LastCustomerInteractionAtUtc",
                filter: "[LastCustomerInteractionAtUtc] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TicketInteractions_LastCustomerInteractionAtUtc",
                table: "TicketInteractions");

            migrationBuilder.DropColumn(
                name: "LastCustomerInteractionAtUtc",
                table: "TicketInteractions");

            migrationBuilder.DropColumn(
                name: "LastCustomerInteractionReportedByEmployeeId",
                table: "TicketInteractions");
        }
    }
}

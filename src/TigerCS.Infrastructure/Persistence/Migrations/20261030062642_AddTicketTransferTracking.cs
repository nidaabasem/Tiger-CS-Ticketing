using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketTransferTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FromDepartmentId",
                table: "TicketWorkflowEvents",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ToDepartmentId",
                table: "TicketWorkflowEvents",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ToEmployeeId",
                table: "TicketWorkflowEvents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "TransferReasonCode",
                table: "TicketWorkflowEvents",
                type: "tinyint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TicketWorkflowEvents_EventType_TransferReasonCode",
                table: "TicketWorkflowEvents",
                columns: new[] { "EventType", "TransferReasonCode" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TicketWorkflowEvents_EventType_TransferReasonCode",
                table: "TicketWorkflowEvents");

            migrationBuilder.DropColumn(
                name: "FromDepartmentId",
                table: "TicketWorkflowEvents");

            migrationBuilder.DropColumn(
                name: "ToDepartmentId",
                table: "TicketWorkflowEvents");

            migrationBuilder.DropColumn(
                name: "ToEmployeeId",
                table: "TicketWorkflowEvents");

            migrationBuilder.DropColumn(
                name: "TransferReasonCode",
                table: "TicketWorkflowEvents");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGenesysAgentMappingAndInteractionOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GenesysAgentUserId",
                table: "TicketInteractions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "HandledByUserId",
                table: "TicketInteractions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GenesysEmail",
                table: "AspNetUsers",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GenesysUserId",
                table: "AspNetUsers",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TicketInteractions_HandledByUserId",
                table: "TicketInteractions",
                column: "HandledByUserId");

            migrationBuilder.CreateIndex(
                name: "UX_AspNetUsers_GenesysUserId",
                table: "AspNetUsers",
                column: "GenesysUserId",
                unique: true,
                filter: "[GenesysUserId] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_TicketInteractions_AspNetUsers_HandledByUserId",
                table: "TicketInteractions",
                column: "HandledByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TicketInteractions_AspNetUsers_HandledByUserId",
                table: "TicketInteractions");

            migrationBuilder.DropIndex(
                name: "IX_TicketInteractions_HandledByUserId",
                table: "TicketInteractions");

            migrationBuilder.DropIndex(
                name: "UX_AspNetUsers_GenesysUserId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "GenesysAgentUserId",
                table: "TicketInteractions");

            migrationBuilder.DropColumn(
                name: "HandledByUserId",
                table: "TicketInteractions");

            migrationBuilder.DropColumn(
                name: "GenesysEmail",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "GenesysUserId",
                table: "AspNetUsers");
        }
    }
}

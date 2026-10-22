using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TigerCS.Infrastructure.Persistence;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Several tickets under one Genesys conversation: adds the caller's service request id and the "human follow-up required" flag to
    /// <c>TicketInteractions</c>, and widens the unique conversation index to (conversation, service request). Existing rows carry a NULL
    /// service request id, so every existing conversation keeps its single-interaction guarantee.
    /// </summary>
    [DbContext(typeof(TigerCsDbContext))]
    [Migration("20261022090000_AddGenesysServiceRequestToTicketInteractions")]
    public partial class AddGenesysServiceRequestToTicketInteractions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "GenesysServiceRequestId", table: "TicketInteractions", type: "nvarchar(64)", maxLength: 64, nullable: true);
            migrationBuilder.AddColumn<bool>(name: "RequiresHumanFollowUp", table: "TicketInteractions", type: "bit", nullable: false, defaultValue: false);

            migrationBuilder.DropIndex(name: "UX_TicketInteractions_GenesysConversationId", table: "TicketInteractions");

            migrationBuilder.CreateIndex(
                name: "IX_TicketInteractions_GenesysConversationId",
                table: "TicketInteractions",
                column: "GenesysConversationId",
                filter: "[GenesysConversationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_TicketInteractions_GenesysConversationId_ServiceRequestId",
                table: "TicketInteractions",
                columns: new[] { "GenesysConversationId", "GenesysServiceRequestId" },
                unique: true,
                filter: "[GenesysConversationId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restoring the single-interaction-per-conversation index fails if several service-request tickets already share a conversation;
            // those rows must be reconciled by hand first (the failure is the safe outcome — nothing is dropped silently).
            migrationBuilder.DropIndex(name: "UX_TicketInteractions_GenesysConversationId_ServiceRequestId", table: "TicketInteractions");
            migrationBuilder.DropIndex(name: "IX_TicketInteractions_GenesysConversationId", table: "TicketInteractions");

            migrationBuilder.CreateIndex(
                name: "UX_TicketInteractions_GenesysConversationId",
                table: "TicketInteractions",
                column: "GenesysConversationId",
                unique: true,
                filter: "[GenesysConversationId] IS NOT NULL");

            migrationBuilder.DropColumn(name: "RequiresHumanFollowUp", table: "TicketInteractions");
            migrationBuilder.DropColumn(name: "GenesysServiceRequestId", table: "TicketInteractions");
        }
    }
}

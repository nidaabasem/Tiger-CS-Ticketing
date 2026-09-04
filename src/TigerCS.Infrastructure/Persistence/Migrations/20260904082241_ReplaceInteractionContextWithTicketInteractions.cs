using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceInteractionContextWithTicketInteractions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TicketInteractions",
                columns: table => new
                {
                    TicketInteractionId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TicketId = table.Column<long>(type: "bigint", nullable: false),
                    IsOriginatingInteraction = table.Column<bool>(type: "bit", nullable: false),
                    Source = table.Column<byte>(type: "tinyint", nullable: false),
                    ChannelId = table.Column<byte>(type: "tinyint", nullable: false),
                    CustomerPhone = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CalledNumber = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    GenesysConversationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    GenesysQueueId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    GenesysQueueName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    GenesysAgentId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    GenesysAgentName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    InteractionStartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Direction = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketInteractions", x => x.TicketInteractionId);
                    table.ForeignKey(
                        name: "FK_TicketInteractions_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "TicketId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TicketInteractions_GenesysConversationId",
                table: "TicketInteractions",
                column: "GenesysConversationId",
                filter: "[GenesysConversationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TicketInteractions_TicketId",
                table: "TicketInteractions",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "UX_TicketInteractions_OneOriginatingPerTicket",
                table: "TicketInteractions",
                column: "TicketId",
                unique: true,
                filter: "[IsOriginatingInteraction] = 1");

            // Hand-written data carry-over (the scaffold would have dropped
            // the rows): every existing 1:1 context row IS the interaction
            // its ticket was created from, so it moves across flagged as the
            // originating interaction before the old table goes.
            migrationBuilder.Sql(
                """
                INSERT INTO [TicketInteractions]
                    ([TicketId], [IsOriginatingInteraction], [Source], [ChannelId], [CustomerPhone], [CalledNumber],
                     [GenesysConversationId], [GenesysQueueId], [GenesysQueueName], [GenesysAgentId], [GenesysAgentName],
                     [InteractionStartedAtUtc], [Direction], [CreatedAtUtc])
                SELECT
                    [TicketId], 1, [Source], [ChannelId], [CustomerPhone], [CalledNumber],
                    [GenesysConversationId], [GenesysQueueId], [GenesysQueueName], [GenesysAgentId], [GenesysAgentName],
                    [InteractionStartedAtUtc], [Direction], [CreatedAtUtc]
                FROM [TicketInteractionContexts];
                """);

            migrationBuilder.DropTable(
                name: "TicketInteractionContexts");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TicketInteractions");

            migrationBuilder.CreateTable(
                name: "TicketInteractionContexts",
                columns: table => new
                {
                    TicketId = table.Column<long>(type: "bigint", nullable: false),
                    CalledNumber = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ChannelId = table.Column<byte>(type: "tinyint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CustomerPhone = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Direction = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    GenesysAgentId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    GenesysAgentName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    GenesysConversationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    GenesysQueueId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    GenesysQueueName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    InteractionStartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Source = table.Column<byte>(type: "tinyint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketInteractionContexts", x => x.TicketId);
                    table.ForeignKey(
                        name: "FK_TicketInteractionContexts_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "TicketId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TicketInteractionContexts_GenesysConversationId",
                table: "TicketInteractionContexts",
                column: "GenesysConversationId",
                filter: "[GenesysConversationId] IS NOT NULL");
        }
    }
}

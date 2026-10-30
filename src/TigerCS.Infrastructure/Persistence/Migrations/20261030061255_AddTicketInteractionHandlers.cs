using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// CS Agent ticket visibility: records <b>every</b> Ticketing user that actually handled a Genesys interaction (a transfer has two),
    /// where <c>TicketInteractions.HandledByUserId</c> only ever held the first. Existing single handlers are carried over (Source 9 =
    /// Legacy) so an agent keeps access to tickets whose calls the integration already attributed to them.
    /// </summary>
    public partial class AddTicketInteractionHandlers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TicketInteractionHandlers",
                columns: table => new
                {
                    TicketInteractionHandlerId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TicketInteractionId = table.Column<long>(type: "bigint", nullable: false),
                    TicketId = table.Column<long>(type: "bigint", nullable: false),
                    HandledByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GenesysAgentUserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Source = table.Column<byte>(type: "tinyint", nullable: false),
                    ConnectedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RecordedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketInteractionHandlers", x => x.TicketInteractionHandlerId);
                    table.ForeignKey(
                        name: "FK_TicketInteractionHandlers_AspNetUsers_HandledByUserId",
                        column: x => x.HandledByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketInteractionHandlers_TicketInteractions_TicketInteractionId",
                        column: x => x.TicketInteractionId,
                        principalTable: "TicketInteractions",
                        principalColumn: "TicketInteractionId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TicketInteractionHandlers_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "TicketId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TicketInteractionHandlers_HandledByUserId_TicketId",
                table: "TicketInteractionHandlers",
                columns: new[] { "HandledByUserId", "TicketId" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketInteractionHandlers_TicketId",
                table: "TicketInteractionHandlers",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "UX_TicketInteractionHandlers_InteractionId_UserId",
                table: "TicketInteractionHandlers",
                columns: new[] { "TicketInteractionId", "HandledByUserId" },
                unique: true);

            migrationBuilder.Sql(
                """
                INSERT INTO [TicketInteractionHandlers]
                    ([TicketInteractionId], [TicketId], [HandledByUserId], [GenesysAgentUserId], [Source], [ConnectedAtUtc], [RecordedAtUtc])
                SELECT i.[TicketInteractionId], i.[TicketId], i.[HandledByUserId], i.[GenesysAgentUserId], 9, NULL, i.[CreatedAtUtc]
                FROM [TicketInteractions] AS i
                WHERE i.[HandledByUserId] IS NOT NULL AND i.[GenesysAgentUserId] IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TicketInteractionHandlers");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Genesys integration phase 1. Three changes, no data migration:
    ///
    /// <list type="number">
    ///   <item><description>
    ///     <b>TicketInteractions gains a conversation lifecycle and the
    ///     channel's own customer fields</b> — <c>EndedAtUtc</c>/
    ///     <c>EndReason</c> (when and why a conversation ended) and
    ///     <c>CustomerName</c>/<c>CustomerEmail</c> (what a chat form
    ///     collected). All four are nullable: a live conversation has no end,
    ///     and a voice call collects no form fields, so every existing row
    ///     stays valid untouched.
    ///   </description></item>
    ///   <item><description>
    ///     <b>The GenesysConversationId index becomes UNIQUE.</b> This is the
    ///     database-level half of "one Genesys inquiry produces exactly one
    ///     ticket" — the application checks for an existing conversation
    ///     first, but two concurrent deliveries can both pass that read, and
    ///     this index is what makes the loser fail rather than create a
    ///     second ticket. Still filtered to NOT NULL, because walk-in
    ///     interactions carry no conversation id and SQL Server treats
    ///     multiple NULLs as duplicates in a unique index.
    ///     <para>
    ///     <b>Upgrade note:</b> nothing before this phase wrote a Genesys
    ///     conversation id automatically (no ingestion path existed), so no
    ///     duplicates are expected. If a database somehow holds two
    ///     interactions sharing one conversation id, this index creation
    ///     fails loudly rather than silently discarding one — which is
    ///     correct: which ticket the conversation really belongs to is a
    ///     business decision, not something a migration may guess. Find them
    ///     with:
    ///     <c>SELECT GenesysConversationId, COUNT(*) FROM TicketInteractions
    ///     WHERE GenesysConversationId IS NOT NULL GROUP BY
    ///     GenesysConversationId HAVING COUNT(*) &gt; 1;</c>
    ///     </para>
    ///   </description></item>
    ///   <item><description>
    ///     <b>Three new tables</b> — <c>TicketInteractionMessages</c> (the
    ///     structured chat transcript, one row per message), plus the
    ///     <c>GenesysQueueMappings</c> and <c>GenesysDepartmentSettings</c>
    ///     routing configuration. <b>No rows are seeded into either
    ///     configuration table</b>: the real Genesys queue ids are not known
    ///     to this repository and are never invented — an administrator
    ///     enters them once the Genesys team supplies them.
    ///   </description></item>
    /// </list>
    /// </summary>
    public partial class AddGenesysIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TicketInteractions_GenesysConversationId",
                table: "TicketInteractions");

            migrationBuilder.AddColumn<string>(
                name: "CustomerEmail",
                table: "TicketInteractions",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerName",
                table: "TicketInteractions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EndReason",
                table: "TicketInteractions",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EndedAtUtc",
                table: "TicketInteractions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GenesysDepartmentSettings",
                columns: table => new
                {
                    GenesysDepartmentSettingsId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DepartmentId = table.Column<int>(type: "int", nullable: false),
                    DefaultCategoryId = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenesysDepartmentSettings", x => x.GenesysDepartmentSettingsId);
                    table.ForeignKey(
                        name: "FK_GenesysDepartmentSettings_Categories_DefaultCategoryId",
                        column: x => x.DefaultCategoryId,
                        principalTable: "Categories",
                        principalColumn: "CategoryId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GenesysDepartmentSettings_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "DepartmentId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GenesysQueueMappings",
                columns: table => new
                {
                    GenesysQueueMappingId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    QueueId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    QueueName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DepartmentId = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenesysQueueMappings", x => x.GenesysQueueMappingId);
                    table.ForeignKey(
                        name: "FK_GenesysQueueMappings_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "DepartmentId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TicketInteractionMessages",
                columns: table => new
                {
                    TicketInteractionMessageId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TicketInteractionId = table.Column<long>(type: "bigint", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    Sender = table.Column<byte>(type: "tinyint", nullable: false),
                    SenderName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SenderId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ExternalMessageId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketInteractionMessages", x => x.TicketInteractionMessageId);
                    table.ForeignKey(
                        name: "FK_TicketInteractionMessages_TicketInteractions_TicketInteractionId",
                        column: x => x.TicketInteractionId,
                        principalTable: "TicketInteractions",
                        principalColumn: "TicketInteractionId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_TicketInteractions_GenesysConversationId",
                table: "TicketInteractions",
                column: "GenesysConversationId",
                unique: true,
                filter: "[GenesysConversationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_GenesysDepartmentSettings_DefaultCategoryId",
                table: "GenesysDepartmentSettings",
                column: "DefaultCategoryId");

            migrationBuilder.CreateIndex(
                name: "UX_GenesysDepartmentSettings_DepartmentId",
                table: "GenesysDepartmentSettings",
                column: "DepartmentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GenesysQueueMappings_DepartmentId",
                table: "GenesysQueueMappings",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "UX_GenesysQueueMappings_QueueId",
                table: "GenesysQueueMappings",
                column: "QueueId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_TicketInteractionMessages_InteractionSequence",
                table: "TicketInteractionMessages",
                columns: new[] { "TicketInteractionId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GenesysDepartmentSettings");

            migrationBuilder.DropTable(
                name: "GenesysQueueMappings");

            migrationBuilder.DropTable(
                name: "TicketInteractionMessages");

            migrationBuilder.DropIndex(
                name: "UX_TicketInteractions_GenesysConversationId",
                table: "TicketInteractions");

            migrationBuilder.DropColumn(
                name: "CustomerEmail",
                table: "TicketInteractions");

            migrationBuilder.DropColumn(
                name: "CustomerName",
                table: "TicketInteractions");

            migrationBuilder.DropColumn(
                name: "EndReason",
                table: "TicketInteractions");

            migrationBuilder.DropColumn(
                name: "EndedAtUtc",
                table: "TicketInteractions");

            migrationBuilder.CreateIndex(
                name: "IX_TicketInteractions_GenesysConversationId",
                table: "TicketInteractions",
                column: "GenesysConversationId",
                filter: "[GenesysConversationId] IS NOT NULL");
        }
    }
}

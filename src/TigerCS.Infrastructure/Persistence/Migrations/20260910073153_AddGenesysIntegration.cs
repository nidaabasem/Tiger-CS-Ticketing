using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGenesysIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TicketInteractions_GenesysConversationId",
                table: "TicketInteractions");

            migrationBuilder.AlterColumn<byte>(
                name: "PriorityId",
                table: "Tickets",
                type: "tinyint",
                nullable: true,
                oldClrType: typeof(byte),
                oldType: "tinyint");

            migrationBuilder.AlterColumn<int>(
                name: "CategoryId",
                table: "Tickets",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

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

            migrationBuilder.AlterColumn<byte>(
                name: "PriorityId",
                table: "Tickets",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0,
                oldClrType: typeof(byte),
                oldType: "tinyint",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "CategoryId",
                table: "Tickets",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TicketInteractions_GenesysConversationId",
                table: "TicketInteractions",
                column: "GenesysConversationId",
                filter: "[GenesysConversationId] IS NOT NULL");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGenesysScreenPopLaunches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GenesysScreenPopLaunches",
                columns: table => new
                {
                    GenesysScreenPopLaunchId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TokenHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    GenesysUserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetPath = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ConversationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    TicketId = table.Column<long>(type: "bigint", nullable: true),
                    IssuedByEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IssuedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RedeemedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenesysScreenPopLaunches", x => x.GenesysScreenPopLaunchId);
                });

            migrationBuilder.CreateIndex(
                name: "UX_GenesysScreenPopLaunches_TokenHash",
                table: "GenesysScreenPopLaunches",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GenesysScreenPopLaunches");
        }
    }
}

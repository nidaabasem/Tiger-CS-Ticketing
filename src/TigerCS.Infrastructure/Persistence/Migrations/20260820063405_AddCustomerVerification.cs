using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditEntries",
                columns: table => new
                {
                    AuditEntryId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ActorEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EntityId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    BeforeValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AfterValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntries", x => x.AuditEntryId);
                });

            migrationBuilder.CreateTable(
                name: "UnitReferences",
                columns: table => new
                {
                    UnitReferenceId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CrmUnitId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UnitNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PropertyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TowerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    UnitType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    LastSyncedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnitReferences", x => x.UnitReferenceId);
                });

            migrationBuilder.CreateTable(
                name: "ContactReferences",
                columns: table => new
                {
                    ContactReferenceId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CrmContactId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UnitReferenceId = table.Column<int>(type: "int", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ContactChannel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ContactType = table.Column<int>(type: "int", nullable: false),
                    AuthorizedRepresentativeOfContactReferenceId = table.Column<int>(type: "int", nullable: true),
                    LastSyncedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContactReferences", x => x.ContactReferenceId);
                    table.ForeignKey(
                        name: "FK_ContactReferences_ContactReferences_AuthorizedRepresentativeOfContactReferenceId",
                        column: x => x.AuthorizedRepresentativeOfContactReferenceId,
                        principalTable: "ContactReferences",
                        principalColumn: "ContactReferenceId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContactReferences_UnitReferences_UnitReferenceId",
                        column: x => x.UnitReferenceId,
                        principalTable: "UnitReferences",
                        principalColumn: "UnitReferenceId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VerificationSessions",
                columns: table => new
                {
                    VerificationSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UnitReferenceId = table.Column<int>(type: "int", nullable: false),
                    ContactReferenceId = table.Column<int>(type: "int", nullable: false),
                    SnapshotUnitNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    SnapshotPropertyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SnapshotTowerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SnapshotUnitType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    SnapshotContactDisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SnapshotContactChannel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Confirmed = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConfirmedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConsumedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConsumedByTicketId = table.Column<long>(type: "bigint", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VerificationSessions", x => x.VerificationSessionId);
                    table.ForeignKey(
                        name: "FK_VerificationSessions_ContactReferences_ContactReferenceId",
                        column: x => x.ContactReferenceId,
                        principalTable: "ContactReferences",
                        principalColumn: "ContactReferenceId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VerificationSessions_UnitReferences_UnitReferenceId",
                        column: x => x.UnitReferenceId,
                        principalTable: "UnitReferences",
                        principalColumn: "UnitReferenceId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContactReferences_AuthorizedRepresentativeOfContactReferenceId",
                table: "ContactReferences",
                column: "AuthorizedRepresentativeOfContactReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_ContactReferences_CrmContactId",
                table: "ContactReferences",
                column: "CrmContactId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContactReferences_UnitReferenceId",
                table: "ContactReferences",
                column: "UnitReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_UnitReferences_CrmUnitId",
                table: "UnitReferences",
                column: "CrmUnitId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VerificationSessions_AgentEmployeeId_IdempotencyKey",
                table: "VerificationSessions",
                columns: new[] { "AgentEmployeeId", "IdempotencyKey" },
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_VerificationSessions_ContactReferenceId",
                table: "VerificationSessions",
                column: "ContactReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_VerificationSessions_UnitReferenceId",
                table: "VerificationSessions",
                column: "UnitReferenceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEntries");

            migrationBuilder.DropTable(
                name: "VerificationSessions");

            migrationBuilder.DropTable(
                name: "ContactReferences");

            migrationBuilder.DropTable(
                name: "UnitReferences");
        }
    }
}

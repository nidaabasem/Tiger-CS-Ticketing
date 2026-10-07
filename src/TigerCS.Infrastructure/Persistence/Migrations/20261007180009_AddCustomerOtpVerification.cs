using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerOtpVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CrmBuyerCustomerId",
                table: "VerificationSessions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CrmBuyerLeadId",
                table: "VerificationSessions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProofChallengeId",
                table: "VerificationSessions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CustomerOtpChallenges",
                columns: table => new
                {
                    CustomerOtpChallengeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CallerEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CrmCustomerId = table.Column<int>(type: "int", nullable: false),
                    CrmLeadId = table.Column<int>(type: "int", nullable: false),
                    UnitReferenceId = table.Column<int>(type: "int", nullable: false),
                    ContactReferenceId = table.Column<int>(type: "int", nullable: false),
                    MaskedDestination = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Salt = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    CodeHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    FailedAttempts = table.Column<int>(type: "int", nullable: false),
                    SendCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    VerifiedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    VerificationSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerOtpChallenges", x => x.CustomerOtpChallengeId);
                    table.ForeignKey(
                        name: "FK_CustomerOtpChallenges_AspNetUsers_CallerEmployeeId",
                        column: x => x.CallerEmployeeId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerOtpChallenges_ContactReferences_ContactReferenceId",
                        column: x => x.ContactReferenceId,
                        principalTable: "ContactReferences",
                        principalColumn: "ContactReferenceId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerOtpChallenges_UnitReferences_UnitReferenceId",
                        column: x => x.UnitReferenceId,
                        principalTable: "UnitReferences",
                        principalColumn: "UnitReferenceId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_VerificationSessions_ProofChallengeId",
                table: "VerificationSessions",
                column: "ProofChallengeId",
                unique: true,
                filter: "[ProofChallengeId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerOtpChallenges_CallerLead",
                table: "CustomerOtpChallenges",
                columns: new[] { "CallerEmployeeId", "CrmCustomerId", "CrmLeadId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerOtpChallenges_ContactReferenceId",
                table: "CustomerOtpChallenges",
                column: "ContactReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerOtpChallenges_Customer",
                table: "CustomerOtpChallenges",
                columns: new[] { "CrmCustomerId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerOtpChallenges_UnitReferenceId",
                table: "CustomerOtpChallenges",
                column: "UnitReferenceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomerOtpChallenges");

            migrationBuilder.DropIndex(
                name: "UX_VerificationSessions_ProofChallengeId",
                table: "VerificationSessions");

            migrationBuilder.DropColumn(
                name: "CrmBuyerCustomerId",
                table: "VerificationSessions");

            migrationBuilder.DropColumn(
                name: "CrmBuyerLeadId",
                table: "VerificationSessions");

            migrationBuilder.DropColumn(
                name: "ProofChallengeId",
                table: "VerificationSessions");
        }
    }
}

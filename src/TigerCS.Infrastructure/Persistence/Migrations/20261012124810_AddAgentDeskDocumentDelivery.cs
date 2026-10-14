using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentDeskDocumentDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "VerificationSessionId",
                table: "CrmDocumentDeliveryRequests",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<int>(
                name: "CrmCustomerId",
                table: "CrmDocumentDeliveryRequests",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CrmLeadId",
                table: "CrmDocumentDeliveryRequests",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "Origin",
                table: "CrmDocumentDeliveryRequests",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)1);

            migrationBuilder.CreateIndex(
                name: "IX_CrmDocumentDeliveryRequests_AgentDocument",
                table: "CrmDocumentDeliveryRequests",
                columns: new[] { "CrmCustomerId", "CrmLeadId", "DocumentType", "CrmRecordId", "Channel", "Status" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_CrmDocumentDeliveryRequests_Origin",
                table: "CrmDocumentDeliveryRequests",
                sql: "([Origin] = 1 AND [VerificationSessionId] IS NOT NULL AND [CrmCustomerId] IS NULL AND [CrmLeadId] IS NULL) OR ([Origin] = 2 AND [VerificationSessionId] IS NULL AND [CrmCustomerId] IS NOT NULL AND [CrmLeadId] IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Agent-desk rows have no verification session, which the old NOT NULL column cannot hold; they cannot be
            // represented after a rollback, so they are removed (they are idempotency/audit rows only, never documents).
            migrationBuilder.Sql("DELETE FROM [CrmDocumentDeliveryRequests] WHERE [Origin] = 2;");

            migrationBuilder.DropIndex(
                name: "IX_CrmDocumentDeliveryRequests_AgentDocument",
                table: "CrmDocumentDeliveryRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CrmDocumentDeliveryRequests_Origin",
                table: "CrmDocumentDeliveryRequests");

            migrationBuilder.DropColumn(
                name: "CrmCustomerId",
                table: "CrmDocumentDeliveryRequests");

            migrationBuilder.DropColumn(
                name: "CrmLeadId",
                table: "CrmDocumentDeliveryRequests");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "CrmDocumentDeliveryRequests");

            migrationBuilder.AlterColumn<Guid>(
                name: "VerificationSessionId",
                table: "CrmDocumentDeliveryRequests",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }
    }
}

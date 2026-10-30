using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Records who created each ticket (<c>Tickets.CreatedByEmployeeId</c>) so the creator can keep following it, read-only, after it moves to
    /// another department. Existing tickets are filled from the "Create" audit entry ticket creation has always written; a ticket without one
    /// stays NULL (no creator access). Only the new column is written — no other data is touched.
    /// </summary>
    public partial class AddTicketCreatedBy : Migration
    {
        /// <summary>
        /// Portable on purpose (no TOP / UPDATE-FROM): the first non-system "Create" audit entry of the ticket names its creator. A constant so
        /// a test can run exactly this statement against a real schema.
        /// </summary>
        public const string BackfillSql =
            """
            UPDATE [Tickets]
            SET [CreatedByEmployeeId] = (
                SELECT a.[ActorEmployeeId] FROM [AuditEntries] AS a
                WHERE a.[AuditEntryId] = (
                    SELECT MIN(a2.[AuditEntryId]) FROM [AuditEntries] AS a2
                    WHERE a2.[EntityType] = 'Ticket' AND a2.[Action] = 'Create' AND a2.[ActorEmployeeId] IS NOT NULL
                      AND a2.[EntityId] = CAST([Tickets].[TicketId] AS nvarchar(64))))
            WHERE [CreatedByEmployeeId] IS NULL;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByEmployeeId",
                table: "Tickets",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_CreatedByEmployeeId",
                table: "Tickets",
                column: "CreatedByEmployeeId");

            // Dynamic SQL: in the single idempotent batch EF scripts, a statement that names a column added earlier in the same batch does not
            // compile on SQL Server, so the backfill must be compiled when it runs, not up front.
            migrationBuilder.Sql($"EXEC(N'{BackfillSql.Replace("'", "''")}');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tickets_CreatedByEmployeeId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "CreatedByEmployeeId",
                table: "Tickets");
        }
    }
}

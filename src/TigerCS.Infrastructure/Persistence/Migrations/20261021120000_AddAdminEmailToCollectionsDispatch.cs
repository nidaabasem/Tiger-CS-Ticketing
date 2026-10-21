using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TigerCS.Infrastructure.Persistence;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <summary>Records the outcome of the administrator report of an approved send apart from the Genesys status, so a mail failure can be retried without touching Genesys.</summary>
    [DbContext(typeof(TigerCsDbContext))]
    [Migration("20261021120000_AddAdminEmailToCollectionsDispatch")]
    public partial class AddAdminEmailToCollectionsDispatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "AdminEmailStatus", table: "CollectionsDispatches", type: "nvarchar(16)", maxLength: 16, nullable: false, defaultValue: "None");
            migrationBuilder.AddColumn<DateTime>(name: "AdminEmailAttemptedAtUtc", table: "CollectionsDispatches", type: "datetime2", nullable: true);
            migrationBuilder.AddColumn<int>(name: "AdminEmailAttempts", table: "CollectionsDispatches", type: "int", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<int>(name: "AdminEmailRecipientCount", table: "CollectionsDispatches", type: "int", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<string>(name: "AdminEmailError", table: "CollectionsDispatches", type: "nvarchar(500)", maxLength: 500, nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "AdminEmailStatus", table: "CollectionsDispatches");
            migrationBuilder.DropColumn(name: "AdminEmailAttemptedAtUtc", table: "CollectionsDispatches");
            migrationBuilder.DropColumn(name: "AdminEmailAttempts", table: "CollectionsDispatches");
            migrationBuilder.DropColumn(name: "AdminEmailRecipientCount", table: "CollectionsDispatches");
            migrationBuilder.DropColumn(name: "AdminEmailError", table: "CollectionsDispatches");
        }
    }
}

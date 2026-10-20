using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TigerCS.Infrastructure.Persistence;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <summary>Stores the stage name (= Genesys campaign name) and the Genesys campaign id on each upload batch.</summary>
    [DbContext(typeof(TigerCsDbContext))]
    [Migration("20261020090000_AddGenesysCampaignToDispatchBatch")]
    public partial class AddGenesysCampaignToDispatchBatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StageName",
                table: "CollectionsGenesysBatches",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GenesysCampaignName",
                table: "CollectionsGenesysBatches",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GenesysCampaignId",
                table: "CollectionsGenesysBatches",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CampaignAction",
                table: "CollectionsGenesysBatches",
                type: "nvarchar(24)",
                maxLength: 24,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "StageName", table: "CollectionsGenesysBatches");
            migrationBuilder.DropColumn(name: "GenesysCampaignName", table: "CollectionsGenesysBatches");
            migrationBuilder.DropColumn(name: "GenesysCampaignId", table: "CollectionsGenesysBatches");
            migrationBuilder.DropColumn(name: "CampaignAction", table: "CollectionsGenesysBatches");
        }
    }
}

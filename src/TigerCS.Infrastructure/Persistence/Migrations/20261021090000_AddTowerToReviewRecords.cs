using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TigerCS.Infrastructure.Persistence;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <summary>Stores the unit's tower on each review record: the structured Project / Tower filter shared by Campaigns and Review.</summary>
    [DbContext(typeof(TigerCsDbContext))]
    [Migration("20261021090000_AddTowerToReviewRecords")]
    public partial class AddTowerToReviewRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(name: "TowerId", table: "CollectionsReviewRecords", type: "int", nullable: true);
            migrationBuilder.AddColumn<string>(name: "TowerNumber", table: "CollectionsReviewRecords", type: "nvarchar(20)", maxLength: 20, nullable: true);
            migrationBuilder.AddColumn<string>(name: "TowerName", table: "CollectionsReviewRecords", type: "nvarchar(200)", maxLength: 200, nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "TowerId", table: "CollectionsReviewRecords");
            migrationBuilder.DropColumn(name: "TowerNumber", table: "CollectionsReviewRecords");
            migrationBuilder.DropColumn(name: "TowerName", table: "CollectionsReviewRecords");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDispatchSuppressionAndVoiceEligibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AcknowledgedSharedPhoneCalls",
                table: "CollectionsDispatches");

            migrationBuilder.AddColumn<DateTime>(
                name: "BalanceCheckedAtUtc",
                table: "CollectionsDispatchItems",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SuppressedAtUtc",
                table: "CollectionsDispatchItems",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SuppressionError",
                table: "CollectionsDispatchItems",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SuppressionStatus",
                table: "CollectionsDispatchItems",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<bool>(
                name: "VoiceEligible",
                table: "CollectionsDispatchItems",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Phase",
                table: "CollectionsDispatches",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RevalidationMs",
                table: "CollectionsDispatches",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BalanceCheckedAtUtc",
                table: "CollectionsDispatchItems");

            migrationBuilder.DropColumn(
                name: "SuppressedAtUtc",
                table: "CollectionsDispatchItems");

            migrationBuilder.DropColumn(
                name: "SuppressionError",
                table: "CollectionsDispatchItems");

            migrationBuilder.DropColumn(
                name: "SuppressionStatus",
                table: "CollectionsDispatchItems");

            migrationBuilder.DropColumn(
                name: "VoiceEligible",
                table: "CollectionsDispatchItems");

            migrationBuilder.DropColumn(
                name: "Phase",
                table: "CollectionsDispatches");

            migrationBuilder.DropColumn(
                name: "RevalidationMs",
                table: "CollectionsDispatches");

            migrationBuilder.AddColumn<bool>(
                name: "AcknowledgedSharedPhoneCalls",
                table: "CollectionsDispatches",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSmsChannelToCustomerOtp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "Channel",
                table: "CustomerOtpChallenges",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)1);

            migrationBuilder.AddColumn<byte>(
                name: "DeliveryState",
                table: "CustomerOtpChallenges",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "CustomerOtpChallenges",
                type: "nvarchar(2)",
                maxLength: 2,
                nullable: false,
                defaultValue: "en");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Channel",
                table: "CustomerOtpChallenges");

            migrationBuilder.DropColumn(
                name: "DeliveryState",
                table: "CustomerOtpChallenges");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "CustomerOtpChallenges");
        }
    }
}

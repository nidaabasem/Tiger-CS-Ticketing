using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChannels : Migration
    {
        /// <inheritdoc />
        /// <summary>
        /// Channel Management — replaces the fixed <c>Channel</c> enum with a
        /// configurable <c>Channels</c> table WITHOUT changing any existing
        /// data. <c>IntakeRecords.ChannelId</c> and
        /// <c>TicketInteractions.ChannelId</c> keep their <c>tinyint</c> type
        /// and values; the five enum members are inserted with their exact
        /// original ids (1..5) and the enum names as their stable codes
        /// BEFORE the two foreign keys are added, so every existing row
        /// already references a channel the moment the constraint exists.
        /// The seed rows mirror <c>ChannelReferenceData</c> exactly.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Channels",
                columns: table => new
                {
                    ChannelId = table.Column<byte>(type: "tinyint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    RequiresPhone = table.Column<bool>(type: "bit", nullable: false),
                    IsGenesysEnabled = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Channels", x => x.ChannelId);
                });

            // The former enum members, under their original ids — inserted
            // before the foreign keys below so existing IntakeRecords /
            // TicketInteractions rows satisfy them. (InsertData handles
            // IDENTITY_INSERT for the identity column.)
            migrationBuilder.InsertData(
                table: "Channels",
                columns: new[] { "ChannelId", "Name", "Code", "RequiresPhone", "IsGenesysEnabled", "IsActive", "DisplayOrder" },
                values: new object[,]
                {
                    { (byte)1, "Phone", "Phone", true, true, true, 1 },
                    { (byte)2, "App / Website", "AppOrWebsite", true, false, true, 2 },
                    { (byte)3, "WhatsApp / Live Chat", "WhatsAppOrLiveChat", true, true, true, 3 },
                    { (byte)4, "Social Media Direct Message", "SocialMediaDirectMessage", false, true, true, 4 },
                    { (byte)5, "Face to Face / Kiosk", "FaceToFaceKiosk", false, false, true, 5 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_TicketInteractions_ChannelId",
                table: "TicketInteractions",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_IntakeRecords_ChannelId",
                table: "IntakeRecords",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_Channels_Code",
                table: "Channels",
                column: "Code",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_IntakeRecords_Channels_ChannelId",
                table: "IntakeRecords",
                column: "ChannelId",
                principalTable: "Channels",
                principalColumn: "ChannelId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TicketInteractions_Channels_ChannelId",
                table: "TicketInteractions",
                column: "ChannelId",
                principalTable: "Channels",
                principalColumn: "ChannelId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_IntakeRecords_Channels_ChannelId",
                table: "IntakeRecords");

            migrationBuilder.DropForeignKey(
                name: "FK_TicketInteractions_Channels_ChannelId",
                table: "TicketInteractions");

            migrationBuilder.DropTable(
                name: "Channels");

            migrationBuilder.DropIndex(
                name: "IX_TicketInteractions_ChannelId",
                table: "TicketInteractions");

            migrationBuilder.DropIndex(
                name: "IX_IntakeRecords_ChannelId",
                table: "IntakeRecords");
        }
    }
}

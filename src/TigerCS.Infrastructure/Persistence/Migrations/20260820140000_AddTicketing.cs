using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Priorities",
                columns: table => new
                {
                    PriorityId = table.Column<byte>(type: "tinyint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DisplayOrder = table.Column<byte>(type: "tinyint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Priorities", x => x.PriorityId);
                });

            migrationBuilder.CreateTable(
                name: "Categories",
                columns: table => new
                {
                    CategoryId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ParentCategoryId = table.Column<int>(type: "int", nullable: true),
                    DepartmentId = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Categories", x => x.CategoryId);
                    table.ForeignKey(
                        name: "FK_Categories_Categories_ParentCategoryId",
                        column: x => x.ParentCategoryId,
                        principalTable: "Categories",
                        principalColumn: "CategoryId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Categories_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "DepartmentId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Tickets",
                columns: table => new
                {
                    TicketId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TicketNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    OriginatingDepartmentId = table.Column<int>(type: "int", nullable: false),
                    CurrentDepartmentId = table.Column<int>(type: "int", nullable: false),
                    CurrentOwnerEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UnitReferenceId = table.Column<int>(type: "int", nullable: true),
                    ContactReferenceId = table.Column<int>(type: "int", nullable: true),
                    CategoryId = table.Column<int>(type: "int", nullable: false),
                    PriorityId = table.Column<byte>(type: "tinyint", nullable: false),
                    TicketStatus = table.Column<byte>(type: "tinyint", nullable: false),
                    VerificationStatus = table.Column<byte>(type: "tinyint", nullable: false),
                    EscalationLevel = table.Column<byte>(type: "tinyint", nullable: false),
                    SlaState = table.Column<byte>(type: "tinyint", nullable: false),
                    ResolutionOutcome = table.Column<byte>(type: "tinyint", nullable: true),
                    DuplicateOfTicketId = table.Column<long>(type: "bigint", nullable: true),
                    RequestSummary = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    FirstHumanResponseAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AcknowledgementSentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReopenCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tickets", x => x.TicketId);
                    table.ForeignKey(
                        name: "FK_Tickets_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "CategoryId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_ContactReferences_ContactReferenceId",
                        column: x => x.ContactReferenceId,
                        principalTable: "ContactReferences",
                        principalColumn: "ContactReferenceId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_Departments_CurrentDepartmentId",
                        column: x => x.CurrentDepartmentId,
                        principalTable: "Departments",
                        principalColumn: "DepartmentId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_Departments_OriginatingDepartmentId",
                        column: x => x.OriginatingDepartmentId,
                        principalTable: "Departments",
                        principalColumn: "DepartmentId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_Employees_CurrentOwnerEmployeeId",
                        column: x => x.CurrentOwnerEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_Priorities_PriorityId",
                        column: x => x.PriorityId,
                        principalTable: "Priorities",
                        principalColumn: "PriorityId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_Tickets_DuplicateOfTicketId",
                        column: x => x.DuplicateOfTicketId,
                        principalTable: "Tickets",
                        principalColumn: "TicketId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_UnitReferences_UnitReferenceId",
                        column: x => x.UnitReferenceId,
                        principalTable: "UnitReferences",
                        principalColumn: "UnitReferenceId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TicketRequesterSnapshots",
                columns: table => new
                {
                    TicketId = table.Column<long>(type: "bigint", nullable: false),
                    SnapshotUnitNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SnapshotPropertyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SnapshotTowerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SnapshotUnitType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    SnapshotContactDisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SnapshotContactChannel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CapturedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketRequesterSnapshots", x => x.TicketId);
                    table.ForeignKey(
                        name: "FK_TicketRequesterSnapshots_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "TicketId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TicketStatusHistory",
                columns: table => new
                {
                    TicketStatusHistoryId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TicketId = table.Column<long>(type: "bigint", nullable: false),
                    Dimension = table.Column<byte>(type: "tinyint", nullable: false),
                    OldValue = table.Column<byte>(type: "tinyint", nullable: true),
                    NewValue = table.Column<byte>(type: "tinyint", nullable: false),
                    ActorEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorIsSystem = table.Column<bool>(type: "bit", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketStatusHistory", x => x.TicketStatusHistoryId);
                    table.ForeignKey(
                        name: "FK_TicketStatusHistory_Employees_ActorEmployeeId",
                        column: x => x.ActorEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketStatusHistory_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "TicketId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IntakeRecords",
                columns: table => new
                {
                    IntakeRecordId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ChannelId = table.Column<byte>(type: "tinyint", nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsUnitRelated = table.Column<bool>(type: "bit", nullable: false),
                    RawUnitNumberEntered = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    PriorityHint = table.Column<byte>(type: "tinyint", nullable: true),
                    CrmVerificationStatus = table.Column<byte>(type: "tinyint", nullable: false),
                    CreatedByEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LinkedTicketId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntakeRecords", x => x.IntakeRecordId);
                    table.ForeignKey(
                        name: "FK_IntakeRecords_Employees_CreatedByEmployeeId",
                        column: x => x.CreatedByEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IntakeRecords_Priorities_PriorityHint",
                        column: x => x.PriorityHint,
                        principalTable: "Priorities",
                        principalColumn: "PriorityId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IntakeRecords_Tickets_LinkedTicketId",
                        column: x => x.LinkedTicketId,
                        principalTable: "Tickets",
                        principalColumn: "TicketId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Categories_DepartmentId",
                table: "Categories",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Categories_ParentCategoryId",
                table: "Categories",
                column: "ParentCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_IntakeRecords_CreatedByEmployeeId",
                table: "IntakeRecords",
                column: "CreatedByEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_IntakeRecords_LinkedTicketId",
                table: "IntakeRecords",
                column: "LinkedTicketId");

            migrationBuilder.CreateIndex(
                name: "IX_IntakeRecords_PriorityHint",
                table: "IntakeRecords",
                column: "PriorityHint");

            migrationBuilder.CreateIndex(
                name: "IX_TicketStatusHistory_ActorEmployeeId",
                table: "TicketStatusHistory",
                column: "ActorEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketStatusHistory_TicketId",
                table: "TicketStatusHistory",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_CategoryId",
                table: "Tickets",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_ContactReferenceId",
                table: "Tickets",
                column: "ContactReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_CurrentDepartmentId",
                table: "Tickets",
                column: "CurrentDepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_CurrentOwnerEmployeeId",
                table: "Tickets",
                column: "CurrentOwnerEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_DuplicateOfTicketId",
                table: "Tickets",
                column: "DuplicateOfTicketId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_OriginatingDepartmentId",
                table: "Tickets",
                column: "OriginatingDepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_PriorityId",
                table: "Tickets",
                column: "PriorityId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TicketNumber",
                table: "Tickets",
                column: "TicketNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_UnitReferenceId",
                table: "Tickets",
                column: "UnitReferenceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IntakeRecords");

            migrationBuilder.DropTable(
                name: "TicketRequesterSnapshots");

            migrationBuilder.DropTable(
                name: "TicketStatusHistory");

            migrationBuilder.DropTable(
                name: "Tickets");

            migrationBuilder.DropTable(
                name: "Categories");

            migrationBuilder.DropTable(
                name: "Priorities");
        }
    }
}

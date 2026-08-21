using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "Tickets",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.CreateTable(
                name: "TicketAssignments",
                columns: table => new
                {
                    TicketAssignmentId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TicketId = table.Column<long>(type: "bigint", nullable: false),
                    AssignedEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignedDepartmentId = table.Column<int>(type: "int", nullable: false),
                    AssignedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AssigningActorEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsCurrent = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketAssignments", x => x.TicketAssignmentId);
                    table.ForeignKey(
                        name: "FK_TicketAssignments_Departments_AssignedDepartmentId",
                        column: x => x.AssignedDepartmentId,
                        principalTable: "Departments",
                        principalColumn: "DepartmentId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketAssignments_Employees_AssignedEmployeeId",
                        column: x => x.AssignedEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketAssignments_Employees_AssigningActorEmployeeId",
                        column: x => x.AssigningActorEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketAssignments_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "TicketId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TicketResolutions",
                columns: table => new
                {
                    TicketResolutionId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TicketId = table.Column<long>(type: "bigint", nullable: false),
                    ResolutionOutcome = table.Column<byte>(type: "tinyint", nullable: false),
                    ResolutionNote = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    ReasonCode = table.Column<byte>(type: "tinyint", nullable: true),
                    DuplicateOfTicketId = table.Column<long>(type: "bigint", nullable: true),
                    ResolvingEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsCurrent = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketResolutions", x => x.TicketResolutionId);
                    table.ForeignKey(
                        name: "FK_TicketResolutions_Employees_ResolvingEmployeeId",
                        column: x => x.ResolvingEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketResolutions_Tickets_DuplicateOfTicketId",
                        column: x => x.DuplicateOfTicketId,
                        principalTable: "Tickets",
                        principalColumn: "TicketId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketResolutions_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "TicketId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TicketNotes",
                columns: table => new
                {
                    TicketNoteId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TicketId = table.Column<long>(type: "bigint", nullable: false),
                    NoteText = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    AuthorEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketNotes", x => x.TicketNoteId);
                    table.ForeignKey(
                        name: "FK_TicketNotes_Employees_AuthorEmployeeId",
                        column: x => x.AuthorEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketNotes_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "TicketId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TicketAssignments_AssignedDepartmentId",
                table: "TicketAssignments",
                column: "AssignedDepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketAssignments_AssignedEmployeeId",
                table: "TicketAssignments",
                column: "AssignedEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketAssignments_AssigningActorEmployeeId",
                table: "TicketAssignments",
                column: "AssigningActorEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketAssignments_TicketId",
                table: "TicketAssignments",
                column: "TicketId",
                unique: true,
                filter: "[IsCurrent] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_TicketNotes_AuthorEmployeeId",
                table: "TicketNotes",
                column: "AuthorEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketNotes_TicketId",
                table: "TicketNotes",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketResolutions_DuplicateOfTicketId",
                table: "TicketResolutions",
                column: "DuplicateOfTicketId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketResolutions_ResolvingEmployeeId",
                table: "TicketResolutions",
                column: "ResolvingEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketResolutions_TicketId",
                table: "TicketResolutions",
                column: "TicketId",
                unique: true,
                filter: "[IsCurrent] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TicketAssignments");

            migrationBuilder.DropTable(
                name: "TicketNotes");

            migrationBuilder.DropTable(
                name: "TicketResolutions");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "Tickets");
        }
    }
}

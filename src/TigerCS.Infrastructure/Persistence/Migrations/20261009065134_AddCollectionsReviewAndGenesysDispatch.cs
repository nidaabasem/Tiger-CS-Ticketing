using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCollectionsReviewAndGenesysDispatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CollectionsDispatches",
                columns: table => new
                {
                    CollectionsDispatchId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Fingerprint = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    InitiatedByEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InitiatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReviewRunId = table.Column<long>(type: "bigint", nullable: false),
                    SelectionMode = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    FilterJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ApprovedCount = table.Column<int>(type: "int", nullable: false),
                    ApprovedTotalsJson = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    AcknowledgedActiveCampaignRisk = table.Column<bool>(type: "bit", nullable: false),
                    AcknowledgedSharedPhoneCalls = table.Column<bool>(type: "bit", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    StatusReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ExcludedAtDispatchCount = table.Column<int>(type: "int", nullable: false),
                    LeaseOwner = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionsDispatches", x => x.CollectionsDispatchId);
                });

            migrationBuilder.CreateTable(
                name: "CollectionsReviewRuns",
                columns: table => new
                {
                    CollectionsReviewRunId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    AsOfDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CompanyId = table.Column<int>(type: "int", nullable: true),
                    DueFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    DueTo = table.Column<DateOnly>(type: "date", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceProcedureSuffix = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    SourceReconciled = table.Column<bool>(type: "bit", nullable: false),
                    RequestedByEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SourceReadAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Phase = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ProgressPercent = table.Column<int>(type: "int", nullable: false),
                    SourceRowCount = table.Column<int>(type: "int", nullable: false),
                    RecordCount = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsCurrent = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionsReviewRuns", x => x.CollectionsReviewRunId);
                });

            migrationBuilder.CreateTable(
                name: "CollectionsDispatchItems",
                columns: table => new
                {
                    CollectionsDispatchItemId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CollectionsDispatchId = table.Column<long>(type: "bigint", nullable: false),
                    RecordKey = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    ReminderType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CompanyId = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    UnitId = table.Column<int>(type: "int", nullable: true),
                    UnitCode = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    CustomerName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Phone = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    StatusReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CollectionsGenesysBatchId = table.Column<long>(type: "bigint", nullable: true),
                    BatchPosition = table.Column<int>(type: "int", nullable: true),
                    GenesysContactId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    UploadedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionsDispatchItems", x => x.CollectionsDispatchItemId);
                    table.ForeignKey(
                        name: "FK_CollectionsDispatchItems_CollectionsDispatches_CollectionsDispatchId",
                        column: x => x.CollectionsDispatchId,
                        principalTable: "CollectionsDispatches",
                        principalColumn: "CollectionsDispatchId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CollectionsGenesysBatches",
                columns: table => new
                {
                    CollectionsGenesysBatchId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CollectionsDispatchId = table.Column<long>(type: "bigint", nullable: false),
                    ReminderType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ContactListId = table.Column<string>(type: "varchar(36)", unicode: false, maxLength: 36, nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    ContactCount = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    RequestHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    HttpStatus = table.Column<int>(type: "int", nullable: true),
                    ReturnedContactCount = table.Column<int>(type: "int", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReconciledByEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReconciledAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReconciliationNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionsGenesysBatches", x => x.CollectionsGenesysBatchId);
                    table.ForeignKey(
                        name: "FK_CollectionsGenesysBatches_CollectionsDispatches_CollectionsDispatchId",
                        column: x => x.CollectionsDispatchId,
                        principalTable: "CollectionsDispatches",
                        principalColumn: "CollectionsDispatchId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CollectionsReviewRecords",
                columns: table => new
                {
                    CollectionsReviewRecordId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CollectionsReviewRunId = table.Column<long>(type: "bigint", nullable: false),
                    RecordKey = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    CycleKey = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    ReminderType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CompanyId = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CustomerName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Phone = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    UnitId = table.Column<int>(type: "int", nullable: true),
                    UnitCode = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    ProjectCode = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    PaymentStatus = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SourceStatus = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RemainingAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    RawRemainingAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    InstalmentCount = table.Column<int>(type: "int", nullable: false),
                    ValidationStatus = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Reasons = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: false),
                    SourceReadAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionsReviewRecords", x => x.CollectionsReviewRecordId);
                    table.ForeignKey(
                        name: "FK_CollectionsReviewRecords_CollectionsReviewRuns_CollectionsReviewRunId",
                        column: x => x.CollectionsReviewRunId,
                        principalTable: "CollectionsReviewRuns",
                        principalColumn: "CollectionsReviewRunId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_CollectionsDispatches_IdempotencyKey",
                table: "CollectionsDispatches",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_CollectionsDispatches_PublicId",
                table: "CollectionsDispatches",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CollectionsDispatchItems_Batch",
                table: "CollectionsDispatchItems",
                column: "CollectionsGenesysBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_CollectionsDispatchItems_CollectionsDispatchId",
                table: "CollectionsDispatchItems",
                column: "CollectionsDispatchId");

            migrationBuilder.CreateIndex(
                name: "IX_CollectionsDispatchItems_RecordKey",
                table: "CollectionsDispatchItems",
                column: "RecordKey");

            migrationBuilder.CreateIndex(
                name: "UX_CollectionsDispatchItems_LiveRecord",
                table: "CollectionsDispatchItems",
                column: "RecordKey",
                unique: true,
                filter: "[Status] IN ('Approved','UploadedToGenesys','UnknownOutcome')");

            migrationBuilder.CreateIndex(
                name: "UX_CollectionsGenesysBatches_Dispatch_Sequence",
                table: "CollectionsGenesysBatches",
                columns: new[] { "CollectionsDispatchId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CollectionsReviewRecords_Run_Customer",
                table: "CollectionsReviewRecords",
                columns: new[] { "CollectionsReviewRunId", "CompanyId", "TenantId", "UnitCode" });

            migrationBuilder.CreateIndex(
                name: "IX_CollectionsReviewRecords_Run_Due",
                table: "CollectionsReviewRecords",
                columns: new[] { "CollectionsReviewRunId", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_CollectionsReviewRecords_Run_Type_Status",
                table: "CollectionsReviewRecords",
                columns: new[] { "CollectionsReviewRunId", "ReminderType", "ValidationStatus" });

            migrationBuilder.CreateIndex(
                name: "UX_CollectionsReviewRecords_Run_Key",
                table: "CollectionsReviewRecords",
                columns: new[] { "CollectionsReviewRunId", "RecordKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_CollectionsReviewRuns_Active",
                table: "CollectionsReviewRuns",
                column: "IsActive",
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "UX_CollectionsReviewRuns_Current",
                table: "CollectionsReviewRuns",
                column: "IsCurrent",
                unique: true,
                filter: "[IsCurrent] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CollectionsDispatchItems");

            migrationBuilder.DropTable(
                name: "CollectionsGenesysBatches");

            migrationBuilder.DropTable(
                name: "CollectionsReviewRecords");

            migrationBuilder.DropTable(
                name: "CollectionsDispatches");

            migrationBuilder.DropTable(
                name: "CollectionsReviewRuns");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MasterEquipmentApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class SyncJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "staging_parameter_updates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SyncJobId",
                table: "staging_parameter_updates",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "aps_connection",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProtectedRefreshToken = table.Column<string>(type: "text", nullable: false),
                    Account = table.Column<string>(type: "text", nullable: true),
                    ConnectedBy = table.Column<string>(type: "text", nullable: true),
                    ConnectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_aps_connection", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "revit_models",
                columns: table => new
                {
                    ModelGuid = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Region = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    CloudProjectGuid = table.Column<string>(type: "text", nullable: true),
                    CloudModelGuid = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_revit_models", x => x.ModelGuid);
                });

            migrationBuilder.CreateTable(
                name: "sync_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelGuid = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Runner = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    WorkItemId = table.Column<string>(type: "text", nullable: true),
                    DaStatus = table.Column<string>(type: "text", nullable: true),
                    RequestedBy = table.Column<string>(type: "text", nullable: false),
                    RequestedById = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastPolledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    ReportUrl = table.Column<string>(type: "text", nullable: true),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: true),
                    RunnerState = table.Column<string>(type: "jsonb", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_jobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sync_jobs_ModelGuid_CreatedAt",
                table: "sync_jobs",
                columns: new[] { "ModelGuid", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_sync_jobs_one_active_per_model",
                table: "sync_jobs",
                column: "ModelGuid",
                unique: true,
                filter: "\"Status\" IN ('Queued','Submitted')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "aps_connection");

            migrationBuilder.DropTable(
                name: "revit_models");

            migrationBuilder.DropTable(
                name: "sync_jobs");

            migrationBuilder.DropColumn(
                name: "LastError",
                table: "staging_parameter_updates");

            migrationBuilder.DropColumn(
                name: "SyncJobId",
                table: "staging_parameter_updates");
        }
    }
}

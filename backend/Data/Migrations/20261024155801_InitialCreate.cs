using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MasterEquipmentApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ModelGuid = table.Column<string>(type: "text", nullable: false),
                    EquipmentUniqueId = table.Column<string>(type: "text", nullable: false),
                    ParameterGuid = table.Column<Guid>(type: "uuid", nullable: true),
                    ParameterName = table.Column<string>(type: "text", nullable: true),
                    OldValue = table.Column<string>(type: "text", nullable: true),
                    NewValue = table.Column<string>(type: "text", nullable: true),
                    StagingId = table.Column<Guid>(type: "uuid", nullable: true),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: true),
                    Details = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_log", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "equipment_master",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelGuid = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    UniqueId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EquipmentGuid = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Tag = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FamilyType = table.Column<string>(type: "text", nullable: false),
                    Discipline = table.Column<string>(type: "text", nullable: false),
                    Values = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_equipment_master", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "staging_parameter_updates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EquipmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelGuid = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EquipmentUniqueId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ParameterGuid = table.Column<Guid>(type: "uuid", nullable: false),
                    ParameterName = table.Column<string>(type: "text", nullable: false),
                    StorageType = table.Column<string>(type: "text", nullable: false),
                    OldValue = table.Column<string>(type: "text", nullable: false),
                    ProposedValue = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RequestedBy = table.Column<string>(type: "text", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewedBy = table.Column<string>(type: "text", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RejectionReason = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staging_parameter_updates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_staging_parameter_updates_equipment_master_EquipmentId",
                        column: x => x.EquipmentId,
                        principalTable: "equipment_master",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_BatchId",
                table: "audit_log",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_ModelGuid_EquipmentUniqueId_OccurredAt",
                table: "audit_log",
                columns: new[] { "ModelGuid", "EquipmentUniqueId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_equipment_master_EquipmentGuid",
                table: "equipment_master",
                column: "EquipmentGuid");

            migrationBuilder.CreateIndex(
                name: "IX_equipment_master_ModelGuid_UniqueId",
                table: "equipment_master",
                columns: new[] { "ModelGuid", "UniqueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_staging_parameter_updates_EquipmentId_ParameterGuid",
                table: "staging_parameter_updates",
                columns: new[] { "EquipmentId", "ParameterGuid" },
                unique: true,
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_staging_parameter_updates_ModelGuid_Status",
                table: "staging_parameter_updates",
                columns: new[] { "ModelGuid", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "staging_parameter_updates");

            migrationBuilder.DropTable(
                name: "equipment_master");
        }
    }
}

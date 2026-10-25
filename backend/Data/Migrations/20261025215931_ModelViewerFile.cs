using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MasterEquipmentApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class ModelViewerFile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccHubId",
                table: "revit_models",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AccItemId",
                table: "revit_models",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AccProjectId",
                table: "revit_models",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccHubId",
                table: "revit_models");

            migrationBuilder.DropColumn(
                name: "AccItemId",
                table: "revit_models");

            migrationBuilder.DropColumn(
                name: "AccProjectId",
                table: "revit_models");
        }
    }
}

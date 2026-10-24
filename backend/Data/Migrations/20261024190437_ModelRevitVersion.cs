using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MasterEquipmentApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class ModelRevitVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RevitVersion",
                table: "revit_models",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RevitVersion",
                table: "revit_models");
        }
    }
}

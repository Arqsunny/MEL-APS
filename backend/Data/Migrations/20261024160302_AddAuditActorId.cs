using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MasterEquipmentApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditActorId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActorId",
                table: "audit_log",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActorId",
                table: "audit_log");
        }
    }
}

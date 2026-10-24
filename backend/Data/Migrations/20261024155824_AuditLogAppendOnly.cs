using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MasterEquipmentApi.Data.Migrations
{
    /// <summary>
    /// Makes audit_log append-only at the database level: UPDATE, DELETE and TRUNCATE are rejected,
    /// so history cannot be rewritten by the application or by an ad-hoc query.
    /// </summary>
    public partial class AuditLogAppendOnly : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE FUNCTION audit_log_reject_change() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'audit_log is append-only (% not allowed)', TG_OP;
END;
$$ LANGUAGE plpgsql;");

            migrationBuilder.Sql(@"
CREATE TRIGGER audit_log_no_update_delete
    BEFORE UPDATE OR DELETE ON audit_log
    FOR EACH ROW EXECUTE FUNCTION audit_log_reject_change();");

            migrationBuilder.Sql(@"
CREATE TRIGGER audit_log_no_truncate
    BEFORE TRUNCATE ON audit_log
    FOR EACH STATEMENT EXECUTE FUNCTION audit_log_reject_change();");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS audit_log_no_truncate ON audit_log;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS audit_log_no_update_delete ON audit_log;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS audit_log_reject_change();");
        }
    }
}

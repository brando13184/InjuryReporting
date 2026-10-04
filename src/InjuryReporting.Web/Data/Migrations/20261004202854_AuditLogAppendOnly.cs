using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InjuryReporting.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuditLogAppendOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SOC2 tamper resistance: the audit trail can be appended to but never changed or removed
            // by the application role (or anyone else short of dropping the trigger as table owner).
            migrationBuilder.Sql("""
                CREATE FUNCTION audit_log_append_only() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'AuditLog is append-only';
                END;
                $$ LANGUAGE plpgsql;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER audit_log_no_update_delete
                    BEFORE UPDATE OR DELETE ON "AuditLog"
                    FOR EACH ROW EXECUTE FUNCTION audit_log_append_only();
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER audit_log_no_truncate
                    BEFORE TRUNCATE ON "AuditLog"
                    FOR EACH STATEMENT EXECUTE FUNCTION audit_log_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP TRIGGER IF EXISTS audit_log_no_truncate ON "AuditLog";""");
            migrationBuilder.Sql("""DROP TRIGGER IF EXISTS audit_log_no_update_delete ON "AuditLog";""");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS audit_log_append_only();");
        }
    }
}

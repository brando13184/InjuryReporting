using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InjuryReporting.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuditPiiErasure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The audit log stays append-only with ONE exception, needed for the right to erasure: a row's personal
            // data (actor e-mail and IP address) may be blanked to NULL. Every other column is frozen, the values can
            // only go to NULL (never to something else), and DELETE / TRUNCATE remain blocked.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION audit_log_append_only() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'UPDATE'
                       AND NEW."Id"           IS NOT DISTINCT FROM OLD."Id"
                       AND NEW."TimestampUtc" IS NOT DISTINCT FROM OLD."TimestampUtc"
                       AND NEW."ActorUserId"  IS NOT DISTINCT FROM OLD."ActorUserId"
                       AND NEW."Action"       IS NOT DISTINCT FROM OLD."Action"
                       AND NEW."EntityType"   IS NOT DISTINCT FROM OLD."EntityType"
                       AND NEW."EntityId"     IS NOT DISTINCT FROM OLD."EntityId"
                       AND NEW."Detail"       IS NOT DISTINCT FROM OLD."Detail"
                       AND NEW."ActorEmail"   IS NULL
                       AND NEW."IpAddress"    IS NULL THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'AuditLog is append-only (only the actor e-mail and IP address may be erased)';
                END;
                $$ LANGUAGE plpgsql;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION audit_log_append_only() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'AuditLog is append-only';
                END;
                $$ LANGUAGE plpgsql;
                """);
        }
    }
}

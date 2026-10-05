#!/usr/bin/env bash
# Watches the append-only audit log (run every 10 min by systemd) and emails when security-relevant things happen:
# privilege/moderation changes, MFA changes, data exports, account deletions, and bursts of failed sign-ins.
# Only action names, actors and IPs are sent -- never report content.
set -u
. /etc/injuryreporting/alerts.conf
STATE=/var/lib/ir-monitor
mkdir -p "$STATE"
psqlq() { sudo -u postgres psql -d injury_reporting -At -F '|' -c "$1"; }

max=$(psqlq 'select coalesce(max("Id"),0) from "AuditLog"')
[ -n "$max" ] || exit 0
if [ ! -f "$STATE/audit_last_id" ]; then echo "$max" > "$STATE/audit_last_id"; exit 0; fi   # first run: start from now
last=$(cat "$STATE/audit_last_id")
[ "$max" -gt "$last" ] || exit 0

interesting="'user.role_changed','user.suspended','user.unsuspended','mfa.disabled','mfa.enabled','mfa.recovery_codes_reset','analytics.exported','analytics.summary_exported','account.deleted','email.changed','incident.merged','incident.report_split','lookup.added','lookup.renamed','lookup.activated','lookup.deactivated','login.locked_out'"
events=$(psqlq "select to_char(\"TimestampUtc\",'YYYY-MM-DD HH24:MI:SS'), coalesce(\"ActorEmail\",'-'), \"Action\", coalesce(\"Detail\",''), coalesce(\"IpAddress\",'-') from \"AuditLog\" where \"Id\" > $last and \"Id\" <= $max and \"Action\" in ($interesting) order by \"Id\" limit 50")
fails=$(psqlq "select count(*), count(distinct \"IpAddress\") from \"AuditLog\" where \"Id\" > $last and \"Id\" <= $max and \"Action\" in ('login.failed','login.failed_unknown_user','login.2fa_failed','login.recovery_code_failed')")
nfail=${fails%%|*}; nips=${fails##*|}

msg=""
[ "${nfail:-0}" -ge 10 ] && msg+="Possible brute-force: ${nfail} failed sign-in attempts from ${nips} IP(s) since the last check.\n\n"
[ -n "$events" ] && msg+="Security-relevant events (UTC | actor | action | detail | IP):\n${events}\n"

if [ -n "$msg" ]; then
  printf '%b' "$msg" | /usr/local/bin/ir-notify "audit alert: $HOST" || exit 0   # keep the checkpoint if mail fails
fi
echo "$max" > "$STATE/audit_last_id"
exit 0

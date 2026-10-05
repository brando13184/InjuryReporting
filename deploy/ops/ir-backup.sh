#!/usr/bin/env bash
# Nightly encrypted backup -> private S3 bucket. Run as root by ir-backup.timer.
#
# What's backed up (one archive, encrypted with the PUBLIC key in backup.conf; the private key is NOT on this server):
#   * pg_dump (custom format) of the injury_reporting database
#   * Postgres roles (globals only)
#   * /etc/injuryreporting (app secrets + the certificate that protects the Data Protection key ring),
#     nginx site configs, fail2ban config, systemd unit, and the DB owner password file
# Restore needs both the archive and the private key; without the certificate the narratives stay unreadable,
# which is why the certificate travels inside the same encrypted archive.
set -euo pipefail
. /etc/injuryreporting/backup.conf          # BUCKET, REGION, AGE_RECIPIENT
STATE=/var/lib/ir-monitor
mkdir -p "$STATE"

fail() {
  echo "backup failed at line $1" | /usr/local/bin/ir-notify "BACKUP FAILED" || true
}
trap 'fail $LINENO' ERR

TS=$(date -u +%Y%m%dT%H%M%SZ)
TMP=$(mktemp -d /var/tmp/ir-backup.XXXXXX)
chmod 700 "$TMP"
trap 'rm -rf "$TMP"' EXIT
trap 'fail $LINENO; rm -rf "$TMP"' ERR

runuser -u postgres -- pg_dump -Fc injury_reporting > "$TMP/db.dump"
runuser -u postgres -- pg_dumpall --globals-only > "$TMP/globals.sql"
tar -C / -cf "$TMP/config.tar" \
  etc/injuryreporting etc/nginx/sites-available etc/fail2ban/jail.d etc/fail2ban/filter.d/ir-login.conf \
  etc/systemd/system/injuryreporting.service root/.injury_owner_pw 2>/dev/null
tar -C "$TMP" -cf - db.dump globals.sql config.tar | age -r "$AGE_RECIPIENT" -o "$TMP/backup.tar.age"

SIZE=$(stat -c %s "$TMP/backup.tar.age")
[ "$SIZE" -gt 1024 ] || { echo "archive suspiciously small ($SIZE bytes)" >&2; false; }

export AWS_SHARED_CREDENTIALS_FILE=/etc/injuryreporting/backup-aws-credentials
export AWS_DEFAULT_REGION="$REGION"
aws s3 cp --only-show-errors --sse AES256 "$TMP/backup.tar.age" "s3://$BUCKET/daily/backup-$TS.tar.age"
if [ "$(date -u +%d)" = "01" ]; then
  aws s3 cp --only-show-errors --sse AES256 "$TMP/backup.tar.age" "s3://$BUCKET/monthly/backup-$TS.tar.age"
fi

date +%s > "$STATE/last_backup"
echo "$TS ${SIZE} bytes" > "$STATE/last_backup_info"
echo "backup $TS ok (${SIZE} bytes)"

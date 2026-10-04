#!/usr/bin/env bash
# Installs the uploaded release, applies schema, (re)starts the service. Run as root.
set -euo pipefail
APP_DIR=/opt/injuryreporting
REL="$APP_DIR/releases/$(date +%Y%m%d%H%M%S)"

echo "== unpack release"
mkdir -p "$REL"
tar -xzf /tmp/release.tgz -C "$REL"
chown -R root:injuryapp "$REL"; chmod -R g+rX,o-rwx "$REL"
ln -sfn "$REL" "$APP_DIR/current"

echo "== apply schema as owner role (idempotent migration script)"
OWNER_PW=$(cat /root/.injury_owner_pw)
PGPASSWORD="$OWNER_PW" psql -h localhost -U injury_owner -d injury_reporting -v ON_ERROR_STOP=1 -q -f /tmp/schema.sql >/dev/null

echo "== least-privilege grants for the app role (no DDL, no TRUNCATE)"
PGPASSWORD="$OWNER_PW" psql -h localhost -U injury_owner -d injury_reporting -v ON_ERROR_STOP=1 -q <<'SQL'
GRANT USAGE ON SCHEMA public TO injury_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO injury_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO injury_app;
REVOKE TRUNCATE ON ALL TABLES IN SCHEMA public FROM injury_app;
SQL

echo "== (re)start"
systemctl restart injuryreporting
sleep 6
systemctl is-active injuryreporting
journalctl -u injuryreporting --no-pager -n 15 | sed -E 's/(Password=)[^; ]+/\1***/g'

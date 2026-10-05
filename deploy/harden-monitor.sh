#!/usr/bin/env bash
# Hardening + monitoring for the injury-reporting host. Idempotent; run as root with the ops/ folder next to it:
#   sudo bash harden-monitor.sh <public-hostname> <alert-email>
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive

HOST="${1:?usage: harden-monitor.sh <public-hostname> <alert-email>}"
ALERT_TO="${2:?usage: harden-monitor.sh <public-hostname> <alert-email>}"
HERE="$(cd "$(dirname "$0")" && pwd)"
ENV_FILE=/etc/injuryreporting/env

echo "== packages"
apt-get update -qq
apt-get install -y -qq fail2ban msmtp ca-certificates

echo "== persistent journal, 90-day retention"
mkdir -p /var/log/journal /etc/systemd/journald.conf.d
cat > /etc/systemd/journald.conf.d/injuryreporting.conf <<'EOF'
[Journal]
Storage=persistent
MaxRetentionSec=90day
SystemMaxUse=500M
EOF
systemctl restart systemd-journald

echo "== automatic security updates with a scheduled reboot (09:00 UTC) when one is required"
cat > /etc/apt/apt.conf.d/20auto-upgrades <<'EOF'
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
EOF
cat > /etc/apt/apt.conf.d/52injuryreporting-unattended <<'EOF'
Unattended-Upgrade::Automatic-Reboot "true";
Unattended-Upgrade::Automatic-Reboot-WithUsers "true";
Unattended-Upgrade::Automatic-Reboot-Time "09:00";
Unattended-Upgrade::Remove-Unused-Dependencies "true";
EOF

echo "== fail2ban: ssh + repeated failed app sign-ins (seen in the nginx access log)"
cat > /etc/fail2ban/filter.d/ir-login.conf <<'EOF'
[Definition]
# A failed sign-in re-renders the form (200); a successful one redirects (302).
failregex = ^<HOST> .* "POST /Account/(Login|LoginWith2fa|LoginWithRecoveryCode) HTTP/[0-9.]+" 200
ignoreregex =
EOF
cat > /etc/fail2ban/jail.d/injuryreporting.local <<'EOF'
[DEFAULT]
bantime  = 1h
findtime = 10m
maxretry = 5

[sshd]
enabled = true
backend = systemd

[ir-login]
enabled  = true
port     = http,https
filter   = ir-login
logpath  = /var/log/nginx/access.log
maxretry = 10
bantime  = 1h
EOF
systemctl enable --now fail2ban >/dev/null
systemctl restart fail2ban

echo "== alert email (SES SMTP credentials are read from the app's own config; never printed)"
get() { grep -m1 "^$1=" "$ENV_FILE" | cut -d= -f2-; }
SMTP_HOST=$(get Smtp__Host); SMTP_PORT=$(get Smtp__Port); SMTP_USER=$(get Smtp__Username)
SMTP_PASS=$(get Smtp__Password); SMTP_FROM=$(get Smtp__From)
[ -n "$SMTP_HOST" ] && [ -n "$SMTP_USER" ] && [ -n "$SMTP_PASS" ] || { echo "Smtp__* settings missing in $ENV_FILE" >&2; exit 1; }
umask 077
cat > /etc/msmtprc <<EOF
defaults
auth on
tls on
tls_starttls on
tls_trust_file /etc/ssl/certs/ca-certificates.crt
logfile /var/log/msmtp.log

account ses
host $SMTP_HOST
port $SMTP_PORT
user $SMTP_USER
password $SMTP_PASS
from $SMTP_FROM

account default : ses
EOF
chmod 600 /etc/msmtprc
touch /var/log/msmtp.log; chmod 600 /var/log/msmtp.log
umask 022
cat > /etc/injuryreporting/alerts.conf <<EOF
HOST=$HOST
ALERT_TO=$ALERT_TO
ALERT_FROM=$SMTP_FROM
EOF
chmod 640 /etc/injuryreporting/alerts.conf; chown root:injuryapp /etc/injuryreporting/alerts.conf

echo "== monitor scripts + timers"
install -m 755 "$HERE/ops/ir-notify.sh" /usr/local/bin/ir-notify
install -m 755 "$HERE/ops/ir-healthcheck.sh" /usr/local/bin/ir-healthcheck
install -m 755 "$HERE/ops/ir-audit-alerts.sh" /usr/local/bin/ir-audit-alerts
mkdir -p /var/lib/ir-monitor

for spec in "ir-healthcheck:5min:Health check" "ir-audit-alerts:10min:Audit log alerts"; do
  name=${spec%%:*}; rest=${spec#*:}; every=${rest%%:*}; desc=${rest#*:}
  cat > "/etc/systemd/system/$name.service" <<EOF
[Unit]
Description=Injury Reporting: $desc

[Service]
Type=oneshot
ExecStart=/usr/local/bin/$name
EOF
  cat > "/etc/systemd/system/$name.timer" <<EOF
[Unit]
Description=Injury Reporting: $desc (every $every)

[Timer]
OnBootSec=2min
OnUnitActiveSec=$every
AccuracySec=30s

[Install]
WantedBy=timers.target
EOF
done
systemctl daemon-reload
systemctl enable --now ir-healthcheck.timer ir-audit-alerts.timer >/dev/null

echo "== hardening + monitoring installed"

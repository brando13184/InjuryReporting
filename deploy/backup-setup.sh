#!/usr/bin/env bash
# Installs the backup tooling and nightly timer. Idempotent; run as root with ops/ next to it.
#   sudo bash backup-setup.sh deps
#   sudo bash backup-setup.sh install <bucket> <region> <age-public-key>
# AWS credentials for the backup writer are placed separately in /etc/injuryreporting/backup-aws-credentials.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
HERE="$(cd "$(dirname "$0")" && pwd)"

case "${1:-}" in
  deps)
    apt-get update -qq
    apt-get install -y -qq age unzip curl
    if ! command -v aws >/dev/null; then
      curl -sS https://awscli.amazonaws.com/awscli-exe-linux-x86_64.zip -o /tmp/awscliv2.zip
      unzip -q -o /tmp/awscliv2.zip -d /tmp
      /tmp/aws/install --update
      rm -rf /tmp/aws /tmp/awscliv2.zip
    fi
    age --version; aws --version
    ;;
  install)
    BUCKET="${2:?bucket}"; REGION="${3:?region}"; RECIPIENT="${4:?age public key}"
    cat > /etc/injuryreporting/backup.conf <<EOF
BUCKET=$BUCKET
REGION=$REGION
AGE_RECIPIENT=$RECIPIENT
EOF
    chmod 640 /etc/injuryreporting/backup.conf; chown root:injuryapp /etc/injuryreporting/backup.conf
    install -m 755 "$HERE/ops/ir-backup.sh" /usr/local/bin/ir-backup
    install -m 755 "$HERE/ops/ir-healthcheck.sh" /usr/local/bin/ir-healthcheck
    cat > /etc/systemd/system/ir-backup.service <<'EOF'
[Unit]
Description=Injury Reporting: encrypted backup to S3

[Service]
Type=oneshot
ExecStart=/usr/local/bin/ir-backup
TimeoutStartSec=1h
Nice=10
IOSchedulingClass=idle
EOF
    cat > /etc/systemd/system/ir-backup.timer <<'EOF'
[Unit]
Description=Injury Reporting: nightly backup (08:15 UTC = 2:15 AM Mountain)

[Timer]
OnCalendar=*-*-* 08:15:00 UTC
RandomizedDelaySec=10min
Persistent=true

[Install]
WantedBy=timers.target
EOF
    systemctl daemon-reload
    systemctl enable --now ir-backup.timer >/dev/null
    systemctl list-timers --no-pager ir-backup.timer | sed -n '1,2p'
    ;;
  *) echo "usage: backup-setup.sh deps | install <bucket> <region> <age-public-key>" >&2; exit 2 ;;
esac

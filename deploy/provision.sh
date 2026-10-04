#!/usr/bin/env bash
# Provisions the SCA Injury Reporting host. Idempotent; run as root.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive

HOST="54-226-80-38.sslip.io"
ADMIN_EMAIL="bnsivret@gmail.com"
APP_DIR=/opt/injuryreporting
CONF_DIR=/etc/injuryreporting

echo "== packages"
apt-get update -qq
apt-get install -y -qq aspnetcore-runtime-10.0 postgresql nginx certbot python3-certbot-nginx openssl ufw unattended-upgrades

echo "== swap (host has <1 GB RAM)"
if ! swapon --show | grep -q swapfile; then
  fallocate -l 1G /swapfile && chmod 600 /swapfile && mkswap /swapfile >/dev/null && swapon /swapfile
  grep -q '^/swapfile' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
fi

echo "== firewall"
ufw allow OpenSSH >/dev/null
ufw allow 80/tcp >/dev/null
ufw allow 443/tcp >/dev/null
ufw --force enable

echo "== service account + dirs"
id injuryapp >/dev/null 2>&1 || useradd --system --home "$APP_DIR" --shell /usr/sbin/nologin injuryapp
mkdir -p "$APP_DIR/releases" "$CONF_DIR"
chown root:injuryapp "$CONF_DIR"; chmod 750 "$CONF_DIR"

echo "== secrets (generated once, kept in $CONF_DIR / /root, never in the repo)"
if [ ! -f "$CONF_DIR/env" ]; then
  DB_OWNER_PW=$(openssl rand -hex 24)
  DB_APP_PW=$(openssl rand -hex 24)
  DP_PW=$(openssl rand -hex 24)
  SEED_PW="$(openssl rand -base64 24 | tr -d '/+=' | cut -c1-22)Aa1"

  sudo -u postgres psql -v ON_ERROR_STOP=1 -q <<SQL
CREATE ROLE injury_owner LOGIN PASSWORD '$DB_OWNER_PW';
CREATE ROLE injury_app LOGIN PASSWORD '$DB_APP_PW';
CREATE DATABASE injury_reporting OWNER injury_owner;
REVOKE ALL ON DATABASE injury_reporting FROM PUBLIC;
GRANT CONNECT ON DATABASE injury_reporting TO injury_app;
SQL
  printf '%s\n' "$DB_OWNER_PW" > /root/.injury_owner_pw && chmod 600 /root/.injury_owner_pw

  # Certificate that encrypts the Data Protection key ring (which in turn encrypts the PHI columns).
  openssl req -x509 -newkey rsa:3072 -nodes -keyout /tmp/dp.key -out /tmp/dp.crt \
      -subj "/CN=InjuryReporting DataProtection" -days 3650 2>/dev/null
  openssl pkcs12 -export -out "$CONF_DIR/dp.pfx" -inkey /tmp/dp.key -in /tmp/dp.crt -passout "pass:$DP_PW"
  shred -u /tmp/dp.key /tmp/dp.crt
  chown root:injuryapp "$CONF_DIR/dp.pfx"; chmod 640 "$CONF_DIR/dp.pfx"

  umask 027
  cat > "$CONF_DIR/env" <<ENV
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:5000
AllowedHosts=$HOST
ConnectionStrings__Default=Host=localhost;Port=5432;Database=injury_reporting;Username=injury_app;Password=$DB_APP_PW
Database__MigrateOnStartup=false
DataProtection__CertificatePath=$CONF_DIR/dp.pfx
DataProtection__CertificatePassword=$DP_PW
App__PublicBaseUrl=https://$HOST
Proxy__KnownProxies__0=127.0.0.1
Seed__SuperAdminEmail=$ADMIN_EMAIL
Seed__SuperAdminPassword=$SEED_PW
ENV
  chown root:injuryapp "$CONF_DIR/env"; chmod 640 "$CONF_DIR/env"
  printf '%s\n' "$SEED_PW" > /root/superadmin-initial-password.txt && chmod 600 /root/superadmin-initial-password.txt
else
  echo "(env already exists; keeping existing secrets)"
fi

echo "== systemd unit"
cat > /etc/systemd/system/injuryreporting.service <<'UNIT'
[Unit]
Description=SCA Injury Reporting
After=network.target postgresql.service
Wants=postgresql.service

[Service]
WorkingDirectory=/opt/injuryreporting/current
ExecStart=/usr/bin/dotnet /opt/injuryreporting/current/InjuryReporting.Web.dll
EnvironmentFile=/etc/injuryreporting/env
User=injuryapp
Group=injuryapp
Restart=always
RestartSec=5
KillSignal=SIGINT
SyslogIdentifier=injuryreporting
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
PrivateDevices=true
ProtectKernelTunables=true
ProtectControlGroups=true
RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6

[Install]
WantedBy=multi-user.target
UNIT
systemctl daemon-reload
systemctl enable injuryreporting >/dev/null

echo "== nginx (HTTP first; certbot adds TLS)"
cat > /etc/nginx/sites-available/injuryreporting <<NGINX
server {
    listen 80;
    listen [::]:80;
    server_name $HOST;
    server_tokens off;
    client_max_body_size 1m;

    location / {
        proxy_pass http://127.0.0.1:5000;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Forwarded-For \$remote_addr;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_read_timeout 60s;
    }
}
NGINX
ln -sf /etc/nginx/sites-available/injuryreporting /etc/nginx/sites-enabled/injuryreporting
rm -f /etc/nginx/sites-enabled/default
nginx -t
systemctl reload nginx

echo "== provision done"

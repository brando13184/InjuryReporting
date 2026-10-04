#!/usr/bin/env bash
# Moves the injury-reporting site to its own hostname (one nginx vhost per subdomain), gets a Let's Encrypt
# cert, and points the app's public URL / allowed host at it. Run as root on the server.
#
#   sudo bash switch-domain.sh reporting.longlivetheoutlands.org admin@example.org [old-host-to-retire]
#
# Other apps/sites on other subdomains follow the same pattern: one file in /etc/nginx/sites-available/<host>,
# its own localhost port + systemd unit, its own certbot cert. They never share this app's config.
set -euo pipefail

HOST="${1:?usage: switch-domain.sh <hostname> <letsencrypt-email> [old-host]}"
EMAIL="${2:?usage: switch-domain.sh <hostname> <letsencrypt-email> [old-host]}"
OLD_HOST="${3:-}"
PORT=5000
ENV_FILE=/etc/injuryreporting/env

MY_IP=$(curl -s https://checkip.amazonaws.com | tr -d '[:space:]')
DNS_IP=$(getent ahostsv4 "$HOST" | awk 'NR==1{print $1}' || true)
if [ "$MY_IP" != "$DNS_IP" ]; then
  echo "DNS for $HOST resolves to '${DNS_IP:-nothing}', but this server is $MY_IP. Create/fix the A record first." >&2
  exit 1
fi

echo "== nginx vhost for $HOST"
cat > "/etc/nginx/sites-available/$HOST" <<NGINX
server {
    listen 80;
    listen [::]:80;
    server_name $HOST;
    server_tokens off;
    client_max_body_size 1m;

    location / {
        proxy_pass http://127.0.0.1:$PORT;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Forwarded-For \$remote_addr;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_read_timeout 60s;
    }
}
NGINX
ln -sf "/etc/nginx/sites-available/$HOST" "/etc/nginx/sites-enabled/$HOST"
if [ -n "$OLD_HOST" ]; then
  rm -f "/etc/nginx/sites-enabled/injuryreporting" "/etc/nginx/sites-enabled/$OLD_HOST"
fi
nginx -t
systemctl reload nginx

echo "== certificate"
certbot --nginx -d "$HOST" --non-interactive --agree-tos --no-eff-email -m "$EMAIL" --redirect

echo "== app configuration"
sed -i "s|^AllowedHosts=.*|AllowedHosts=$HOST|; s|^App__PublicBaseUrl=.*|App__PublicBaseUrl=https://$HOST|" "$ENV_FILE"
systemctl restart injuryreporting

if [ -n "$OLD_HOST" ]; then
  echo "== retire old certificate/vhost for $OLD_HOST"
  rm -f "/etc/nginx/sites-available/injuryreporting"
  certbot delete --cert-name "$OLD_HOST" --non-interactive || true
  nginx -t && systemctl reload nginx
fi

sleep 4
systemctl is-active injuryreporting
curl -s -o /dev/null -w "https://$HOST/ -> %{http_code}\n" "https://$HOST/"

#!/usr/bin/env bash
# Local health check (run every 5 min by systemd). Emails once when something is wrong, repeats every 6 hours
# while it stays wrong, and sends an all-clear on recovery. Checks run from the server itself, so they cover the
# app, nginx, Postgres, TLS and capacity; they can't see an AWS-firewall or DNS problem from outside.
set -u
. /etc/injuryreporting/alerts.conf
STATE=/var/lib/ir-monitor
mkdir -p "$STATE"
problems=()

code=$(curl -s -o /dev/null -m 20 -w '%{http_code}' --resolve "$HOST:443:127.0.0.1" "https://$HOST/" || true)
[ "$code" = "200" ] || problems+=("site returned HTTP ${code:-000} (expected 200)")

for s in injuryreporting nginx postgresql fail2ban; do
  systemctl is-active --quiet "$s" || problems+=("service $s is not active")
done

disk=$(df --output=pcent / | tail -1 | tr -dc '0-9')
[ "${disk:-0}" -lt 85 ] || problems+=("root disk is ${disk}% full")

avail=$(awk '/MemAvailable/ {print int($2/1024)}' /proc/meminfo)
[ "${avail:-0}" -gt 80 ] || problems+=("low memory: only ${avail}MB available")

end=$(echo | openssl s_client -servername "$HOST" -connect 127.0.0.1:443 2>/dev/null | openssl x509 -noout -enddate 2>/dev/null | cut -d= -f2)
if [ -n "$end" ]; then
  days=$(( ($(date -d "$end" +%s) - $(date +%s)) / 86400 ))
  [ "$days" -gt 14 ] || problems+=("TLS certificate expires in ${days} days (auto-renewal may be failing)")
else
  problems+=("could not read the TLS certificate")
fi

if [ -f /var/run/reboot-required ] && [ -n "$(find /var/run/reboot-required -mtime +3 2>/dev/null)" ]; then
  problems+=("a reboot has been pending for more than 3 days")
fi

now=$(date +%s)
if [ "${#problems[@]}" -gt 0 ]; then
  body=$(printf ' - %s\n' "${problems[@]}")
  last=$(cat "$STATE/last_alert" 2>/dev/null || echo 0)
  prev=$(cat "$STATE/problems" 2>/dev/null || true)
  if [ "$body" != "$prev" ] || [ $((now - last)) -ge 21600 ]; then
    printf 'Problems detected on %s at %s UTC:\n\n%s\n\nRun: journalctl -u injuryreporting -n 50\n' \
      "$(hostname)" "$(date -u +%F\ %T)" "$body" | /usr/local/bin/ir-notify "PROBLEM: $HOST" && {
      printf '%s' "$body" > "$STATE/problems"; echo "$now" > "$STATE/last_alert"; }
  fi
elif [ -f "$STATE/problems" ]; then
  printf 'All checks are passing again on %s at %s UTC.\n' "$(hostname)" "$(date -u +%F\ %T)" | /usr/local/bin/ir-notify "RECOVERED: $HOST" \
    && rm -f "$STATE/problems" "$STATE/last_alert"
fi
exit 0

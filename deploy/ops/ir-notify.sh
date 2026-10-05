#!/usr/bin/env bash
# Usage: ir-notify.sh "subject" < body.txt   -- sends an operations email through SES (msmtp).
set -euo pipefail
. /etc/injuryreporting/alerts.conf
SUBJECT="${1:?subject required}"
{
  printf 'From: SCA Injury Reporting <%s>\n' "$ALERT_FROM"
  printf 'To: %s\n' "$ALERT_TO"
  printf 'Subject: [injury-reporting] %s\n' "$SUBJECT"
  printf 'Content-Type: text/plain; charset=UTF-8\n\n'
  cat
} | msmtp --read-envelope-from -t

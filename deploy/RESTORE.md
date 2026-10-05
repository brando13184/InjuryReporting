# Backups and restore runbook

## What exists
| Layer | What | When | Kept |
|---|---|---|---|
| Encrypted archive in S3 (`injuryreporting-backups-599777184120`, us-east-2) | `pg_dump` of the database, Postgres roles, `/etc/injuryreporting` (secrets + the Data Protection certificate), nginx/fail2ban/systemd config, DB owner password | nightly 08:15 UTC (2:15 AM Mountain) | dailies 35 days; first-of-month copies 400 days |
| Lightsail automatic snapshots (`LongLiveTheOutlands`, us-east-1) | whole server disk | daily 06:00 UTC | last 7 |

* Archives are encrypted on the server with an **age public key**. The matching **private key is not on the server**.
  Your copy: `C:\Users\DevArgento\.injury-reporting\backup-private-key.txt` — move it into a password manager and
  keep a second offline copy. **Without it the S3 archives cannot be opened.**
* The server can only *add* objects (IAM user `injury-reporting-backup-writer`: PutObject on `daily/*` and `monthly/*`,
  nothing else), so a compromised server can't read or delete history. The bucket is private, versioned, TLS-only.
* A failed or missing backup emails an alert (`BACKUP FAILED`, or the health check's "last successful backup N hours ago").

## Restore the database (same or new server)
1. Fetch the newest archive (needs AWS read access, e.g. your admin profile):
   `aws s3 cp s3://injuryreporting-backups-599777184120/daily/<file>.tar.age . --profile argento --region us-east-2`
2. Decrypt and unpack (needs the private key): `age -d -i backup-private-key.txt <file>.tar.age | tar -x`
   → `db.dump`, `globals.sql`, `config.tar`.
3. New server only: run `deploy/provision.sh`, then unpack `config.tar` over `/` **before** starting the app
   (this restores `/etc/injuryreporting/env` and `dp.pfx`; the app cannot decrypt narratives without that certificate).
4. Restore roles and data:
   `sudo -u postgres psql -f globals.sql` (ignore "already exists"), then
   `sudo -u postgres createdb -O injury_owner injury_reporting` and
   `sudo -u postgres pg_restore -d injury_reporting --no-owner --role=injury_owner db.dump`.
5. Re-grant app permissions: run `deploy/release.sh` (it re-applies grants) or the GRANT block inside it.
6. `sudo systemctl restart injuryreporting` and check the site, then sign in.

## After any restore: re-apply erasures
A restored backup brings back people who were erased after it was taken. Look at the *live* system's record before you
overwrite it (or in a snapshot/archive taken after those deletions): audit rows with action `account.erased_self` or
`account.erased_by_admin` list the user ids (`EntityId`). For each, delete the account and its reports again from
*Users* (Super Admin) if the person still exists in the restored data.

## Restore the whole machine
Lightsail console → the instance → Snapshots → pick an automatic snapshot → *Create new instance*. Attach the static IP
(54.226.80.38) to the new instance. Anything written after the snapshot (up to 24 h) comes from the S3 archive above.

## Test it
The restore was tested end to end on 2026-10-05 (decrypt, wrong-key refusal, restore into a throwaway database, row counts
matched the live database, audit-log triggers present). Repeat after any change to the backup scripts, and at least quarterly.

## Rotating keys
* Backup writer key: create a new access key for `injury-reporting-backup-writer`, write it to
  `/etc/injuryreporting/backup-aws-credentials` (mode 600), then delete the old key.
* Age key pair: generate a new pair, put the new public key in `/etc/injuryreporting/backup.conf`, and keep the old private
  key until the last archive encrypted with it has expired.

# SCA Injury Reporting

ASP.NET Core MVC (.NET 10) + PostgreSQL + Bootstrap 5. Anonymous and registered injury reporting for the
Society for Creative Anachronism, with combined-incident analytics for staff. All report data is handled as PHI.

## Run it locally

```bash
docker compose up -d                       # Postgres on 127.0.0.1:5432 (dev credentials in docker-compose.yml)
cd src/InjuryReporting.Web
dotnet user-secrets init
dotnet user-secrets set "Seed:SuperAdminEmail" "you@example.org"
dotnet user-secrets set "Seed:SuperAdminPassword" "<a long password, 14+ chars, upper/lower/digit>"
dotnet run --launch-profile https
```

Development applies migrations automatically and writes outgoing email (confirmation / reset links) to the console.
The first Super Admin is created from the seed secrets only when none exists; remove the password secret afterwards.
Staff must enrol in 2FA (**Manage → Two-factor**) before any staff page opens.

```bash
dotnet test                                # 37 tests on SQLite + 6 real-Postgres integration tests (skipped unless TEST_PG_ADMIN is set)
dotnet dotnet-ef migrations add <Name> --project src/InjuryReporting.Web --output-dir Data/Migrations
```

`db/schema.sql` is the idempotent schema script (regenerate with `dotnet dotnet-ef migrations script --idempotent`).

## What it does

| Area | Behaviour |
|---|---|
| Reporting | Discipline (Armored Combat, Rapier, Equestrian, Archery, Thrown Weapons), injury type, severity, injury date from a calendar picker, event name, kingdom where it occurred, injured person's kingdom membership (or non-member), short narrative. Disciplines, injury types and kingdoms are lookup tables seeded by migration. |
| Anonymous | No account needed. Signed-in users can tick "submit anonymously" per report. Anonymous reports store no user id, IP, or time of day, and the audit event carries no actor or report id. |
| Combining | Reports roll up into **incidents**. Analytics count incidents, never raw reports. |
| Users & roles | Email login. Roles: `User`, `Admin`, `SuperAdmin`. Admins review/merge incidents, see analytics, and suspend/reinstate plain users. Super Admins also grant roles and see the audit log. Users can change password and (with confirmation) email. |
| Analytics | Monthly trend chart (SVG, gap-filled), year-over-year grid, by discipline / injury type / severity / year / event kingdom / injured person's kingdom, discipline × injury-type matrix, filters. **"Hide counts under 5"** suppresses small cells; the *shareable summary* CSV always does. The incident-row CSV is internal only. |
| Pick lists | Staff can add, rename and deactivate kingdoms, disciplines and injury types (never delete, so history stays meaningful). Inactive entries leave the public form but still show on old records. |
| Self-service | Reporters can correct their report's **narrative** for 7 days (never the fields that drive duplicate matching). Users can **delete their account**: sign-in and profile are removed, filed reports stay but become anonymous, and the audit log keeps the event. 2FA setup shows a QR code. |

### Avoiding duplicate and circular reporting

* **Idempotent submit**: every form carries a random token with a unique index, so a double-click or replay stores nothing new.
* **Same reporter, same incident**: a registered user's identical report is rejected.
* **Strict match** (discipline + injury type + date + normalised event name + event kingdom + injured kingdom): an identical report from anyone else is attached to the existing incident, flagged *needs review*, and counted once.
* **Loose match** (same event/date/discipline, different injury): kept separate but flagged as a possible duplicate for staff to merge.
* **No cycles**: a report has one FK to one incident and reports never reference each other. Merging retires the source with a tombstone pointer; only *active* incidents can be targets, so chains and loops cannot form. Database check constraints reject self-merge and enforce "retired ⇔ has target". Staff can split a report back out.

## Security & compliance controls

Mapped loosely to SOC 2 Trust Services Criteria / HIPAA Security Rule safeguards.

* **Encryption**: PHI free text (narratives, reviewer notes) is field-level encrypted with ASP.NET Data Protection; the key ring is stored in the DB and **must** be protected with a certificate outside Development (`DataProtection:CertificatePath`; startup refuses otherwise). TLS everywhere (HTTPS redirect, HSTS, `__Host-` Secure cookies).
* **Authentication**: PBKDF2 hashes, 14+ char passwords, lockout after 5 failures, confirmed email required, TOTP 2FA with recovery codes, **mandatory 2FA for Admin/Super Admin**. Login, reset and registration responses don't reveal whether an email exists.
* **Sessions**: HttpOnly/Secure/SameSite=Strict cookie, 20-minute sliding idle timeout, security stamp re-checked every minute so suspensions, role changes and password/email changes end sessions quickly.
* **Authorization**: role checks on every staff controller; reporters can read only their own reports; Admins can't touch staff accounts; nobody can change their own role/status; last Super Admin protected.
* **Audit**: append-only `AuditLog` (Postgres triggers block UPDATE/DELETE/TRUNCATE) covering logins, failures, lockouts, MFA, password/email/role/suspension changes, report submission, staff reads of PHI, merges, and exports. No narrative content is ever logged.
* **Web hardening**: strict CSP (no inline script/style), `no-store` on dynamic pages, anti-forgery on all POSTs, rate limiting (reports, auth), honeypot, CSV-injection-safe export, reset/confirm links built from `App:PublicBaseUrl` (not the Host header).
* **Data minimisation**: the injured person's name is never collected; the form tells reporters not to put names in the narrative.

### Production checklist (not things code can do for you)

1. Set `ConnectionStrings:Default` with `SSL Mode=VerifyFull`, a **least-privilege app role** (no DDL; apply `db/schema.sql` with a separate migration role, and `REVOKE TRUNCATE`), and Postgres/volume encryption at rest plus encrypted backups.
2. Set `DataProtection:CertificatePath` (+ password via secret store), `App:PublicBaseUrl`, `Smtp:*`, and `Proxy:KnownProxies` if behind a load balancer. Set `Database:MigrateOnStartup=false` in prod.
3. Keep secrets in a vault / environment, not `appsettings`. Remove `Seed:SuperAdminPassword` after first boot.
4. SOC 2 / HIPAA also need organisational controls: BAAs with hosting/email vendors, centralised log shipping and alerting from the audit table, retention/deletion policy, access reviews, backup restore tests, pen testing, incident response.

### Operations (`deploy/`)

* `provision.sh`, `release.sh`, `switch-domain.sh`: first-time server setup, repeatable releases, per-subdomain nginx + TLS.
* `harden-monitor.sh` + `ops/`: `fail2ban` (ssh + repeated failed app sign-ins), persistent 90-day journal, unattended security updates with a 09:00 UTC reboot when required, and email alerts through SES:
  * health check every 5 min (site, services, disk, memory, TLS expiry, stuck reboot): alerts once, repeats every 6 h, sends an all-clear;
  * audit-log watcher every 10 min: role/suspension/MFA changes, exports, account deletions, lockouts, bursts of failed sign-ins (never report content).
  The health check runs *on* the server, so it can't see an AWS-firewall or DNS outage; add an external uptime monitor for that.
* `backup-setup.sh` + `ops/ir-backup.sh`: nightly **age-encrypted** archive (database, roles, app secrets and the Data Protection certificate) to a private, versioned S3 bucket with a write-only IAM user, plus daily Lightsail snapshots. A missing backup alerts. See **`deploy/RESTORE.md`** for the restore runbook and key handling.
* CI (`.github/workflows/ci.yml`) builds and runs every test including the Postgres integration tests against a Postgres service container, and fails if those were skipped.

### Known gaps / decisions for you

* No automated retention/purge: deleting reports after N years is a policy decision. Account self-deletion exists; report deletion does not.
* Small-count suppression is per cell. Totals and neighbouring cells can still hint at hidden values, so share only aggregate figures.
* The backup private key lives only with the operator; losing it makes the S3 archives unreadable (Lightsail snapshots still work).
* Data Protection keys + PHI columns share one database; for stronger separation use a KMS-backed key store.

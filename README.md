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
dotnet test                                # 29 tests: duplicate logic, merges, user guards, encryption, full HTTP pipeline
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
| Analytics | By discipline, injury type, severity, month, event kingdom, injured person's kingdom; discipline × injury-type matrix; date/discipline/kingdom filters; de-identified CSV export. |

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

### Known gaps / decisions for you

* No QR code for 2FA (shows the key and `otpauth://` URI) to avoid a third-party library.
* No hard-delete of users or reports (retention is a policy decision); user moderation is suspend/reinstate.
* Analytics shows small counts as-is; consider small-cell suppression before sharing statistics outside the safety team.
* Data Protection keys + PHI columns share one database; for stronger separation use a KMS-backed key store.

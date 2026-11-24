# API Scheduler

Runs scheduled API calls on a Windows Server. Each job runs a SQL `SELECT`, turns each returned row into a JSON body and `POST`s it to an API. After each call it marks the row as sent, so no row is sent twice.

| Component | What it is |
|---|---|
| `ApiScheduler.Service` | Windows Service. It reads the jobs, runs them on schedule, sends the calls and updates the rows. It restarts automatically if it fails. |
| `ApiScheduler.Desktop` | WPF admin app. Add and edit jobs, database connections, the column→JSON mapping and the API key. Preview the query and JSON, start a run now, view history, and start or stop the service. |
| `ApiScheduler.Core` | Shared logic: config, encryption, SQL safety checks, JSON builder, HTTP sender and job runner. |

Built with **.NET 10 (LTS)** and published **self-contained**, so the server needs no .NET installed. Supported on Windows Server 2019 and 2022.

## How a run works

1. The service checks every 15 s whether a job is due (schedule or **Run now**).
2. It runs the job's `SELECT` and reads up to *Max rows per run* rows.
3. For each row it builds the JSON body from the mapping and sends `POST` with header `apiKey: <key>` and `Content-Type: application/json; charset=utf-8`.
4. It updates that row right away:
   - **2xx** → `SENT_CALL = 'SUCCESS'`, `SENT_DATE = GETDATE()`
   - **5xx / timeout / network** → `SENT_CALL = 'FAILED-xxx'`, `SENT_DATE` stays `NULL`, so it **retries next run**.
   - **4xx (bad data)** → `SENT_CALL = 'REJECTED-xxx'`, `SENT_DATE` stays `NULL`. The template query excludes these rows, so bad rows don't fail every 8 hours. Fix the data and set `SENT_CALL = NULL` to send the row again.
5. If 5 calls in a row fail with network errors, the run stops early. The remaining rows wait for the next run.

The update uses `WHERE key = @id AND SENT_DATE IS NULL`. A row is marked right after its own call, so a crash mid-run can re-send at most the one row that was in flight.

**Schedule:** every *N* hours, optionally anchored to a time of day. For example, 8 h anchored at `00:00` runs at 00:00, 08:00 and 16:00 server time. The last run time is saved, so a reboot doesn't reset the schedule.

## Survey SMS template

**New from Survey SMS template** creates a job that is already set up for `NG_MC_SURVEY_SMS_LOG`:

```sql
SELECT * FROM dbo.NG_MC_SURVEY_SMS_LOG
WHERE SENT_DATE IS NULL AND (SENT_CALL IS NULL OR SENT_CALL NOT LIKE 'REJECTED%')
ORDER BY INSERTIONORDERID
```

| JSON field | Source |
|---|---|
| `serviceName` | fixed `1` (number) |
| `SurveyType` | fixed `CSAT` |
| `channel` | fixed `SMS` |
| `trigger` | `TRIGGER_NAME` |
| `policyHolderName`, `policyHolderID`, `memberName`, `policyNo`, `mobileNo`, `claimNo`, `email`, `gender`, `clientType`, `segment`, `city`, `brokerName`, `repairFacility` | the matching column |
| `language` | `COMM_LANGUAGE` |

## Security

| Area | What is done |
|---|---|
| **API key** | Encrypted with **Windows DPAPI (machine scope)** and only decryptable on that server. A key can't be stored only as a *hash*, because the service has to send the real key and a hash is one-way. So the key is **encrypted**, and a **SHA-256 hash (fingerprint)** is also stored and shown in the UI so you can check which key is set. The key is never shown again, never logged, and typed into a password box that isn't data-bound. |
| **SQL password** | Same as the API key (DPAPI), when SQL authentication is used. **Windows authentication is the default** and stores no password. |
| **Config folder** | `%ProgramData%\ApiScheduler` is limited to SYSTEM, Administrators and the service account. Inherited permissions are removed. |
| **Desktop app** | Needs Administrator (UAC prompt), because it edits the locked config and controls the service. |
| **SQL injection** | The query is parsed with Microsoft's T-SQL parser (ScriptDom). Only **one** `SELECT` is allowed: no second statement, `GO`, `INTO`, `EXEC`, `OPENROWSET`, `OPENQUERY` or `OPENDATASOURCE`. Table and column names for the update are checked and bracket-quoted. Values are always parameters. |
| **TLS / HTTP** | HTTPS is required (plain HTTP is allowed only for `localhost` testing). Certificate checking is **never disabled**. For an internal CA that the server doesn't trust, you can *pin* the exact certificate thumbprint, and the host name must still match. Redirects are not followed, so the key can't leak to another host. No cookies. Timeouts are bounded. |
| **SQL transport** | `Encrypt=Mandatory` by default. "Trust server certificate" is an explicit opt-in. |
| **Personal data in logs** | Request bodies (names, mobile numbers) are **never logged**. Only the row key, HTTP status and a short error are logged. Logs are kept for 30 days. |
| **Least privilege** | The service runs as a virtual account or gMSA, not LocalSystem. Give its SQL login only `SELECT, UPDATE` on the tables it uses. |
| **Config writes** | Writes are atomic (temp file then replace) and use a cross-process lock, so a crash can't corrupt the config. |

## Build & deploy

On a dev machine (needs the .NET 10 SDK):

```powershell
.\scripts\publish.ps1
```

This runs the tests and creates `publish\ApiScheduler\` with `Service\`, `Desktop\`, `install.ps1` and `uninstall.ps1`.

On the server, copy that folder over and run this in an **elevated** PowerShell:

```powershell
.\install.ps1                                         # virtual account NT SERVICE\ApiScheduler
.\install.ps1 -ServiceAccount 'CORP\gmsa-apisched$'   # or a gMSA (recommended in a domain)
.\install.ps1 -ServiceAccount 'CORP\svc-apisched'     # or a domain user (prompts for password)
```

The installer copies the program to `C:\Program Files\ApiScheduler`. It registers the service with **delayed auto-start** and **automatic restart on failure**, locks down the data folder, adds a Start Menu shortcut and starts the service. Running it again upgrades in place and keeps your config.

**SQL login for the service (Windows auth):**
- **Virtual account:** the login is `DOMAIN\SERVERNAME$`. If SQL Server is on the same machine, use `NT SERVICE\ApiScheduler`.
- **gMSA or domain user:** use that account.

```sql
CREATE LOGIN [CORP\APPSRV01$] FROM WINDOWS;
-- in the database:
CREATE USER [CORP\APPSRV01$] FOR LOGIN [CORP\APPSRV01$];
GRANT SELECT, UPDATE ON dbo.NG_MC_SURVEY_SMS_LOG TO [CORP\APPSRV01$];
```

**First-time setup in the app:**
1. **Connections… → + New**: enter server and database, choose Windows auth, then **Test connection** and **Save**.
2. **+ New from Survey SMS template**.
3. On **General**, paste the API key.
4. On **Query & JSON body**, click **Run preview** and check the JSON.
5. Tick **Enabled**, then **Save**.

To uninstall: `.\uninstall.ps1`. Add `-RemoveData` to also delete the config, history and logs.

## Files on the server

| Path | Content |
|---|---|
| `%ProgramData%\ApiScheduler\config.json` | Jobs and connections (secrets encrypted) |
| `%ProgramData%\ApiScheduler\state.json` | Last and next run per job, service heartbeat |
| `%ProgramData%\ApiScheduler\history.jsonl` | One line per run (rolls over at 5 MB) |
| `%ProgramData%\ApiScheduler\logs\` | Daily log files (30 days); warnings and errors also go to the Windows Event Log (source `ApiScheduler`) |

> DPAPI machine keys belong to the server. A `config.json` copied to another server can't decrypt its secrets, so re-enter the API key there.

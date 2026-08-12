# PgBackupManager

A Windows desktop application for backing up and restoring **PostgreSQL and
SQL Server** databases — built for anyone who needs a safe, visual way to run
backups and restores without memorizing command-line flags.

Point it at a database, pick what you want backed up, and it does the rest:
correct flags, timestamped files, a live progress log, and a clear picture of
what a restore will actually do to the target database *before* you run it.

---

## Contents

- [What it does](#what-it-does)
- [Installing](#installing)
- [First-time setup](#first-time-setup)
- [Using the app](#using-the-app)
  - [1. Add a connection profile](#1-add-a-connection-profile)
  - [2. Take a backup (PostgreSQL)](#2-take-a-backup-postgresql)
  - [3. Restore a backup (PostgreSQL)](#3-restore-a-backup-postgresql)
  - [4. Back up a SQL Server database](#4-back-up-a-sql-server-database)
  - [5. Restore a SQL Server database](#5-restore-a-sql-server-database)
  - [6. Notifications](#6-notifications)
  - [7. Retention cleanup](#7-retention-cleanup)
- [Safety features](#safety-features)
- [Troubleshooting](#troubleshooting)
- [For developers](#for-developers)

---

## What it does

PgBackupManager is a front end for **PostgreSQL's own backup tools**
(`pg_dump` / `pg_restore` / `psql`) **and native SQL Server backup/restore**
(`BACKUP DATABASE` / `RESTORE DATABASE`, talked to directly — no extra tools
to install for the SQL Server side). It doesn't reinvent how either engine's
backups work — it makes the process visible and hard to get wrong:

- **Backup** a full database, specific schemas, or specific tables (PostgreSQL)
  — or a whole database (SQL Server) — in whichever archive format you need,
  with a live log and a time estimate instead of a blank terminal.
- **Restore** a backup file into any target, but only after showing you
  exactly what's new, what already exists, and what will actually change —
  not just "restore started."
- **Manage connections** to as many databases as you work with — PostgreSQL
  and SQL Server side by side in one Profiles list — with passwords encrypted
  on disk instead of stored in plain text.
- **Get notified** when a long-running backup or restore finishes, even if
  you've minimized the app to work on something else.

---

## Installing

### Option A — Installer (recommended)

Download **[`installer/dist/PgBackupManager-Setup-2.2.0.exe`](installer/dist/PgBackupManager-Setup-2.2.0.exe)**
from this repo and run it. It installs the app with a Start Menu shortcut
and an uninstaller — nothing else on the machine is required to *run*
PgBackupManager itself (the .NET runtime is bundled inside the installer).

If you're browsing on GitHub: open that path, click **View raw** (or the
download icon), and save the `.exe`.

**One real prerequisite (PostgreSQL side only):** PgBackupManager calls
PostgreSQL's own command-line tools (`pg_dump.exe`, `pg_restore.exe`,
`psql.exe`) — it does not bundle them. If this machine already has
PostgreSQL (or just its client tools) installed, PgBackupManager finds them
automatically. If not, install PostgreSQL first (or just the "command line
tools" component), then see [First-time setup](#first-time-setup) below to
point the app at them. **SQL Server backup/restore needs nothing extra** — it
talks to SQL Server directly, so there's no client tool to install or configure.

### Option B — Run from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download) (Windows).

```powershell
dotnet run --project PgBackupManager.UI/PgBackupManager.UI.csproj
```

---

## First-time setup

Open the **Settings** tab when you launch the app for the first time.

1. **PostgreSQL Client Tools** — the app auto-detects `pg_dump`, `pg_restore`
   and `psql` under `C:\Program Files\PostgreSQL\<version>\bin` or your system
   `PATH`. If the detected paths look wrong (or say "not found"), set **Bin
   Folder Override** to the folder that actually contains `pg_dump.exe`.
2. **Default Folders** — set a **Default Backup Root** (where new backups are
   saved) and a **Default Restore Source** (where the "Browse"/"Use latest"
   buttons on the Restore tab look for files). Ticking **"Use auto Y/M/D
   folder structure under backup root"** organizes backups automatically into
   `<root>\<database>\<year>\<month>\<day>\`.
3. **Notifications** — decide whether you want a pop-up notification and a
   flashing taskbar icon when a backup/restore finishes (see
   [Notifications](#4-notifications)).
4. Click **Save Settings**.

Then go to the **Profiles** tab and add your first database connection.

---

## Using the app

### 1. Add a connection profile

**Profiles** tab → **ADD**. Fill in:

| Field | Meaning |
|---|---|
| Profile Name | Whatever you want to call it — shown everywhere else in the app |
| Engine | **PostgreSQL** or **SQL Server** — picks which fields apply and which tabs (Backup/Restore vs. MSSQL Backup/Restore) the profile shows up in |
| Host / Port | Where the server is. Port defaults to 5432 (PostgreSQL) / 1433 (SQL Server) and flips automatically when you change Engine, unless you've already typed your own |
| Database | The database name |
| User / Password | PostgreSQL: always used. SQL Server: only used when "Use Windows Authentication" is unchecked — otherwise it connects as the account running PgBackupManager. Password is encrypted with Windows DPAPI and can only be decrypted by your own Windows user account on this machine |
| Default Schema | PostgreSQL only, optional and informational |

Use **Test Connection** to confirm it connects before relying on it. **EDIT**
and **DELETE** work on whichever profile is selected in the list. The
Profiles list shows a **PG** / **MSSQL** badge next to each name so a mixed
list stays easy to scan.

### 2. Take a backup (PostgreSQL)

Go to the **Backup** tab.

1. Pick a **Profile**, click **Load DB Objects** — this reads the live schema
   list (tables, views, functions, sequences, types) from the database.
2. Choose what to back up:
   - **Scope** — *Full database*, *Selected schemas*, or *Selected tables*.
     Ticking a schema in the tree below automatically switches Scope to
     "Selected schemas"; ticking an individual table switches it to
     "Selected tables."
   - **Format** — *Custom (.dump)* (recommended: compressed, supports
     selective restore), *Plain SQL*, *Tar*, or *Directory (parallel)*.
   - **Content** — *Schema + Data*, *Schema only*, or *Data only*.
   - **Jobs** — number of parallel workers. Only works with the *Directory
     (parallel)* format (this is a PostgreSQL restriction, not a UI
     limitation) — larger databases finish noticeably faster with this on.
3. If backing up multiple schemas, you can tick **One file per schema**
   (next to the destination folder) to get a separate `.dump` file per
   schema instead of one combined file — still landing in the same
   destination folder.
4. Set the **Destination Root** (or use the folder picked up from Settings).
   The full output path is previewed live above the log.
5. Click **Start Backup**. The footer shows elapsed time, an estimated time
   remaining (based on actual table sizes, queried up front), and a progress
   bar. **Cancel** stops the run cleanly.

When it finishes you'll see a `>> SUCCESS` line in the log with the final
file size and duration, plus a notification if you've enabled those (see
below).

### 3. Restore a backup (PostgreSQL)

Go to the **Restore** tab. This is the part worth reading carefully — a
restore changes a real database.

1. **Pick the backup file** — Browse for a single file, Folder for a
   directory-format backup, or tick **Use latest** to auto-fill the most
   recently written backup under your Default Restore Source. *Always glance
   at the filename before restoring* — "latest" only means "newest file on
   disk," which matters if that folder ever holds backups from more than one
   database.
2. **Pick the target profile** — shown immediately below in a banner colored
   **green** for a local target ("safe") or **orange** for a remote/network
   target ("double-check before restoring").
3. Click **Analyze**. This compares the backup file against the live target
   database and fills in:
   - **Schemas to restore** — checkboxes for every schema present in the
     backup file, each showing how many objects it contains. Only ticked
     schemas are restored.
   - Five summary cards:
     - **NEW** — in the backup but not yet in the target (will be created).
     - **EXISTING** — in both — restoring will hit "already exists" unless
       you drop first.
     - **MISSING FROM BACKUP** — in the target's ticked schemas but not in
       this backup (informational; restoring can't bring these back).
     - **WILL RESTORE** — total size of the operation (every object + all
       its data, indexes, constraints, sequences in the ticked schemas).
     - **CHANGES TO TARGET** — what will *actually* happen given your
       current options below (e.g. "12 created, 3 already there,
       untouched" or "blocked — tick Drop existing first").
   - A read-only preview table of every object and its status, filterable by
     name/kind and by clicking the NEW/EXISTING/MISSING cards.
4. **Restore options:**
   - **Single transaction** — if anything fails, the *entire* restore rolls
     back (recommended; on by default).
   - **Drop existing first (`--clean --if-exists`)** — drops matching
     objects before recreating them. Needed if you're restoring into a
     database that already has some of these objects.
   - **--no-owner / --no-privileges** — skip restoring the original owner and
     grants, useful when the target has different roles than the source.
   - **Parallel jobs** — like backup, speeds up large restores, but
     PostgreSQL doesn't allow it together with Single transaction — untick
     that first if you want to use it.
5. Click **Start Restore**. You'll get a confirmation dialog stating exactly
   which schemas, how many objects, and which target database this is about
   to touch — remote targets get an extra, more insistent warning. Nothing
   runs until you confirm.
6. Once it succeeds, the app automatically **re-analyzes** the target so the
   cards and table reflect what's really there now — you don't have to
   click Analyze again to confirm the restore actually did something.

If the target database doesn't exist yet on the server, use **Create Target
DB** next to the target banner to create an empty one before restoring
into it.

### 4. Back up a SQL Server database

Go to the **MSSQL Backup** tab. This runs `BACKUP DATABASE` straight over the
connection — no `pg_dump`-style scoping here, since SQL Server's backup unit
is always the *whole* database, in one file.

1. Pick a **Profile** (only SQL Server–engine profiles show up in this list).
2. Set the **Destination Root** (Browse, or use auto Y/M/D folders same as
   the PostgreSQL tab). The full output path is previewed live.
3. Click **Start Backup**. Progress comes straight from SQL Server's own
   `WITH STATS` percentage messages, so the bar and ETA are accurate, not
   estimated.

**Where the file actually goes:** the `.bak` file is written by the **SQL
Server engine process itself**, not by PgBackupManager — so the destination
folder needs to be reachable by *that* process. For a local server this is
trivial (any folder on the same machine SQL Server can write to). For a
**remote** SQL Server profile, the destination must be a path that server's
own service account can reach — typically a UNC share (`\\server\share\...`),
not a folder that only exists on the PC running PgBackupManager.

### 5. Restore a SQL Server database

Go to the **MSSQL Restore** tab.

1. **Browse** to a `.bak` file and pick the **target profile**. The same
   green (local)/orange (remote) banner from the PostgreSQL Restore tab
   applies here too — remote targets get the same UNC-path caveat as backup.
2. Click **Analyze**. This reads the backup's own header
   (`RESTORE HEADERONLY` / `FILELISTONLY`) — no restore happens yet — and
   shows the original database name, when it was backed up, and how many
   files it contains.
3. Set **Restore as database name** (defaults to the backup's original name,
   editable — e.g. to restore alongside an existing copy under a different
   name). If that name already exists on the target server, you'll see a
   warning and must tick **Overwrite existing (WITH REPLACE)** to proceed;
   otherwise the restore is blocked before it starts.
4. Click **Start Restore**. A confirmation dialog states the exact target and
   whether an existing database will be replaced — remote targets get the
   same insistent warning as the PostgreSQL tab. Data/log files are placed
   automatically: onto the existing database's current files when
   overwriting, or under the server's default data folder for a fresh
   restore — no manual `MOVE` clause needed.

### 6. Notifications

**Settings → Notifications** controls two independent things that fire when
a backup or restore finishes (success, failure, or error):

- **A snackbar-style pop-up** in the bottom-right corner of your screen,
  auto-dismissing after a duration you choose. It appears even if the main
  window is minimized.
- **A flashing taskbar icon**, so you notice even if you're focused on
  another window.

Use **SEND TEST NOTIFICATION** on the Settings page to preview both before
committing to a duration.

### 7. Retention cleanup

**Settings → Retention Policy** lets you set how many days of backups to
keep under your Default Backup Root. It shows a live preview of how many
files are currently eligible for deletion, and **RUN CLEANUP NOW** deletes
them — nothing is deleted automatically in the background.

---

## Safety features

PgBackupManager is built around one idea: you should never be surprised by
what a restore did. Concretely:

- **Local vs. remote coloring** everywhere a target database is shown.
- **Pre-flight checks** before a restore even starts — e.g. it refuses to let
  you run a restore that's *guaranteed* to fail (existing objects +
  Single transaction + no Drop) and tells you exactly which option to change
  instead of letting you find out the hard way.
- **A real confirmation dialog** before every restore, stating the schemas,
  object count, and exact target — not a generic "Are you sure?".
- **Automatic re-analysis after a successful restore**, so the numbers you
  see always reflect the real state of the target, not a stale pre-restore
  snapshot.
- **Encrypted passwords** (Windows DPAPI, tied to your Windows user account)
  — never stored or displayed in plain text.

---

## Troubleshooting

**"pg_dump.exe / pg_restore.exe not found"** — Settings → PostgreSQL Client
Tools → set Bin Folder Override to the folder containing `pg_dump.exe`
(typically `C:\Program Files\PostgreSQL\<version>\bin`).

**A restore says SUCCESS almost instantly but nothing changed** — this
usually means the ticked schemas didn't exist yet in a brand-new target
database. PgBackupManager creates any missing schemas automatically before
restoring, so this shouldn't happen anymore — if you still see it, re-run
**Analyze** first (the schema list shown must come from the same file you're
about to restore).

**"unrecognized configuration parameter" or "unsupported version in file
header" errors** — the `pg_dump`/`pg_restore` version in use doesn't match
the server (or the archive) closely enough. Make sure the client tools
configured in Settings are the same major version as the PostgreSQL server
you're connecting to.

**Parallel jobs field won't take effect** — Backup: parallel jobs only work
with the *Directory (parallel)* format. Restore: parallel jobs and *Single
transaction* can't be combined — PostgreSQL itself refuses that combination.

**MSSQL Backup fails with "Cannot open backup device ... Access is denied"**
— the destination folder isn't writable by the *SQL Server service account*
(not your Windows user). Point the destination at a folder SQL Server itself
already writes to (e.g. its own default Backup folder under
`...\MSSQL\Backup\`), or grant that service account write access to your
chosen folder.

**MSSQL Restore fails with "backup set holds a backup of a database other
than the existing X database"** — a database named exactly `X` already
exists on the target and its identity doesn't match this backup. Tick
**Overwrite existing (WITH REPLACE)** on the MSSQL Restore tab, or restore
under a different **Restore as database name**.

---

## For developers

```
PgBackupManager.Core/   No-UI engine — pg_dump/pg_restore process runners,
                         Npgsql-based object inspectors/diffing, native SQL
                         Server BACKUP/RESTORE runners (Microsoft.Data.SqlClient,
                         no external tool), DPAPI secret store, settings,
                         filename/retention logic.
PgBackupManager.UI/     WPF front-end (MVVM, CommunityToolkit.Mvvm), custom
                         theme, notification toast, Inno Setup installer
                         script under installer/.
```

Build:

```powershell
dotnet build PgBackupManager.slnx -c Release
```

Run from source:

```powershell
dotnet run --project PgBackupManager.UI/PgBackupManager.UI.csproj
```

Publish a self-contained single-file build (no .NET runtime needed on the
target machine):

```powershell
dotnet publish PgBackupManager.UI/PgBackupManager.UI.csproj -c Release -p:PublishProfile=win-x64
```

Build the installer (requires [Inno Setup 6](https://jrsoftware.org/isinfo.php)):

```powershell
ISCC installer\PgBackupManager.iss
```

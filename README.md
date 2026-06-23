# Database BackStore Manager (PgBackupManager)

A Windows desktop app (WPF / .NET 8) for backing up and restoring PostgreSQL
schemas, tables, functions and types with a friendly UI — built around
`pg_dump` / `pg_restore`.

## Features

- **Connection profiles** — host/port/db/user; passwords encrypted with Windows
  DPAPI (per-user) in `%AppData%\PgBackupManager\profiles.json`.
- **Backup** — pick a profile, load the live object catalog, choose scope
  (full DB / selected schemas / selected tables), format (custom / plain / tar)
  and content (schema+data / schema-only / data-only). Auto Y/M/D destination
  folders and timestamped filenames. Live `pg_dump` log.
- **Restore** — pick a backup file + a **target** profile (clearly shown, with a
  local-vs-remote safety colour), analyze it against the live DB to get an
  object-level **diff** (NEW / EXISTING / MISSING-from-backup), then restore the
  selected objects. "Create target DB" helper, schema-include checkboxes,
  single-transaction restore.
- **Settings** — PG client-tools auto-detection (with override), default folders,
  retention policy + cleanup.

## Project layout

```
PgBackupManager.Core/        # No-UI engine: pg_dump/pg_restore runners, Npgsql
│                            #   inspectors, DPAPI secret store, diff, settings
└── PgBackupManager.UI/      # WPF (custom themed) front-end (MVVM)
```

## Requirements

- .NET 8 SDK (Windows desktop)
- PostgreSQL client tools (`pg_dump.exe`, `pg_restore.exe`, `psql.exe`) —
  auto-detected under `C:\Program Files\PostgreSQL\*\bin`.

## Run

```powershell
dotnet run --project PgBackupManager.UI/PgBackupManager.UI.csproj
```

## Build

```powershell
dotnet build PgBackupManager.slnx -c Release
```

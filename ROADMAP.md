# PgBackupManager — Capabilities, Limits & Roadmap

_Updated 2026-09-30 · version 3.0.0_

---

## 1. What the tool does (3.0)

| Area | Feature |
|---|---|
| **Engines** | PostgreSQL, SQL Server, Oracle (managed ODP.NET — no client install), MySQL / MariaDB, SQLite. One `IDbProvider` per engine behind a shared canonical type model. |
| **Connections** | All five engines; DPAPI-encrypted passwords; Oracle service name or SID; SQLite file picker; extra connection-string options; test before saving. |
| **SQL Editor** | Multi-tab AvalonEdit editor, SQL highlighting (theme-aware), autocomplete (keywords / objects / columns), run selection / all / statement at cursor, EXPLAIN on all engines, auto-commit or manual transactions, NOTICE / PRINT capture, error line jump, object explorer (lazy), query history, result grids (virtualized), export grid / full result (CSV, TSV, JSON, INSERT script), CSV import, run big script files without opening them, drag & drop `.sql`. |
| **Scripts & dumps** | Script splitter understands psql (`$$`, `\` meta, `COPY ... FROM stdin` data blocks), SSMS `GO`, SQL*Plus `/` + PL/SQL blocks, MySQL `DELIMITER`, SQLite trigger bodies — so plain `pg_dump` / `mysqldump` / Oracle / T-SQL scripts run in-app. |
| **Transfer** | Any engine → any engine: tables (type-mapped DDL + bulk load: binary COPY, SqlBulkCopy, Oracle array binding, multi-row INSERT), modes create/append · truncate+load · upsert by PK · drop+recreate · structure only, identifier-case mapping, row filter, commit-every-N, dry run, per-table progress, compare, PG sequence resync, transfer script, presets. Code objects via the converter (script-only or applied). |
| **Code Converter** | Oracle PL/SQL → PL/pgSQL (packages → schema per package, procedures/functions, triggers → trigger fn + CREATE TRIGGER, sequences, views, tables, indexes, object types, cursors, exceptions, NVL/NVL2/DECODE/SYSDATE/TO_DATE/ADD_MONTHS/LISTAGG/seq.NEXTVAL/RAISE_APPLICATION_ERROR/DBMS_OUTPUT...). T-SQL → PL/pgSQL (heuristic statement segmentation, IF/WHILE/TRY-CATCH/cursors/variables/EXEC). MySQL → PG. PG → Oracle / SQL Server / MySQL / SQLite for views/DDL/DML. Other pairs go through PG. |
| **PostgreSQL** | Backup (all pg_dump formats, scopes, parallel), Restore with pre-restore diff, Copy Schema, Sync (upsert/mirror/full refresh) — unchanged from 2.x. |
| **SQL Server** | Native BACKUP / RESTORE, MSSQL → PG import + compare — unchanged from 2.x. |
| **Engine Backup** | SQLite online backup/restore; MySQL mysqldump + in-app restore; Oracle Data Pump expdp/impdp (DIRECTORY listing, remap schema, table-exists action). |
| **Automation** | Presets (transfer / backup / script), Windows Task Scheduler (daily / weekly / hourly / once), headless `--run-preset` CLI with exit codes, History page with per-run log files (all pages record there). |
| **Dependencies** | Scan + one-click download of PostgreSQL client tools and Oracle Instant Client (basic + tools + sqlplus); detection of mysqldump, sqlcmd, LocalDB with links. |
| **UI** | Sidebar navigation grouped by task, remembers last page, Light / Dark / System + 8 accents switched live, thin scrollbars, themed grids/editors, title-bar theme toggle, crash guard. |

## 2. Verified

- 27 unit tests (splitter, converters, type mapping, coercion, connection strings).
- 46 live end-to-end checks on local PostgreSQL 18, SQL Server 2022 and SQLite: MSSQL→PG (types incl. unicode, decimal, bit, uuid, bytea, µs timestamps), converted view + procedure executed on PG, upsert idempotency, truncate+load, PG→SQLite, SQLite→MSSQL round-trip (identical SUMs), compare, script executor (`$$`, COPY stdin, NOTICE, truncation, errors), CSV export/import round-trip, SQLite backup, Oracle package converted **and executed** on PG, Task Scheduler create/query/delete.
- Real app driven headless: editor query + explorer, Transfer object load (241 tables / 735 code objects), converter, theme switching; release single-file publish + CLI exit codes.

**Not verified live (no server available here):** Oracle and MySQL connections/transfers/Data Pump/mysqldump — code paths are implemented and compile; first real use should be a dry run.

## 3. Limits that remain (by nature, not by omission)

- **Proprietary binary backups** — MSSQL `.bak` and Oracle `.dmp` can only be read by their own engine. The tool drives that engine (MSSQL restore, Data Pump) but can't parse the files itself.
- **Code conversion is assisted, not complete** — typical Oracle packages convert ~80–90% automatically; package state, autonomous transactions, BULK COLLECT/FORALL, CONNECT BY, `(+)` joins, pipelined functions and DBMS_* calls are flagged for manual work. T-SQL procedures are translated heuristically (no mandatory `;` in T-SQL) and must be reviewed.
- **Cross-engine transfer moves tables + PK + NOT NULL**; secondary indexes, foreign keys, check constraints and defaults are not recreated across engines (same-engine PG→PG: use Sync / Copy Schema for full fidelity).
- **Oracle Data Pump files live on the DB server** (DIRECTORY object) — not on the PC.
- **Scheduled tasks run while the user is logged on** (no stored Windows password).
- **Installer 3.0.0** needs Inno Setup to build (not installed on this machine).

## 4. Next ideas

1. Foreign keys + secondary indexes in cross-engine transfer (second pass after data load).
2. Converter: CONNECT BY → WITH RECURSIVE, `(+)` → ANSI joins, BULK COLLECT → array_agg.
3. Result-grid inline editing (by primary key) and a data diff view.
4. Visual schema diff between any two connections (not just PG).
5. SSH tunnel / SSL certificate options in the profile editor.
6. Optional Oracle/MySQL integration tests via Docker/Testcontainers.

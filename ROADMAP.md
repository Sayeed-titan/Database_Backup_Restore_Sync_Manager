# PgBackupManager — Capabilities, Limits & Roadmap

_Updated 2026-09-30 · version 3.0.0_

---

## 1. What the tool does (3.0)

| Area | Feature |
|---|---|
| **Engines** | PostgreSQL, SQL Server, Oracle (managed ODP.NET — no client install), MySQL / MariaDB, SQLite. One `IDbProvider` per engine behind a shared canonical type model. |
| **Connections** | All five engines; DPAPI-encrypted passwords; Oracle service name or SID; SQLite file picker; extra connection-string options; test before saving. |
| **SQL Editor** | Edit results in place (single-table SELECT with its primary key: edit / add / delete rows, saved in one transaction keyed on the original key, stale rows detected), multi-tab AvalonEdit editor, SQL highlighting (theme-aware), autocomplete (keywords / objects / columns), run selection / all / statement at cursor, EXPLAIN on all engines, auto-commit or manual transactions, NOTICE / PRINT capture, error line jump, object explorer (lazy), query history, paged result grids (page size 100–5,000, absolute row numbers, next pages fetched from the server via LIMIT/OFFSET or OFFSET…FETCH, COUNT ALL), export grid / full result (CSV, TSV, JSON, INSERT script), CSV import, run big script files without opening them, drag & drop `.sql`. |
| **Scripts & dumps** | Script splitter understands psql (`$$`, `\` meta, `COPY ... FROM stdin` data blocks), SSMS `GO`, SQL*Plus `/` + PL/SQL blocks, MySQL `DELIMITER`, SQLite trigger bodies — so plain `pg_dump` / `mysqldump` / Oracle / T-SQL scripts run in-app. |
| **Schema Compare** | Any two schemas on any two connections: tables, columns (type/length/nullability), PKs, indexes, FKs, views/routines/sequences (definitions too when same engine); side-by-side definitions; sync script in the target dialect (drops commented out) opened in the SQL Editor. |
| **Transfer** | Any engine → any engine: tables (type-mapped DDL + bulk load: binary COPY, SqlBulkCopy, Oracle array binding, multi-row INSERT), modes create/append · truncate+load · upsert by PK · drop+recreate · structure only, identifier-case mapping, row filter, commit-every-N, dry run, per-table progress, compare, PG sequence resync, transfer script, presets. Code objects via the converter (script-only or applied). |
| **Code Converter** | Oracle PL/SQL → PL/pgSQL (packages → schema per package, procedures/functions, triggers → trigger fn + CREATE TRIGGER, sequences, views, tables, indexes, object types, cursors, exceptions, NVL/NVL2/DECODE/SYSDATE/TO_DATE/ADD_MONTHS/LISTAGG/seq.NEXTVAL/RAISE_APPLICATION_ERROR/DBMS_OUTPUT...). T-SQL → PL/pgSQL (heuristic statement segmentation, IF/WHILE/TRY-CATCH/cursors/variables/EXEC). MySQL → PG. PG → Oracle / SQL Server / MySQL / SQLite for views/DDL/DML. Other pairs go through PG. |
| **PostgreSQL** | Backup (all pg_dump formats, scopes, parallel), Restore with pre-restore diff, Copy Schema, Sync (upsert/mirror/full refresh) — unchanged from 2.x. |
| **SQL Server** | Native BACKUP / RESTORE, MSSQL → PG import + compare — unchanged from 2.x. |
| **Engine Backup** | SQLite online backup/restore; MySQL mysqldump + in-app restore; Oracle Data Pump expdp/impdp (DIRECTORY listing, remap schema, table-exists action). |
| **Automation** | Presets (transfer / backup / script), Windows Task Scheduler (daily / weekly / hourly / once), headless `--run-preset` CLI with exit codes, History page with per-run log files (all pages record there). |
| **Dependencies** | Scan + one-click download of PostgreSQL client tools and Oracle Instant Client (basic + tools + sqlplus); detection of mysqldump, sqlcmd, LocalDB with links. |
| **UI** | Sidebar navigation grouped by task, remembers last page, Light / Dark / System + 8 accents switched live, thin scrollbars, themed grids/editors, title-bar theme toggle, crash guard. |

## 2. Verified

- 64 unit tests (incl. paging SQL, FK/index DDL per dialect, Oracle structural rewrites) (splitter, converters, type mapping, coercion, connection strings).
- 46 live end-to-end checks on local PostgreSQL 18, SQL Server 2022 and SQLite: MSSQL→PG (types incl. unicode, decimal, bit, uuid, bytea, µs timestamps), converted view + procedure executed on PG, upsert idempotency, truncate+load, PG→SQLite, SQLite→MSSQL round-trip (identical SUMs), compare, script executor (`$$`, COPY stdin, NOTICE, truncation, errors), CSV export/import round-trip, SQLite backup, Oracle package converted **and executed** on PG, Task Scheduler create/query/delete.
- Schema Compare verified live: PG→PG and SQL Server→PG, sync script applied, re-compare shows 0 differences (only commented drops remain).
- Grid editing verified live on PG, SQL Server and SQLite (updates, NULLs, timestamps, deletes, inserts using column defaults, stale-row rollback of the whole batch) and through the real UI.
- Oracle rewrites verified live: converted `(+)` joins (single, chained, constant filter, anti-join), CONNECT BY (paths, levels, sibling order, filtered subtree) and a BULK COLLECT + FORALL function run on PostgreSQL and return the Oracle-equivalent results.
- Indexes + FKs verified live: MSSQL → PG (4 indexes incl. name collision, 3 FKs incl. composite, CASCADE / NO ACTION / SET NULL behave like the source), PG → SQLite (FKs inlined), SQLite → MSSQL, dry-run planning.
- Paging verified live: 12,345 generated rows on PG, SQL Server (incl. CTE) and SQLite — continuous chunks, exact COUNT ALL, UI reaches page 25/25.
- Real app driven headless: editor query + explorer, Transfer object load (241 tables / 735 code objects), converter, theme switching; release single-file publish + CLI exit codes.

- Data Compare verified live: PG→PG (scale-insensitive numerics, NULL vs value, sequence resync), MSSQL→PG (unicode), PG→MSSQL (IDENTITY_INSERT), PG→SQLite (1,000 rows) — each re-compare shows 0 differences, both by direct apply and by running the generated script; missing key and row filters handled.
- SSL verified live: SQL Server Require → encrypted session, VerifyFull refuses a self-signed cert; PostgreSQL Disable/Prefer connect, Require fails cleanly when the server has SSL off.

**Not verified live (no server available here):** SSH tunnels (no SSH server on this PC), Oracle and MySQL connections/transfers/Data Pump/mysqldump — code paths are implemented and compile; first real use should be a dry run.

## 3. Limits that remain (by nature, not by omission)

- **Proprietary binary backups** — MSSQL `.bak` and Oracle `.dmp` can only be read by their own engine. The tool drives that engine (MSSQL restore, Data Pump) but can't parse the files itself.
- **Code conversion is assisted, not complete** — typical Oracle packages convert ~80–90% automatically; package state, autonomous transactions, pipelined functions, DBMS_* calls, `%ROWTYPE` record collections and multi-table / expression CONNECT BY are flagged for manual work. `(+)` joins, single-table CONNECT BY (LEVEL, SYS_CONNECT_BY_PATH, ORDER SIBLINGS BY), BULK COLLECT, FORALL and TABLE OF / VARRAY collections are converted. T-SQL procedures are translated heuristically (no mandatory `;` in T-SQL) and must be reviewed.
- **Cross-engine transfer moves tables, PK, NOT NULL, secondary indexes and foreign keys** (incl. composite keys and ON DELETE/UPDATE rules, per-engine limits respected). Check constraints, defaults, expression/partial indexes are not recreated across engines; existing target tables are never altered.
- **Oracle Data Pump files live on the DB server** (DIRECTORY object) — not on the PC.
- **Scheduled tasks run while the user is logged on** (no stored Windows password).

## 4. Next ideas

1. ~~Foreign keys + secondary indexes in cross-engine transfer~~ — done (verified MSSQL → PG → SQLite → MSSQL).
2. ~~Converter: CONNECT BY → WITH RECURSIVE, `(+)` → ANSI joins, BULK COLLECT → array_agg~~ — done (plus FORALL and PL/SQL collections), verified by executing the output on PostgreSQL.
3. ~~Result-grid inline editing (by primary key)~~ — done (edit / add / delete, one transaction, stale-row detection).
4. ~~Schema diff between any two connections~~ — done (Schema Compare page + sync script; verified PG→PG and MSSQL→PG to 0 differences).
5. ~~SSH tunnel / SSL certificate options in the profile editor~~ — done (SSL modes for all four server engines, SSH local forwarding with password/key and pinned host keys; tools like pg_dump/mysqldump go through the same tunnel). SSL verified live on MSSQL + PG; SSH not tested here (no SSH server).
6. Optional Oracle/MySQL integration tests via Docker/Testcontainers.
7. ~~Data diff view (row-level compare of one table across two connections)~~ — done (Data Compare page: key match, type-aware value compare, insert / update / delete-extra in one transaction or as a reviewable per-dialect script; verified PG→PG, MSSQL→PG, PG→MSSQL with identity, PG→SQLite to 0 differences).

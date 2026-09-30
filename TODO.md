# TODO — roadmap implementation (see ROADMAP.md)

- [x] 1. Themes: Light / Dark / System + accent picker, live switch, persisted
- [x] 2. Engine abstraction: `IDbProvider` + canonical types (PG, MSSQL, Oracle, MySQL, SQLite)
- [x] 3. Profiles: Oracle / MySQL / SQLite engines, in-dialog Test, extra options
- [x] 4. Transfer page: any engine → any engine, modes, dry run, compare, presets
- [x] 5. SQL Editor: AvalonEdit, run selection/statement, explain, transactions, explorer, history
- [x] 6. Export (CSV/TSV/JSON/INSERT) + CSV import
- [x] 7. Code converter: Oracle/T-SQL/MySQL → PG, PG → others; converter page
- [x] 8. Presets + History page (existing pages record into History too)
- [x] 9. Dependency manager in Settings (PG tools + Oracle Instant Client download)
- [x] 10. CLI `--run-preset` + Windows Task Scheduler page
- [x] 11. Sidebar navigation, remembers last page
- [x] 12. Engine Backup page: SQLite / MySQL / Oracle Data Pump
- [x] 13. Tests: 27 unit + 46 live end-to-end checks (PG 18, MSSQL 2022, SQLite)
- [x] 14. Build, publish, README / ROADMAP updated, version 3.0.0

## Open
- [ ] Live test against a real Oracle and MySQL server (no credentials/server here)
- [ ] Build installer 3.0.0 (needs Inno Setup: `ISCC installer\PgBackupManager.iss`)
- [ ] Cross-engine foreign keys / secondary indexes (see ROADMAP §4)

# Import comparison — SQL Server `Accounting_20_09_2026` → Postgres `dcci_migration_from_mssql_20_09_2026`

Source: `localhost\Accounting_20_09_2026` (SQL Server, Windows auth)
Target: `172.237.78.31:5432/mediklaud_dcci`, schema `dcci_migration_from_mssql_20_09_2026`

## Correction

An earlier version of this report compared against the wrong schema
(`mediklaud_dcci_live`, the live production schema) and wrongly flagged
`Ptransac` as having 137,716 rows in Postgres vs 42,485 in source. That
number was real, but for the wrong table: the database has **five** separate
`ptransac` tables, one per schema (`mediklaud_dcci_live`,
`mediklaud_dcci_live_restore`, `dcci_acc_migration_from_mssql_16_09_2026`,
`dcci_migration_from_mssql_20_09_2026`, and `mediklaud_dcci`). This import
actually landed in `dcci_migration_from_mssql_20_09_2026`, which is the one
compared below.

## Result: clean match

All **210** tables compared, source vs. target, using real `COUNT(*)` on
both sides (not SQL Server's `sys.partitions.rows`, which can go stale after
deletes until stats are rebuilt — it initially misreported `DummyPtransac` as
45 rows when the true count is 43 on both sides).

- Tables present in both: 210 / 210
- Row-count mismatches: **0**
- Tables only in SQL Server (never imported): **0**
- Tables only in Postgres (unexpected extras): **0**

Every table, including the large ones, matches exactly:

| Table      | SQL Server | Postgres |
|------------|-----------:|---------:|
| AccProfile | 130        | 130      |
| Users      | 32         | 32       |
| Buyer      | 55         | 55       |
| Ptransac   | 42,485     | 42,485   |
| BudgetHead | 37         | 37       |

## The duplicate-key error

```
Import failed: 23505: duplicate key value violates unique constraint "AccProfile_pkey"
DETAIL: Key ("pCode")=(0106) already exists.
```

This came from an earlier, partial run of the import (before it was retried
to completion). By the time of this comparison the data is fully
reconciled — 210/210 tables match, nothing outstanding. No data fix needed.

## Open item (not a data problem)

`MsSqlImportRunner` (`PgBackupManager.Core/Services/MsSqlImportRunner.cs`)
does a plain insert, so re-running an import that's already partially applied
will hit `23505` on the first already-there row instead of skipping it. Worth
making it an upsert (`INSERT ... ON CONFLICT (<pk>) DO NOTHING`) so a retry
is safe and idempotent — say the word and I'll wire that in.

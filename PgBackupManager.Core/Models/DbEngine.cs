namespace PgBackupManager.Core.Models;

// PostgreSql MUST stay the first (default/0) value — existing profiles.json
// entries have no "Engine" property, and System.Text.Json defaults a missing
// enum to its zero value on deserialize. Reordering this breaks every saved
// profile silently (they'd all become SqlServer).
public enum DbEngine { PostgreSql, SqlServer }

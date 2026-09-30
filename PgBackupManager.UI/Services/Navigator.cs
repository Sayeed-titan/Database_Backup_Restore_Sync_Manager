using System;

namespace PgBackupManager.UI.Services;

public sealed record OpenSqlRequest(string Title, string Sql, Guid? ProfileId);

// Cross-tab hand-offs ("open this converted script in the SQL editor",
// "go to Transfer"). MainWindow switches the visible page; the target
// view-model picks up the payload.
public static class Navigator
{
    public static event EventHandler<string>? PageRequested;
    public static event EventHandler<OpenSqlRequest>? OpenSqlRequested;

    public static void Go(string page) => PageRequested?.Invoke(null, page);

    public static void OpenSql(string title, string sql, Guid? profileId = null)
    {
        OpenSqlRequested?.Invoke(null, new OpenSqlRequest(title, sql, profileId));
        Go("Editor");
    }
}

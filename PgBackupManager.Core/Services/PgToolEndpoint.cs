using System.Collections.Generic;
using PgBackupManager.Core.Models;

namespace PgBackupManager.Core.Services;

// Where pg_dump / pg_restore / psql should connect, and the environment they
// need: through the profile's SSH tunnel when it has one, with PGSSLMODE when
// the profile sets an SSL mode. Jobs without a profile keep their Host/Port.
public static class PgToolEndpoint
{
    public static (string Host, int Port) Resolve(ConnectionProfile? p, string host, int port) =>
        p == null ? (host, port) : (p.EndpointHost, p.EndpointPort);

    public static Dictionary<string, string> Env(ConnectionProfile? p, string password)
    {
        var env = new Dictionary<string, string> { ["PGPASSWORD"] = password };
        if (p?.LibpqSslMode is { } mode) env["PGSSLMODE"] = mode;
        return env;
    }
}

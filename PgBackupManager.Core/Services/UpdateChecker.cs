using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PgBackupManager.Core.Services;

public sealed record UpdateInfo(bool IsAvailable, string? LatestVersion, string? ReleaseUrl);

// Checks GitHub Releases for a newer build than the one currently running.
// The repo is public, so this needs no auth token — just a User-Agent, which
// GitHub's API requires on every request regardless of auth.
public static class UpdateChecker
{
    private const string ReleasesApiUrl =
        "https://api.github.com/repos/Sayeed-titan/Database_Backup_Restore_Sync_Manager/releases/latest";

    public static async Task<UpdateInfo> CheckAsync(Version currentVersion, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PgBackupManager-UpdateChecker");

            using var resp = await http.GetAsync(ReleasesApiUrl, ct);
            if (!resp.IsSuccessStatusCode) return new UpdateInfo(false, null, null);

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() : null;

            // Release tags are conventionally "v2.1.0" — Version.Parse doesn't
            // accept the leading "v", so strip it before comparing.
            var cleaned = tag.TrimStart('v', 'V');
            if (!Version.TryParse(cleaned, out var latest)) return new UpdateInfo(false, tag, url);

            return new UpdateInfo(latest > currentVersion, cleaned, url);
        }
        catch
        {
            // Offline, rate-limited, no releases published yet, etc. — an
            // update check should never be able to disrupt the app; the
            // caller decides what to do with "couldn't determine".
            return new UpdateInfo(false, null, null);
        }
    }
}

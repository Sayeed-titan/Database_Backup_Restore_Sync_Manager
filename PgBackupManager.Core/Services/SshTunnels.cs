using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PgBackupManager.Core.Models;
using Renci.SshNet;

namespace PgBackupManager.Core.Services;

// SSH local port forwarding for profiles with "Connect through SSH" on.
// One tunnel per (profile, target) is opened on first use and reused by every
// connection and every external tool (pg_dump, mysqldump...) — they all just
// see 127.0.0.1:<bound port>. Host keys are pinned on first use (TOFU) and a
// changed key refuses to connect, like OpenSSH's known_hosts.
public static class SshTunnels
{
    private sealed record Tunnel(SshClient Client, ForwardedPortLocal Port);

    private static readonly Dictionary<string, Tunnel> Open = new();
    private static readonly object Gate = new();

    public static string KnownHostsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PgBackupManager", "ssh_known_hosts.json");

    private static string Key(ConnectionProfile p) => $"{p.Id}|{p.SshUser}@{p.SshHost}:{p.SshPort}|{p.Host}:{p.Port}";

    // Returns the local port that forwards to the profile's Host:Port.
    public static int Ensure(ConnectionProfile p)
    {
        lock (Gate)
        {
            var key = Key(p);
            if (Open.TryGetValue(key, out var t) && t.Client.IsConnected && t.Port.IsStarted) return (int)t.Port.BoundPort;
            if (t != null) Close(key);

            if (string.IsNullOrWhiteSpace(p.SshHost) || string.IsNullOrWhiteSpace(p.SshUser))
                throw new InvalidOperationException("SSH tunnel is on but the SSH host or user is empty.");

            var auths = new List<AuthenticationMethod>();
            if (!string.IsNullOrWhiteSpace(p.SshKeyFile))
            {
                var pass = SecretProtector.Unprotect(p.SshEncryptedKeyPassphrase ?? "");
                var key2 = string.IsNullOrEmpty(pass) ? new PrivateKeyFile(p.SshKeyFile) : new PrivateKeyFile(p.SshKeyFile, pass);
                auths.Add(new PrivateKeyAuthenticationMethod(p.SshUser, key2));
            }
            var pwd = SecretProtector.Unprotect(p.SshEncryptedPassword ?? "");
            if (!string.IsNullOrEmpty(pwd)) auths.Add(new PasswordAuthenticationMethod(p.SshUser, pwd));
            if (auths.Count == 0) throw new InvalidOperationException("SSH tunnel needs a password or a private key file.");

            var info = new ConnectionInfo(p.SshHost, p.SshPort > 0 ? p.SshPort : 22, p.SshUser, auths.ToArray()) { Timeout = TimeSpan.FromSeconds(15) };
            var client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
            string? mismatch = null;
            client.HostKeyReceived += (_, e) =>
            {
                var fp = e.FingerPrintSHA256;
                var known = LoadKnownHosts();
                var host = $"{p.SshHost}:{info.Port}";
                if (known.TryGetValue(host, out var pinned))
                {
                    e.CanTrust = pinned == fp;
                    if (!e.CanTrust) mismatch = $"SSH host key for {host} CHANGED (was {pinned}, now {fp}). Refusing to connect — remove it from {KnownHostsPath} if the change is expected.";
                }
                else
                {
                    known[host] = fp;
                    SaveKnownHosts(known);
                    e.CanTrust = true;
                }
            };
            try { client.Connect(); }
            catch (Exception ex) { client.Dispose(); throw new InvalidOperationException(mismatch ?? $"SSH connection to {p.SshHost} failed: {ex.Message}", ex); }

            var fwd = new ForwardedPortLocal("127.0.0.1", 0, p.Host, (uint)(p.Port > 0 ? p.Port : ConnectionProfile.DefaultPort(p.Engine)));
            client.AddForwardedPort(fwd);
            fwd.Start();
            Open[key] = new Tunnel(client, fwd);
            return (int)fwd.BoundPort;
        }
    }

    public static bool IsOpen(ConnectionProfile p) { lock (Gate) return Open.TryGetValue(Key(p), out var t) && t.Client.IsConnected; }

    private static void Close(string key)
    {
        if (!Open.Remove(key, out var t)) return;
        try { t.Port.Stop(); } catch { }
        try { t.Client.Disconnect(); } catch { }
        t.Client.Dispose();
    }

    public static void CloseAll() { lock (Gate) foreach (var k in Open.Keys.ToList()) Close(k); }

    private static Dictionary<string, string> LoadKnownHosts()
    {
        try { return File.Exists(KnownHostsPath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(KnownHostsPath)) ?? new() : new(); }
        catch { return new(); }
    }

    private static void SaveKnownHosts(Dictionary<string, string> d)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KnownHostsPath)!);
        File.WriteAllText(KnownHostsPath, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
    }
}

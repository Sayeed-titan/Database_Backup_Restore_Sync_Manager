using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PgBackupManager.Core.Services;

public sealed class ProcessRunner
{
    public event EventHandler<string>? StdoutLine;
    public event EventHandler<string>? StderrLine;

    public async Task<int> RunAsync(
        string exePath,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? envOverrides = null,
        string? workingDirectory = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(exePath))
            throw new FileNotFoundException($"Executable not found: {exePath}", exePath);

        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (envOverrides != null)
            foreach (var kv in envOverrides) psi.Environment[kv.Key] = kv.Value;

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stdoutDone = new TaskCompletionSource();
        var stderrDone = new TaskCompletionSource();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) stdoutDone.TrySetResult();
            else StdoutLine?.Invoke(this, e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) stderrDone.TrySetResult();
            else StderrLine?.Invoke(this, e.Data);
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await using (ct.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch { } }))
        {
            await Task.WhenAll(stdoutDone.Task, stderrDone.Task);
            await proc.WaitForExitAsync(CancellationToken.None);
        }
        return proc.ExitCode;
    }
}

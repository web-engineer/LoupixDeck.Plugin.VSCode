using System.Diagnostics;
using System.Text;

namespace LoupixDeck.Plugin.VSCode.Platform;

/// <summary>Runs small helper processes (<c>lsappinfo</c>, <c>open</c>, <c>pgrep</c>, the editor's CLI).</summary>
internal static class ProcessRunner
{
    /// <summary>How long <see cref="Capture"/> lets a helper run before it is killed.</summary>
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Runs a command and returns its stdout (trimmed), or null on failure / timeout.</summary>
    internal static string? Capture(string file, params string[] args) => Capture(file, args, CaptureTimeout);

    /// <summary>
    /// Runs a command and returns its stdout (trimmed), or null when it fails or runs past
    /// <paramref name="timeout"/> (it is killed then). Both pipes are drained asynchronously, so a
    /// child that stalls or fills stderr cannot block past the timeout.
    /// </summary>
    internal static string? Capture(string file, IEnumerable<string> args, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var stdout = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return null;
            }

            // The pipes close when the process exits; a grandchild still holding stdout must not block us.
            if (!stdout.Wait(TimeSpan.FromMilliseconds(500))) return null;
            return p.ExitCode == 0 ? stdout.Result.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Runs a command to completion and returns its exit code with stdout and stderr together.
    /// Returns -1 and the reason when it cannot start or runs past <paramref name="timeout"/> (it is
    /// killed then).
    /// </summary>
    internal static async Task<(int ExitCode, string Output)> RunAsync(string file, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        var output = new StringBuilder();
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
            if (!p.Start()) return (-1, $"{file} did not start");
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            try
            {
                await p.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                lock (output) return (-1, output + (ct.IsCancellationRequested ? "Cancelled." : $"Timed out after {timeout.TotalSeconds:0} s."));
            }

            // Lets the asynchronous readers drain the last lines.
            p.WaitForExit();
            lock (output) return (p.ExitCode, output.ToString().Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    /// <summary>Starts a process without waiting for it. False when it could not start.</summary>
    internal static bool Launch(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            return p is not null;
        }
        catch
        {
            return false;
        }
    }
}

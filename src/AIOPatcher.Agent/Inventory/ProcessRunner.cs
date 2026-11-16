using System.Diagnostics;

namespace AIOPatcher.Agent.Inventory;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;
}

public static class ProcessRunner
{
    /// <summary>Startet ein Programm ohne Shell (keine Interpretation von Argumenten), mit Timeout und C-Locale.</summary>
    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        var exitCode = await RunCoreAsync(fileName, arguments, timeout, line => stdout.AppendLine(line), line => stderr.AppendLine(line), ct);
        return new ProcessResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Wie <see cref="RunAsync"/>, reicht aber jede Ausgabezeile sofort weiter (für das Live-Log).</summary>
    public static Task<int> RunStreamingAsync(string fileName, IEnumerable<string> arguments, TimeSpan timeout, Action<string> onLine, CancellationToken ct) =>
        RunCoreAsync(fileName, arguments, timeout, onLine, onLine, ct);

    private static async Task<int> RunCoreAsync(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        Action<string> onStdout,
        Action<string> onStderr,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        // Stabile, parsebare Ausgabe und keine interaktiven Rückfragen.
        psi.Environment["LANG"] = "C";
        psi.Environment["LC_ALL"] = "C";
        psi.Environment["DEBIAN_FRONTEND"] = "noninteractive";
        psi.Environment["APT_LISTCHANGES_FRONTEND"] = "none";
        psi.Environment["UCF_FORCE_CONFFOLD"] = "1";
        // needrestart (Ubuntu) nur auflisten lassen: ein automatischer Dienst-Neustart könnte sonst den Agent
        // samt laufendem dpkg beenden.
        psi.Environment["NEEDRESTART_MODE"] = "l";

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onStdout(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onStderr(e.Data); };

        if (!process.Start())
        {
            throw new InvalidOperationException($"{fileName} could not be started.");
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            // Restliche Ausgabe-Events abarbeiten. Mit Limit: ein vom Prozess gestarteter Hintergrunddienst
            // könnte die Pipes sonst ewig offen halten.
            process.WaitForExit(TimeSpan.FromSeconds(10));
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // bereits beendet
            }

            if (ct.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException($"{fileName} ran longer than {timeout.TotalMinutes:0} minutes and was aborted.");
        }
    }
}

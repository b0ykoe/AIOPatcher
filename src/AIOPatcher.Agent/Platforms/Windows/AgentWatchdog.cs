using System.Runtime.Versioning;
using System.Security;
using AIOPatcher.Agent.Inventory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AIOPatcher.Agent.Platforms.Windows;

/// <summary>XML der Wächter-Aufgabe (plattformneutral, damit es auch auf Linux getestet werden kann).</summary>
public static class AgentWatchdogTask
{
    public const string TaskName = "AIOPatcherAgentWatchdog";
    public const string ServiceName = "AIOPatcherAgent";

    /// <summary>Aufgabe als XML (Task Scheduler 1.2): ab Start alle 5 Minuten "sc.exe start", höchstens 1 Minute Laufzeit.</summary>
    public static string Xml(string systemDirectory, DateTime startLocal) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Description>AIOPatcher: starts the agent service again if it is not running (e.g. after an aborted reboot).</Description></RegistrationInfo>
          <Triggers>
            <TimeTrigger>
              <StartBoundary>{startLocal:yyyy-MM-ddTHH:mm:ss}</StartBoundary>
              <Repetition><Interval>PT5M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition>
              <Enabled>true</Enabled>
            </TimeTrigger>
          </Triggers>
          <Principals><Principal id="Author"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <ExecutionTimeLimit>PT1M</ExecutionTimeLimit>
            <StartWhenAvailable>true</StartWhenAvailable>
            <Enabled>true</Enabled>
          </Settings>
          <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(Path.Combine(systemDirectory, "sc.exe"))}</Command><Arguments>start {ServiceName}</Arguments></Exec></Actions>
        </Task>
        """;
}

/// <summary>
/// Wächter für den Agent-Dienst unter Windows: eine geplante Aufgabe (SYSTEM) startet den Dienst alle 5 Minuten, falls er
/// nicht läuft. Hintergrund: Beim Herunterfahren beendet Windows den Dienst. Bricht der Neustart danach ab (App blockiert,
/// etwas hängt), startet ihn sonst niemand mehr und der Host bleibt offline. Der Agent legt die Aufgabe bei jedem Start
/// an bzw. erneuert sie, damit auch selbst aktualisierte Installationen sie bekommen.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentWatchdog(ILogger<AgentWatchdog> logger) : BackgroundService
{
    private const string TaskName = AgentWatchdogTask.TaskName;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
        {
            return; // nur für den installierten Dienst, nicht beim Start von Hand
        }

        var file = Path.Combine(Path.GetTempPath(), $"aiopatcher-watchdog-{Guid.NewGuid():N}.xml");
        try
        {
            await File.WriteAllTextAsync(file, AgentWatchdogTask.Xml(Environment.SystemDirectory, DateTime.Now.AddMinutes(5)), System.Text.Encoding.Unicode, stoppingToken);
            var result = await ProcessRunner.RunAsync(Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                ["/Create", "/TN", TaskName, "/XML", file, "/F"], TimeSpan.FromSeconds(30), stoppingToken);
            if (!result.Success)
            {
                logger.LogWarning("Could not create the watchdog task {Task}: {Error}", TaskName, (result.StandardError + result.StandardOutput).Trim());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning("Could not create the watchdog task {Task}: {Message}", TaskName, ex.Message);
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

using System.Text.Json;
using AIOPatcher.Agent;
using AIOPatcher.Agent.Commands;
using AIOPatcher.Agent.Inventory;
using AIOPatcher.Agent.Jobs;
using AIOPatcher.Agent.Modules;
using AIOPatcher.Agent.Platforms.Apt;
using AIOPatcher.Agent.Platforms.Windows;
using AIOPatcher.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var commandLine = new CommandLine(args);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

switch (commandLine.Command)
{
    case "enroll":
        return await EnrollCommand.RunAsync(commandLine, cts.Token);

    case "version":
        Console.WriteLine(AgentState.AgentVersion);
        return 0;

    case "policy":
    {
        // aiopatcher-agent policy [set <key> <true|false>] [--state-dir DIR]
        var stateDir = commandLine.Get("state-dir") ?? AgentState.DefaultDirectory;
        try
        {
            var policy = commandLine.Positional is ["set", var key, var value]
                ? AgentPolicy.Set(stateDir, key, value)
                : AgentPolicy.Load(stateDir);
            Console.WriteLine(JsonSerializer.Serialize(policy, AgentPolicyJsonContext.Default.AgentPolicy));
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or JsonException)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 2;
        }
    }

    case "selftest":
    {
        using var loggerFactory = LoggerFactory.Create(l => l.SetMinimumLevel(LogLevel.Warning).AddSimpleConsole(o => o.SingleLine = true));
        return await SelfTestCommand.RunAsync(commandLine, () => CreateInventory(loggerFactory), cts.Token);
    }

    case "scan":
    {
        // Diagnose: zeigt, was das Modul "updates" auf einen Scan-Auftrag antworten würde (ohne Server).
        using var loggerFactory = LoggerFactory.Create(l => l.AddSimpleConsole(o => o.SingleLine = true));
        var inventory = CreateInventory(loggerFactory);
        var info = await inventory.GetSystemInfoAsync(cts.Token);
        Console.WriteLine($"{info.OsName} {info.OsVersion}, reboot required: {info.RebootRequired}");
        if (!commandLine.Has("no-updates") && await inventory.ScanUpdatesAsync(cts.Token) is { } updates)
        {
            Console.WriteLine(JsonSerializer.Serialize(new UpdateScanResult(updates), PullJsonContext.Default.UpdateScanResult));
        }

        return 0;
    }

    case "run":
    {
        var stateDir = commandLine.Get("state-dir") ?? AgentState.DefaultDirectory;
        if (!AgentState.Exists(stateDir))
        {
            await Console.Error.WriteLineAsync($"Agent is not enrolled ({stateDir}). Run 'aiopatcher-agent enroll ...' first.");
            return 3;
        }

        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddSystemd();
        builder.Services.AddWindowsService(o => o.ServiceName = "AIOPatcherAgent");
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
        });
        if (OperatingSystem.IsWindows() && Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
        {
            // Als Dienst gibt es keine Konsole: Ereignisanzeige > Windows-Protokolle > Anwendung, Quelle "AIOPatcherAgent".
            AddWindowsEventLog(builder.Logging);
        }

        builder.Services.AddSingleton(AgentState.Load(stateDir));
        var complianceStore = new AIOPatcher.Agent.Compliance.ComplianceStore(stateDir);
        builder.Services.AddSingleton<AIOPatcher.Agent.Compliance.IComplianceProvider>(_ => OperatingSystem.IsWindows()
            ? new AIOPatcher.Agent.Compliance.WindowsCompliance(complianceStore)
            : new AIOPatcher.Agent.Compliance.LinuxCompliance(complianceStore));
        builder.Services.AddSingleton(sp => CreateInventory(sp.GetRequiredService<ILoggerFactory>()));
        builder.Services.AddSingleton<IPatchPlatform>(_ => OperatingSystem.IsWindows() ? new WindowsPatcher() : new AptPatcher());
        builder.Services.AddSingleton<JobExecutor>();
        builder.Services.AddSingleton<AIOPatcher.Agent.Maintenance.SelfMaintenance>();

        // Module: jedes kennt nur seine festen Aktionen (siehe AIOPatcher.Contracts.AgentModules).
        builder.Services.AddSingleton<IAgentModule>(sp => new SystemModule(sp.GetRequiredService<IPlatformInventory>(),
            sp.GetRequiredService<AIOPatcher.Agent.Maintenance.SelfMaintenance>(), () => sp.GetRequiredService<ModuleHost>()));
        builder.Services.AddSingleton<IAgentModule, UpdatesModule>();
        builder.Services.AddSingleton<IAgentModule, SoftwareModule>();
        builder.Services.AddSingleton<IAgentModule, DockerModule>();
        builder.Services.AddSingleton<IAgentModule, ServicesModule>();
        builder.Services.AddSingleton<IAgentModule, FirewallModule>();
        builder.Services.AddSingleton<IAgentModule, RemoteAccessModule>();
        builder.Services.AddSingleton<IAgentModule, UsersModule>();
        builder.Services.AddSingleton<IAgentModule, AgentSelfModule>();
        builder.Services.AddSingleton<ModuleHost>();
        builder.Services.AddHostedService<PollService>();

        await builder.Build().RunAsync(cts.Token);
        return 0;
    }

    default:
        await Console.Error.WriteLineAsync("Commands: run (default), enroll, selftest, scan, version");
        return 2;
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static void AddWindowsEventLog(ILoggingBuilder logging) =>
    logging.AddEventLog(new Microsoft.Extensions.Logging.EventLog.EventLogSettings { SourceName = "AIOPatcherAgent" });

static IPlatformInventory CreateInventory(ILoggerFactory loggerFactory) =>
    OperatingSystem.IsWindows()
        ? new WindowsInventory()
        : new LinuxInventory(loggerFactory.CreateLogger<LinuxInventory>());

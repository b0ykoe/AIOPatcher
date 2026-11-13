using System.Text.Json;
using AIOPatcher.Agent;
using AIOPatcher.Agent.Commands;
using AIOPatcher.Agent.Inventory;
using AIOPatcher.Agent.Jobs;
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

    case "scan":
    {
        // Diagnose: zeigt, was der Agent beim nächsten Check-in melden würde (ohne Server).
        using var loggerFactory = LoggerFactory.Create(l => l.AddSimpleConsole(o => o.SingleLine = true));
        var inventory = CreateInventory(loggerFactory);
        var updates = commandLine.Has("no-updates") ? null : await inventory.ScanUpdatesAsync(cts.Token);
        var request = await CheckinService.BuildRequestAsync(inventory, updates, cts.Token);
        Console.WriteLine(JsonSerializer.Serialize(request, AgentJsonContext.Default.CheckinRequest));
        return 0;
    }

    case "run":
    {
        var stateDir = commandLine.Get("state-dir") ?? AgentState.DefaultDirectory;
        if (!AgentState.Exists(stateDir))
        {
            await Console.Error.WriteLineAsync($"Agent ist nicht registriert ({stateDir}). Zuerst 'aiopatcher-agent enroll ...' ausführen.");
            return 3;
        }

        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddSystemd();
        builder.Services.AddWindowsService(o => o.ServiceName = "AIOPatcher Agent");
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
        });

        builder.Services.AddSingleton(AgentState.Load(stateDir));
        builder.Services.AddSingleton(sp => CreateInventory(sp.GetRequiredService<ILoggerFactory>()));
        builder.Services.AddSingleton<IPatchPlatform>(_ => OperatingSystem.IsWindows() ? new WindowsPatcher() : new AptPatcher());
        builder.Services.AddSingleton<JobExecutor>();
        builder.Services.AddHostedService<CheckinService>();

        await builder.Build().RunAsync(cts.Token);
        return 0;
    }

    default:
        await Console.Error.WriteLineAsync("Befehle: run (Standard), enroll, scan, version");
        return 2;
}

static IPlatformInventory CreateInventory(ILoggerFactory loggerFactory) =>
    OperatingSystem.IsWindows()
        ? new WindowsInventory()
        : new LinuxInventory(loggerFactory.CreateLogger<LinuxInventory>());

using System.Net;
using AIOPatcher.Agent.Inventory;

namespace AIOPatcher.Agent.Commands;

/// <summary>
/// "aiopatcher-agent selftest": Kann dieser Agent hier laufen? Lädt Registrierung und Policy, liest Systemdaten und
/// meldet sich mit seinem Zertifikat beim Server. Nutzen: Installationsskripte (vor dem Dienststart) und das
/// Self-Update (die neue Version prüft sich, bevor sie die alte ersetzt). Erste Zeile immer "version: x.y.z".
/// Exit-Code 0 = alles gut, 1 = Fehler.
/// </summary>
public static class SelfTestCommand
{
    public static async Task<int> RunAsync(CommandLine commandLine, Func<IPlatformInventory> createInventory, CancellationToken ct)
    {
        Console.WriteLine($"version: {AgentState.AgentVersion}");
        var stateDir = commandLine.Get("state-dir") ?? AgentState.DefaultDirectory;
        if (!AgentState.Exists(stateDir))
        {
            Console.WriteLine($"FAIL state: not enrolled ({stateDir})");
            return 1;
        }

        AgentState? state = null;
        var ok = await StepAsync("state", () =>
        {
            state = AgentState.Load(stateDir);
            var days = (state.ClientCertificate.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays;
            return Task.FromResult(days < 0
                ? throw new InvalidOperationException($"client certificate expired on {state.ClientCertificate.NotAfter:yyyy-MM-dd}")
                : $"host {state.Config.HostId}, certificate valid for {days:0} days");
        });

        ok &= await StepAsync("policy", () =>
        {
            var policy = AgentPolicy.Load(stateDir);
            return Task.FromResult($"patch {policy.AllowPatch}, reboot {policy.AllowReboot}, self-update {policy.AllowSelfUpdate}");
        });

        ok &= await StepAsync("system", async () =>
        {
            var info = await createInventory().GetSystemInfoAsync(ct);
            return $"{info.OsName} {info.OsVersion}";
        });

        if (state is not null)
        {
            ok &= await StepAsync("server", async () =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                using var client = ServerClient.FromState(state);
                try
                {
                    var hello = await client.HelloAsync(timeout.Token);
                    return hello.HostId == state.Config.HostId
                        ? $"{state.Config.ServerUrl} knows this host as '{hello.HostName}' (server {hello.ServerVersion})"
                        : throw new InvalidOperationException($"server answers for another host ({hello.HostId})");
                }
                catch (ServerRejectedException ex) when (ex.Status == HttpStatusCode.NotFound)
                {
                    return $"{state.Config.ServerUrl} reachable (older server without self-test endpoint)";
                }
                catch (ServerRejectedException ex) when (ex.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new InvalidOperationException($"server rejects this agent ({(int)ex.Status}): revoked, deleted or IP not allowed for the site");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"{state.Config.ServerUrl} did not answer within 45 s (firewall, port forwarding?)");
                }
            });
        }

        Console.WriteLine(ok ? "selftest: ok" : "selftest: FAILED");
        return ok ? 0 : 1;
    }

    private static async Task<bool> StepAsync(string name, Func<Task<string>> step)
    {
        try
        {
            Console.WriteLine($"ok   {name}: {await step()}");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"FAIL {name}: {(ex is HttpRequestException http ? http.InnerException?.Message ?? http.Message : ex.Message)}");
            return false;
        }
    }
}

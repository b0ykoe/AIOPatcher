using System.Xml.Linq;
using AIOPatcher.Agent.Platforms.Windows;

namespace AIOPatcher.Tests;

/// <summary>
/// Windows: bricht ein Neustart ab, nachdem Windows den Agent-Dienst beendet hat, startet ihn die Wächter-Aufgabe wieder.
/// Hier: das Aufgaben-XML ist gültig und startet genau den Agent-Dienst alle 5 Minuten.
/// </summary>
public sealed class AgentWatchdogTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Fact]
    public void TaskXml_StartsTheServiceEveryFiveMinutes()
    {
        var xml = AgentWatchdogTask.Xml(@"C:\Windows\System32", new DateTime(2026, 11, 19, 20, 5, 0));
        var task = XDocument.Parse(xml).Root!;

        var trigger = task.Element(Ns + "Triggers")!.Element(Ns + "TimeTrigger")!;
        Assert.Equal("2026-11-19T20:05:00", trigger.Element(Ns + "StartBoundary")!.Value);
        Assert.Equal("PT5M", trigger.Element(Ns + "Repetition")!.Element(Ns + "Interval")!.Value);
        Assert.Equal("S-1-5-18", task.Element(Ns + "Principals")!.Element(Ns + "Principal")!.Element(Ns + "UserId")!.Value); // SYSTEM
        var exec = task.Element(Ns + "Actions")!.Element(Ns + "Exec")!;
        Assert.Equal(@"C:\Windows\System32\sc.exe", exec.Element(Ns + "Command")!.Value);
        Assert.Equal("start AIOPatcherAgent", exec.Element(Ns + "Arguments")!.Value);
        Assert.Equal("IgnoreNew", task.Element(Ns + "Settings")!.Element(Ns + "MultipleInstancesPolicy")!.Value);
    }
}

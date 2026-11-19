using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AIOPatcher.Contracts;

namespace AIOPatcher.Agent.Platforms.Monitoring;

/// <summary>Gelesene Client-Einstellungen aus ossec.conf.</summary>
/// <param name="IsAgent">false = Konfiguration eines Managers (kein &lt;client&gt;/&lt;agent&gt;): nicht anfassen.</param>
/// <param name="Addresses">"manager[:port]" bzw. Endpoint-URLs (Wazuh 5).</param>
/// <param name="EnrollmentAddress">&lt;enrollment&gt;&lt;manager_address&gt;, falls gesetzt.</param>
/// <param name="PassPath">&lt;enrollment&gt;&lt;authorization_pass_path&gt;, falls gesetzt (relativ zum Installationsordner).</param>
public sealed record WazuhConfigInfo(bool IsAgent, string Variant, IReadOnlyList<string> Addresses, string? EnrollmentAddress, string? PassPath);

/// <summary>
/// ossec.conf lesen und umschreiben, ohne die übrige Datei anzufassen. Die Datei hat oft mehrere &lt;ossec_config&gt;-Blöcke
/// und ist damit kein gültiges XML-Dokument: Sie wird zum Bearbeiten in ein Hilfselement gepackt.
/// Formate laut Doku: Wazuh 3.0–4.x &lt;client&gt;&lt;server&gt;&lt;address&gt;, OSSEC/vor 3.0 &lt;server-ip&gt;/&lt;server-hostname&gt;,
/// Wazuh 5 &lt;agent&gt;&lt;manager&gt;&lt;endpoint&gt; (hat Vorrang vor &lt;client&gt;).
/// </summary>
public static partial class WazuhConfig
{
    private const string Wrapper = "aiopatcher-wrapper";

    public static WazuhConfigInfo Parse(string text)
    {
        var (doc, _) = Load(text);
        var endpoints = Endpoints(doc).ToList();
        if (endpoints.Count > 0)
        {
            return new WazuhConfigInfo(true, MonitoringVariants.Wazuh5, endpoints.Select(e => e.Value.Trim()).ToList(), null, null);
        }

        if (Client(doc) is not { } client)
        {
            return new WazuhConfigInfo(false, MonitoringVariants.Wazuh, [], null, null);
        }

        var enrollment = client.Element("enrollment");
        var enrollAddress = Text(enrollment?.Element("manager_address"));
        var passPath = Text(enrollment?.Element("authorization_pass_path"));

        var servers = client.Elements("server").ToList();
        var legacy = client.Elements().Where(e => e.Name.LocalName is "server-ip" or "server-hostname").ToList();
        if (servers.Count == 0 && legacy.Count > 0)
        {
            var port = Text(client.Element("port"));
            return new WazuhConfigInfo(true, MonitoringVariants.Ossec,
                legacy.Select(e => Text(e)).OfType<string>().Select(a => port is null ? a : $"{a}:{port}").ToList(), enrollAddress, passPath);
        }

        var addresses = servers
            .Select(s => (Address: Text(s.Element("address")), Port: Text(s.Element("port"))))
            .Where(s => s.Address is not null)
            .Select(s => s.Port is null ? s.Address! : $"{s.Address}:{s.Port}")
            .ToList();
        return new WazuhConfigInfo(true, MonitoringVariants.Wazuh, addresses, enrollAddress, passPath);
    }

    /// <summary>
    /// Ziel eintragen. newEnrollment: beim neuen Manager neu anmelden (mit enrollmentBlock: &lt;enrollment&gt; setzen, ab 4.0);
    /// sonst wird nur die Adresse geändert (Schlüssel wurden auf den neuen Manager übernommen).
    /// </summary>
    /// <exception cref="InvalidOperationException">Datei hat keinen Client-Abschnitt bzw. die Variante kann das nicht.</exception>
    public static string Rewrite(string text, MonitoringSpec spec, bool newEnrollment, bool enrollmentBlock)
    {
        var address = spec.Address ?? throw new InvalidOperationException("No manager address given.");
        var (doc, declaration) = Load(text);

        var endpoints = Endpoints(doc).ToList();
        if (endpoints.Count > 0)
        {
            if (newEnrollment)
            {
                throw new InvalidOperationException(
                    "Wazuh 5 agents enroll with tokens, which AIOPatcher does not support yet. Copy client.keys to the new manager and choose \"keys already on the new manager\".");
            }

            endpoints[0].Value = $"https://{HostPart(address)}" + (spec.Port is { } p ? $":{p}" : "");
            foreach (var extra in endpoints.Skip(1))
            {
                RemoveWithWhitespace(extra);
            }

            return Save(doc, declaration);
        }

        var client = Client(doc) ?? throw new InvalidOperationException("ossec.conf has no <client> section (is this a Wazuh manager?).");
        var servers = client.Elements("server").ToList();
        var legacy = client.Elements().Where(e => e.Name.LocalName is "server-ip" or "server-hostname").ToList();

        if (servers.Count == 0 && legacy.Count > 0)
        {
            // OSSEC / Wazuh vor 3.0
            var element = new XElement(IPAddress.TryParse(address, out _) ? "server-ip" : "server-hostname", address);
            legacy[0].ReplaceWith(element);
            foreach (var extra in legacy.Skip(1))
            {
                RemoveWithWhitespace(extra);
            }

            if (spec.Port is { } legacyPort)
            {
                SetChild(client, "port", legacyPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            return Save(doc, declaration);
        }

        XElement server;
        if (servers.Count == 0)
        {
            server = new XElement("server");
            client.AddFirst(server);
        }
        else
        {
            server = servers[0];
            foreach (var extra in servers.Skip(1))
            {
                RemoveWithWhitespace(extra);
            }
        }

        SetChild(server, "address", address);
        if (spec.Port is { } port)
        {
            SetChild(server, "port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (spec.Protocol is { } protocol)
        {
            SetChild(server, "protocol", protocol);
        }

        var enrollment = client.Element("enrollment");
        if (newEnrollment && enrollmentBlock)
        {
            if (enrollment is null)
            {
                enrollment = new XElement("enrollment");
                SetChildElement(client, enrollment);
            }

            SetChild(enrollment, "enabled", "yes");
            SetChild(enrollment, "manager_address", address);
            if (spec.EnrollmentPort is { } enrollPort)
            {
                SetChild(enrollment, "port", enrollPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (!string.IsNullOrWhiteSpace(spec.Groups))
            {
                SetChild(enrollment, "groups", spec.Groups);
            }
        }
        else if (enrollment?.Element("manager_address") is { } managerAddress)
        {
            // Sonst meldet sich der Client nach einer Ablehnung beim alten Manager an.
            managerAddress.Value = address;
        }

        return Save(doc, declaration);
    }

    /// <summary>Status aus wazuh-agentd.state bzw. wazuh-agent.state: true = connected, false = pending/disconnected, null = unbekannt.</summary>
    public static bool? ParseState(string? text)
    {
        var match = text is null ? Match.Empty : StatePattern().Match(text);
        return match.Success ? match.Groups[1].Value.Equals("connected", StringComparison.OrdinalIgnoreCase) : null;
    }

    /// <summary>Rohwert des Status ("connected", "pending", "disconnected") für Meldungen.</summary>
    public static string? StateValue(string? text) => text is not null && StatePattern().Match(text) is { Success: true } m ? m.Groups[1].Value : null;

    // --- intern ---------------------------------------------------------------------------------------

    private static (XDocument Doc, string? Declaration) Load(string text)
    {
        string? declaration = null;
        var body = text;
        if (DeclarationPattern().Match(text) is { Success: true } m)
        {
            declaration = m.Value;
            body = text[m.Length..];
        }

        try
        {
            return (XDocument.Parse($"<{Wrapper}>{body}</{Wrapper}>", LoadOptions.PreserveWhitespace), declaration);
        }
        catch (System.Xml.XmlException e)
        {
            throw new InvalidOperationException($"ossec.conf is not readable: {e.Message}");
        }
    }

    private static string Save(XDocument doc, string? declaration)
    {
        var builder = new StringBuilder(declaration);
        foreach (var node in doc.Root!.Nodes())
        {
            builder.Append(node.ToString(SaveOptions.DisableFormatting));
        }

        return builder.ToString();
    }

    private static XElement? Client(XDocument doc) => doc.Root!.Descendants("client").FirstOrDefault();

    private static IEnumerable<XElement> Endpoints(XDocument doc) =>
        doc.Root!.Descendants("agent").Elements("manager").Elements("endpoint");

    private static string? Text(XElement? element) => element is null || string.IsNullOrWhiteSpace(element.Value) ? null : element.Value.Trim();

    private static string HostPart(string address) => address.Contains(':') && !address.StartsWith('[') ? $"[{address}]" : address;

    /// <summary>Kind setzen; neu angelegt nach dem letzten Kind mit dessen Einrückung.</summary>
    private static void SetChild(XElement parent, string name, string value)
    {
        if (parent.Element(name) is { } existing)
        {
            existing.Value = value;
            return;
        }

        SetChildElement(parent, new XElement(name, value));
    }

    private static void SetChildElement(XElement parent, XElement element)
    {
        var last = parent.Elements().LastOrDefault();
        if (last is null)
        {
            parent.Add(element);
            return;
        }

        var indent = last.PreviousNode is XText { Value: var ws } && string.IsNullOrWhiteSpace(ws) ? ws : null;
        last.AddAfterSelf(element);
        if (indent is not null)
        {
            element.AddBeforeSelf(new XText(indent));
        }
    }

    private static void RemoveWithWhitespace(XElement element)
    {
        if (element.PreviousNode is XText text && string.IsNullOrWhiteSpace(text.Value))
        {
            text.Remove();
        }

        element.Remove();
    }

    [GeneratedRegex(@"^\s*<\?xml[^>]*\?>")]
    private static partial Regex DeclarationPattern();

    [GeneratedRegex(@"^\s*status\s*=\s*'?(\w+)'?", RegexOptions.Multiline)]
    private static partial Regex StatePattern();
}

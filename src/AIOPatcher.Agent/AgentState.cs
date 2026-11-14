using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIOPatcher.Agent;

/// <summary>Registrierungsdaten des Agents: Konfiguration, Client-Zertifikat, Schlüssel und gepinnte CA.</summary>
public sealed class AgentState
{
    public const string ConfigFile = "agent.json";
    public const string CaFile = "ca.crt";
    public const string CertFile = "client.crt";
    public const string KeyFile = "client.key";

    public required string Directory { get; init; }
    public required AgentConfig Config { get; init; }
    public required X509Certificate2 CaCertificate { get; init; }
    public required X509Certificate2 ClientCertificate { get; init; }

    public static string DefaultDirectory =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AIOPatcher", "Agent")
            : "/var/lib/aiopatcher-agent";

    public static string AgentVersion { get; } =
        (typeof(AgentState).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    public static bool Exists(string directory) => File.Exists(Path.Combine(directory, ConfigFile));

    public static AgentState Load(string directory)
    {
        var config = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(directory, ConfigFile)), AgentStateJsonContext.Default.AgentConfig)
                     ?? throw new InvalidDataException($"{ConfigFile} ist leer.");

        var ca = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(Path.Combine(directory, CaFile)));
        using var pemClient = X509Certificate2.CreateFromPemFile(Path.Combine(directory, CertFile), Path.Combine(directory, KeyFile));

        return new AgentState
        {
            Directory = directory,
            Config = config,
            CaCertificate = ca,
            ClientCertificate = CertificateHelper.ToUsableForTls(pemClient),
        };
    }

    public static void Save(string directory, AgentConfig config, string caPem, string clientCertPem, ECDsa key)
    {
        System.IO.Directory.CreateDirectory(directory);
        RestrictDirectory(directory);

        WriteFile(Path.Combine(directory, KeyFile), key.ExportPkcs8PrivateKeyPem());
        WriteFile(Path.Combine(directory, CertFile), clientCertPem);
        WriteFile(Path.Combine(directory, CaFile), caPem);
        WriteFile(Path.Combine(directory, ConfigFile), JsonSerializer.Serialize(config, AgentStateJsonContext.Default.AgentConfig));
    }

    private static void WriteFile(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content.TrimEnd() + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, path, overwrite: true);
    }

    private static void RestrictDirectory(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return;
        }

        // Unter ProgramData dürfen normale Benutzer sonst mitlesen (privater Schlüssel!).
        // Nur bei erhöhten Rechten anpassen: ein nicht erhöhter Testlauf würde sich sonst selbst aussperren.
        if (Environment.IsPrivilegedProcess)
        {
            RestrictToSystemAndAdministrators(directory);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RestrictToSystemAndAdministrators(string directory)
    {
        var security = new System.Security.AccessControl.DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false); // Vererbung kappen
        foreach (var sid in new[] { System.Security.Principal.WellKnownSidType.LocalSystemSid, System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(sid, null),
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow));
        }

        new DirectoryInfo(directory).SetAccessControl(security);
    }
}

public sealed record AgentConfig(string ServerUrl, Guid HostId, Guid SiteId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(AgentConfig))]
public sealed partial class AgentStateJsonContext : JsonSerializerContext;

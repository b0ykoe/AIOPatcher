namespace AIOPatcher.Agent.Commands;

/// <summary>Minimaler Parser für "befehl --schluessel wert --flag".</summary>
public sealed class CommandLine
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    public CommandLine(string[] args)
    {
        Command = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0].ToLowerInvariant() : "run";

        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var name = args[i][2..];
            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            _options[name] = hasValue ? args[++i] : null;
        }
    }

    public string Command { get; }

    public string? Get(string name) => _options.GetValueOrDefault(name);

    public bool Has(string name) => _options.ContainsKey(name);
}

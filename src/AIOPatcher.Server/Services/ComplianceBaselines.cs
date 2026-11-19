using System.Text.Json;
using System.Text.Json.Serialization;
using AIOPatcher.Contracts;
using AIOPatcher.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Services;

// --- Regeln einer Compliance-Vorgabe (als JSON in ComplianceBaseline.RulesJson) ------------------------

/// <summary>Soll-Zustand. Linux-Regeln (ssh.*, Schlüssel) greifen nur auf Linux, RDP nur auf Windows; Benutzer je nach Os.</summary>
public sealed record BaselineRules(
    IReadOnlyList<SettingRule> Settings,
    IReadOnlyList<UserRule> Users,
    bool OnlyListedAdmins,
    IReadOnlyList<KeyRule> Keys,
    ForeignKeyPolicy ForeignKeys,
    Guid? FirewallProfileId)
{
    public static BaselineRules Empty => new([], [], false, [], ForeignKeyPolicy.Ignore, null);
}

/// <summary>Einstellung mit Sollwert. ssh.maxAuthTries: höchstens dieser Wert.</summary>
public sealed record SettingRule(string Id, string Value);

/// <summary>Present: muss existieren und aktiv sein. Admin: zusätzlich Administrator. Disabled: darf nicht aktiv sein (fehlt = ok).</summary>
public enum UserRequirement
{
    Present,
    Admin,
    Disabled,
}

public enum BaselineOs
{
    Any,
    Linux,
    Windows,
}

public sealed record UserRule(string Name, UserRequirement Requirement, BaselineOs Os = BaselineOs.Any);

/// <summary>Öffentlicher Schlüssel, der bei diesem Linux-Benutzer in authorized_keys stehen muss.</summary>
public sealed record KeyRule(string User, string PublicKey);

/// <summary>Andere Schlüssel bei Benutzern mit Schlüssel-Regel: übergehen, melden oder beim Angleichen entfernen.</summary>
public enum ForeignKeyPolicy
{
    Ignore,
    Report,
    Remove,
}

// --- Ergebnis der Prüfung -------------------------------------------------------------------------------

public enum RuleState
{
    Ok,
    Violation,
    Unknown,
}

/// <summary>
/// Eine geprüfte Regel. Category: setting, user, admin, key, foreignKey, firewall. Subject: Einstellung, Benutzer,
/// Fingerabdruck oder Regelsatz. Reason (bei Unknown oder nicht behebbar): noReport, notReported, oldAgent, noUsers,
/// groupPolicy, noInclude, notManageable, userMissing, reportOnly, profileMissing.
/// </summary>
public sealed record RuleResult(string Key, string Category, string Subject, RuleState State, string? Expected, string? Actual, bool Fixable, string? Reason)
{
    /// <summary>Schritte zum Angleichen (nur Server, geht nicht ans Dashboard).</summary>
    [JsonIgnore]
    public IReadOnlyList<SystemChange> Fixes { get; init; } = [];

    [JsonIgnore]
    public Guid? FirewallVersionId { get; init; }
}

/// <summary>Wie ein Host zu seiner Vorgabe kam: ausgewählt, über Tag/Site oder als "alle übrigen".</summary>
public sealed record BaselineMembership(Guid BaselineId, string BaselineName, PlanVia Via);

/// <summary>Aktueller Firewall-Stand für die Prüfung: ausgerollte Version (Server) und Hash, den der Host meldet.</summary>
public sealed record FirewallTarget(Guid ProfileId, string ProfileName, Guid LatestVersionId, int LatestNumber, string LatestHash, FirewallConfig LatestConfig);

/// <summary>Compliance-Vorgaben: Regeln prüfen, Hosts zuordnen, Abweichungen in Agent-Änderungen übersetzen.</summary>
public static class ComplianceBaselines
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static BaselineRules Parse(string json) => JsonSerializer.Deserialize<BaselineRules>(json, Json) ?? BaselineRules.Empty;

    public static string Serialize(BaselineRules rules) => JsonSerializer.Serialize(rules, Json);

    // --- Einstellungen ---------------------------------------------------------------------------------

    /// <summary>Bekannte Einstellungen: Id → (System, erlaubte Werte; leer = Zahl in Spanne).</summary>
    public static readonly IReadOnlyDictionary<string, (OsFamily Os, string[] Values, int Min, int Max)> Settings =
        new Dictionary<string, (OsFamily, string[], int, int)>
        {
            ["ssh.permitRootLogin"] = (OsFamily.Linux, ["no", "prohibit-password", "forced-commands-only", "yes"], 0, 0),
            ["ssh.passwordAuthentication"] = (OsFamily.Linux, ["no", "yes"], 0, 0),
            ["ssh.kbdInteractiveAuthentication"] = (OsFamily.Linux, ["no", "yes"], 0, 0),
            ["ssh.permitEmptyPasswords"] = (OsFamily.Linux, ["no", "yes"], 0, 0),
            ["ssh.x11Forwarding"] = (OsFamily.Linux, ["no", "yes"], 0, 0),
            ["ssh.maxAuthTries"] = (OsFamily.Linux, [], 1, 20),
            ["ssh.port"] = (OsFamily.Linux, [], 1, 65535),
            ["rdp.enabled"] = (OsFamily.Windows, ["off", "on"], 0, 0),
            ["rdp.nla"] = (OsFamily.Windows, ["1", "0"], 0, 0),
            ["rdp.securityLayer"] = (OsFamily.Windows, ["2", "1", "0"], 0, 0),
            ["rdp.encryption"] = (OsFamily.Windows, ["3", "4", "2", "1"], 0, 0),
        };

    /// <summary>Ältere OpenSSH melden "without-password" für prohibit-password.</summary>
    private static string Normalize(string id, string? value)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        return id == "ssh.permitRootLogin" && v == "without-password" ? "prohibit-password" : v;
    }

    private static bool Matches(string id, string expected, string? actual)
    {
        if (actual is null)
        {
            return false;
        }

        return id == "ssh.maxAuthTries"
            ? int.TryParse(actual, out var a) && int.TryParse(expected, out var e) && a <= e
            : Normalize(id, actual) == Normalize(id, expected);
    }

    // --- Prüfen der Eingabe ----------------------------------------------------------------------------

    public static List<string> Validate(BaselineRules rules)
    {
        var errors = new List<string>();
        foreach (var s in rules.Settings ?? [])
        {
            if (s.Id is null || !Settings.TryGetValue(s.Id, out var def))
            {
                errors.Add(L.T($"Unbekannte Einstellung {s.Id}.", $"Unknown setting {s.Id}."));
            }
            else if (def.Values.Length > 0 ? !def.Values.Contains(s.Value) : !(int.TryParse(s.Value, out var n) && n >= def.Min && n <= def.Max))
            {
                errors.Add(L.T($"{s.Id}: ungültiger Wert \"{s.Value}\".", $"{s.Id}: invalid value \"{s.Value}\"."));
            }
        }

        if ((rules.Settings ?? []).GroupBy(s => s.Id).Any(g => g.Count() > 1))
        {
            errors.Add(L.T("Jede Einstellung nur einmal.", "Each setting only once."));
        }

        foreach (var u in rules.Users ?? [])
        {
            if (string.IsNullOrWhiteSpace(u.Name) || u.Name.Length > 32 || u.Name.Any(c => char.IsWhiteSpace(c) && c != ' ') || u.Name.Contains(':'))
            {
                errors.Add(L.T($"Ungültiger Benutzername \"{u.Name}\".", $"Invalid user name \"{u.Name}\"."));
            }
        }

        if ((rules.Users ?? []).GroupBy(u => (u.Name.ToLowerInvariant(), u.Os)).Any(g => g.Count() > 1))
        {
            errors.Add(L.T("Jeder Benutzer nur einmal je System.", "Each user only once per system."));
        }

        foreach (var k in rules.Keys ?? [])
        {
            if (string.IsNullOrWhiteSpace(k.User) || !SshPublicKeys.IsValid(k.PublicKey))
            {
                errors.Add(L.T($"Schlüssel für \"{k.User}\": genau ein öffentlicher Schlüssel (eine Zeile, z. B. ssh-ed25519 AAAA… name).",
                    $"Key for \"{k.User}\": exactly one public key (one line, e.g. ssh-ed25519 AAAA… name)."));
            }
        }

        if (rules.Settings?.Count > 30 || rules.Users?.Count > 50 || rules.Keys?.Count > 100)
        {
            errors.Add(L.T("Zu viele Regeln.", "Too many rules."));
        }

        return errors;
    }

    /// <summary>Leerzeichen am Rand weg, Schlüssel in eine Zeile.</summary>
    public static BaselineRules Clean(BaselineRules rules) => new(
        (rules.Settings ?? []).Select(s => s with { Value = (s.Value ?? "").Trim() }).ToList(),
        (rules.Users ?? []).Select(u => u with { Name = (u.Name ?? "").Trim() }).ToList(),
        rules.OnlyListedAdmins,
        (rules.Keys ?? []).Select(k => new KeyRule((k.User ?? "").Trim(), (k.PublicKey ?? "").Trim())).ToList(),
        rules.ForeignKeys,
        rules.FirewallProfileId);

    // --- Zuordnung -------------------------------------------------------------------------------------

    public sealed record HostInfo(Guid Id, Guid SiteId, string? Tags);

    /// <summary>Wie bei Patch-Plänen: ausgewählte Hosts, dann Tag/Site (ältere Vorgabe zuerst), dann "alle übrigen".</summary>
    public static Dictionary<Guid, BaselineMembership> Resolve(IReadOnlyList<ComplianceBaseline> baselines, IReadOnlyList<HostInfo> hosts)
    {
        var result = new Dictionary<Guid, BaselineMembership>();
        var ordered = baselines.OrderBy(b => b.CreatedAt).ThenBy(b => b.Name).ToList();
        var active = hosts.Select(h => h.Id).ToHashSet();

        foreach (var baseline in ordered)
        {
            foreach (var member in baseline.Hosts.Where(m => active.Contains(m.HostId) && !result.ContainsKey(m.HostId)))
            {
                result[member.HostId] = new BaselineMembership(baseline.Id, baseline.Name, PlanVia.Host);
            }
        }

        foreach (var baseline in ordered.Where(HasRule))
        {
            foreach (var host in hosts.Where(h => !result.ContainsKey(h.Id) && MatchesRule(baseline, h)))
            {
                result[host.Id] = new BaselineMembership(baseline.Id, baseline.Name, PlanVia.Rule);
            }
        }

        if (ordered.FirstOrDefault(b => b.IncludeOthers) is { } others)
        {
            foreach (var host in hosts.Where(h => !result.ContainsKey(h.Id)))
            {
                result[host.Id] = new BaselineMembership(others.Id, others.Name, PlanVia.Others);
            }
        }

        return result;
    }

    public static bool HasRule(ComplianceBaseline b) => !string.IsNullOrEmpty(b.MemberTags) || b.MemberSiteId is not null;

    /// <summary>Tags: einer muss passen. Site: muss passen. Beides gesetzt: beides.</summary>
    public static bool MatchesRule(ComplianceBaseline b, HostInfo host)
    {
        if (!HasRule(b) || (b.MemberSiteId is { } site && host.SiteId != site))
        {
            return false;
        }

        if (string.IsNullOrEmpty(b.MemberTags))
        {
            return true;
        }

        var tags = PatchRules.SplitTags(host.Tags);
        return PatchRules.SplitTags(b.MemberTags).Any(t => tags.Contains(t, StringComparer.Ordinal));
    }

    public static async Task<(List<ComplianceBaseline> Baselines, Dictionary<Guid, BaselineMembership> Members)> LoadAsync(AppDbContext db, CancellationToken ct)
    {
        var baselines = await db.ComplianceBaselines.AsNoTracking().Include(b => b.Hosts).ToListAsync(ct);
        var hosts = await db.Hosts.AsNoTracking().Where(h => !h.IsRevoked).Select(h => new HostInfo(h.Id, h.SiteId, h.Tags)).ToListAsync(ct);
        return (baselines, Resolve(baselines, hosts));
    }

    /// <summary>Neueste Version je Firewall-Regelsatz, den eine der Vorgaben verlangt.</summary>
    public static async Task<Dictionary<Guid, FirewallTarget>> FirewallTargetsAsync(AppDbContext db, IEnumerable<BaselineRules> rules, CancellationToken ct)
    {
        var ids = rules.Where(r => r.FirewallProfileId is not null).Select(r => r.FirewallProfileId!.Value).Distinct().ToList();
        var versions = await db.FirewallProfileVersions.AsNoTracking().Where(v => ids.Contains(v.ProfileId))
            .Select(v => new { v.Id, v.ProfileId, ProfileName = v.Profile!.Name, v.Number, v.Hash, v.ConfigJson }).ToListAsync(ct);
        return versions.GroupBy(v => v.ProfileId).ToDictionary(g => g.Key, g =>
        {
            var latest = g.MaxBy(v => v.Number)!;
            return new FirewallTarget(g.Key, latest.ProfileName, latest.Id, latest.Number, latest.Hash,
                JsonSerializer.Deserialize(latest.ConfigJson, AgentJsonContext.Default.FirewallConfig)!);
        });
    }

    // --- Soll/Ist ----------------------------------------------------------------------------------------

    /// <param name="appliedVersionId">Vom Server zuletzt erfolgreich ausgerollte Firewall-Version des Hosts.</param>
    public static List<RuleResult> Evaluate(BaselineRules rules, OsFamily os, ComplianceReport? report, Guid? appliedVersionId,
        IReadOnlyDictionary<Guid, FirewallTarget> firewall)
    {
        var results = new List<RuleResult>();
        var linux = os == OsFamily.Linux;
        var windows = os == OsFamily.Windows;
        bool OsMatches(BaselineOs o) => o == BaselineOs.Any || (o == BaselineOs.Linux && linux) || (o == BaselineOs.Windows && windows);

        // Einstellungen: alle Sollwerte eines Systems ergeben beim Angleichen einen gemeinsamen Schritt
        foreach (var rule in rules.Settings.Where(s => Settings.TryGetValue(s.Id, out var d) && d.Os == os))
        {
            var key = $"setting:{rule.Id}";
            if (report is null)
            {
                results.Add(new RuleResult(key, "setting", rule.Id, RuleState.Unknown, rule.Value, null, false, "noReport"));
                continue;
            }

            var actual = report.RemoteAccess.FirstOrDefault(s => s.Id == rule.Id);
            if (actual is null)
            {
                results.Add(new RuleResult(key, "setting", rule.Id, RuleState.Unknown, rule.Value, null, false, "notReported"));
                continue;
            }

            if (Matches(rule.Id, rule.Value, actual.Value))
            {
                results.Add(new RuleResult(key, "setting", rule.Id, RuleState.Ok, rule.Value, actual.Value, false, null));
                continue;
            }

            // Fixable meldet der Agent: Linux ohne Include-Zeile bzw. Windows-Gruppenrichtlinie = nein. rdp.enabled war früher nur lesbar.
            var fixable = actual.Fixable || rule.Id == "rdp.enabled";
            var reason = fixable ? null : actual.Note switch { "group-policy" => "groupPolicy", "no-include" => "noInclude", _ => "notManageable" };
            results.Add(new RuleResult(key, "setting", rule.Id, RuleState.Violation, rule.Value, actual.Value, fixable, reason)
            {
                Fixes = fixable ? [new SystemChange(SystemChangeKind.RemoteAccessSet, Values: new Dictionary<string, string> { [rule.Id] = rule.Value })] : [],
            });
        }

        // Benutzer
        var users = report?.Users ?? [];
        var comparer = windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var userRules = rules.Users.Where(u => OsMatches(u.Os)).ToList();
        var willExist = userRules.Where(u => u.Requirement != UserRequirement.Disabled).Select(u => u.Name).ToHashSet(comparer);
        foreach (var rule in userRules)
        {
            var key = $"user:{rule.Name}";
            var expected = rule.Requirement.ToString();
            if (report is null || users.Count == 0)
            {
                results.Add(new RuleResult(key, "user", rule.Name, RuleState.Unknown, expected, null, false, report is null ? "noReport" : "noUsers"));
                continue;
            }

            var user = users.FirstOrDefault(u => comparer.Equals(u.Name, rule.Name));
            var actual = user is null ? "missing" : !Active(user, linux) ? "disabled" : user.IsAdmin ? "admin" : "present";
            var fixes = new List<SystemChange>();
            switch (rule.Requirement)
            {
                case UserRequirement.Present or UserRequirement.Admin:
                    var admin = rule.Requirement == UserRequirement.Admin;
                    if (user is null)
                    {
                        fixes.Add(new SystemChange(SystemChangeKind.UserCreate, UserName: rule.Name, Admin: admin));
                        break;
                    }

                    if (!user.Enabled)
                    {
                        fixes.Add(new SystemChange(SystemChangeKind.UserUnlock, UserName: user.Name));
                    }

                    if (admin && !user.IsAdmin)
                    {
                        fixes.Add(new SystemChange(SystemChangeKind.UserGrantAdmin, UserName: user.Name));
                    }

                    break;
                case UserRequirement.Disabled when user is not null && Active(user, linux):
                    fixes.Add(new SystemChange(SystemChangeKind.UserLock, UserName: user.Name));
                    break;
            }

            results.Add(fixes.Count == 0
                ? new RuleResult(key, "user", rule.Name, RuleState.Ok, expected, actual, false, null)
                : new RuleResult(key, "user", rule.Name, RuleState.Violation, expected, actual, true, null) { Fixes = fixes });
        }

        // Nur diese Admins: eingebaute Konten (root/Administrator) regeln die Benutzer-Regeln selbst
        if (rules.OnlyListedAdmins && report is not null && users.Count > 0)
        {
            var allowed = userRules.Where(u => u.Requirement == UserRequirement.Admin).Select(u => u.Name).ToHashSet(comparer);
            foreach (var user in users.Where(u => u.IsAdmin && u.Enabled && !u.IsBuiltIn && !allowed.Contains(u.Name)))
            {
                results.Add(new RuleResult($"admin:{user.Name}", "admin", user.Name, RuleState.Violation, "notAdmin", "admin", true, null)
                {
                    Fixes = [new SystemChange(SystemChangeKind.UserRevokeAdmin, UserName: user.Name)],
                });
            }
        }

        // SSH-Schlüssel (nur Linux)
        if (linux)
        {
            foreach (var group in rules.Keys.GroupBy(k => k.User, comparer))
            {
                var user = users.FirstOrDefault(u => comparer.Equals(u.Name, group.Key));
                var wanted = group.Select(k => (Rule: k, Info: SshPublicKeys.KeyOf(k.PublicKey))).Where(k => k.Info is not null).ToList();
                foreach (var (rule, info) in wanted)
                {
                    var key = $"key:{group.Key}:{info!.Fingerprint}";
                    var subject = $"{group.Key}|{info.Fingerprint}|{info.Comment}";
                    if (report is null || users.Count == 0)
                    {
                        results.Add(new RuleResult(key, "key", subject, RuleState.Unknown, info.Fingerprint, null, false, report is null ? "noReport" : "noUsers"));
                    }
                    else if (user is null)
                    {
                        var creatable = willExist.Contains(group.Key);
                        results.Add(new RuleResult(key, "key", subject, RuleState.Violation, info.Fingerprint, "missing", creatable, creatable ? null : "userMissing")
                        {
                            Fixes = creatable ? [new SystemChange(SystemChangeKind.UserAddSshKey, UserName: group.Key, SshKey: rule.PublicKey)] : [],
                        });
                    }
                    else if (user.Keys is null)
                    {
                        results.Add(new RuleResult(key, "key", subject, RuleState.Unknown, info.Fingerprint, null, false, "oldAgent"));
                    }
                    else if (user.Keys.Any(k => k.Fingerprint == info.Fingerprint))
                    {
                        results.Add(new RuleResult(key, "key", subject, RuleState.Ok, info.Fingerprint, info.Fingerprint, false, null));
                    }
                    else
                    {
                        results.Add(new RuleResult(key, "key", subject, RuleState.Violation, info.Fingerprint, "missing", true, null)
                        {
                            Fixes = [new SystemChange(SystemChangeKind.UserAddSshKey, UserName: user.Name, SshKey: rule.PublicKey)],
                        });
                    }
                }

                if (rules.ForeignKeys != ForeignKeyPolicy.Ignore && user?.Keys is { } present)
                {
                    var known = wanted.Select(w => w.Info!.Fingerprint).ToHashSet();
                    foreach (var foreign in present.Where(k => !known.Contains(k.Fingerprint)).DistinctBy(k => k.Fingerprint))
                    {
                        var remove = rules.ForeignKeys == ForeignKeyPolicy.Remove;
                        results.Add(new RuleResult($"foreignKey:{user.Name}:{foreign.Fingerprint}", "foreignKey", $"{user.Name}|{foreign.Fingerprint}|{foreign.Comment}",
                            RuleState.Violation, null, foreign.Fingerprint, remove, remove ? null : "reportOnly")
                        {
                            Fixes = remove ? [new SystemChange(SystemChangeKind.UserRemoveSshKey, UserName: user.Name, SshKey: foreign.Fingerprint)] : [],
                        });
                    }
                }
            }
        }

        // Firewall-Regelsatz: neueste Version muss ausgerollt sein und der Host darf nicht abweichen
        if (rules.FirewallProfileId is { } profileId)
        {
            if (!firewall.TryGetValue(profileId, out var target))
            {
                results.Add(new RuleResult("firewall", "firewall", "?", RuleState.Unknown, null, null, false, "profileMissing"));
            }
            else if (report is null)
            {
                results.Add(new RuleResult("firewall", "firewall", target.ProfileName, RuleState.Unknown, $"v{target.LatestNumber}", null, false, "noReport"));
            }
            else
            {
                var ok = appliedVersionId == target.LatestVersionId && report.Firewall.AppliedConfigHash == target.LatestHash;
                var actual = appliedVersionId == target.LatestVersionId ? "drift" : report.Firewall.AppliedConfigHash is null ? "none" : "other";
                var fixable = report.Firewall.Manageable;
                results.Add(ok
                    ? new RuleResult("firewall", "firewall", target.ProfileName, RuleState.Ok, $"v{target.LatestNumber}", $"v{target.LatestNumber}", false, null)
                    : new RuleResult("firewall", "firewall", target.ProfileName, RuleState.Violation, $"v{target.LatestNumber}", actual, fixable, fixable ? null : "notManageable")
                    {
                        Fixes = fixable ? [new SystemChange(SystemChangeKind.FirewallApply, Firewall: target.LatestConfig)] : [],
                        FirewallVersionId = target.LatestVersionId,
                    });
            }
        }

        return results;
    }

    /// <summary>Linux-root bleibt "aktiv" (Shell), sein Passwort gesperrt zählt als deaktiviert.</summary>
    private static bool Active(LocalUser user, bool linux) =>
        user.Enabled && !(linux && user.IsBuiltIn && user.PasswordState is "locked" or "none");

    private static readonly SystemChangeKind[] Order =
    [
        SystemChangeKind.UserCreate, SystemChangeKind.UserUnlock, SystemChangeKind.UserGrantAdmin, SystemChangeKind.UserAddSshKey,
        SystemChangeKind.RemoteAccessSet, SystemChangeKind.UserRemoveSshKey, SystemChangeKind.UserRevokeAdmin, SystemChangeKind.UserLock,
        SystemChangeKind.FirewallApply,
    ];

    /// <summary>
    /// Schritte zum Angleichen in sicherer Reihenfolge: erst Benutzer und Schlüssel anlegen, dann SSH/RDP (z. B. Passwort-Login
    /// aus erst, wenn der Schlüssel da ist), dann entfernen/sperren, Firewall zuletzt (mit Rückfall). Sollwerte zusammengefasst.
    /// </summary>
    public static List<SystemChange> Steps(IEnumerable<RuleResult> results)
    {
        var fixes = results.Where(r => r.State == RuleState.Violation && r.Fixable).SelectMany(r => r.Fixes).ToList();
        var values = fixes.Where(f => f.Kind == SystemChangeKind.RemoteAccessSet).SelectMany(f => f.Values ?? new Dictionary<string, string>())
            .GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Last().Value);
        var steps = fixes.Where(f => f.Kind != SystemChangeKind.RemoteAccessSet)
            .DistinctBy(f => (f.Kind, f.UserName, f.SshKey))
            .ToList();
        if (values.Count > 0)
        {
            steps.Add(new SystemChange(SystemChangeKind.RemoteAccessSet, Values: values));
        }

        return steps.OrderBy(s => Array.IndexOf(Order, s.Kind)).ToList();
    }

    /// <summary>Schalter, die für diesen Schritt an sein müssen.</summary>
    public static Feature FeatureFor(SystemChangeKind kind) => kind switch
    {
        SystemChangeKind.FirewallApply or SystemChangeKind.FirewallRestoreOriginal => Feature.FirewallManagement,
        SystemChangeKind.RemoteAccessFix or SystemChangeKind.RemoteAccessRevert or SystemChangeKind.RemoteAccessSet => Feature.RemoteAccessFixes,
        SystemChangeKind.AutoUpdatesOff or SystemChangeKind.AutoUpdatesOn => Feature.AutoUpdateControl,
        SystemChangeKind.MonitoringInstall or SystemChangeKind.MonitoringConfigure or SystemChangeKind.MonitoringUninstall => Feature.MonitoringClients,
        _ => Feature.UserManagement,
    };
}

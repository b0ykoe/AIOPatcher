using System.Net;
using System.Net.Sockets;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace AIOPatcher.Server.Endpoints;

/// <summary>
/// IPAM light: Subnetze pro Site, Adressen aus Agents, OPNsense (ARP) und manuellen Einträgen,
/// Konflikte (doppelte Adressen) und die nächste freie IPv4-Adresse.
/// </summary>
public static class IpamEndpoints
{
    public sealed record SubnetDto(Guid Id, Guid SiteId, string Cidr, string Name, string? Description, int Used, long? Size);
    public sealed record SubnetRequest(Guid SiteId, string Cidr, string Name, string? Description);
    public sealed record AddressDto(long Id, string Address, int? PrefixLength, Guid? SubnetId, string? SubnetName, Guid? HostId, string? HostName,
        string? InterfaceName, string? MacAddress, string? DeviceName, IpSource Source, string? Description, DateTime LastSeenAt, bool Conflict);
    public sealed record AddressRequest(Guid SiteId, string Address, string? Hostname, string? Description);
    public sealed record NextFreeDto(string? Address);

    public static void MapIpamEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/ipam").RequireAuthorization();
        var op = api.MapGroup("").RequireAuthorization(UserAccounts.OperatorPolicy);

        api.MapGet("/subnets", ListSubnets);
        op.MapPost("/subnets", CreateSubnet);
        op.MapPut("/subnets/{id:guid}", UpdateSubnet);
        op.MapDelete("/subnets/{id:guid}", DeleteSubnet);
        api.MapGet("/subnets/{id:guid}/next-free", NextFree);

        api.MapGet("/addresses", ListAddresses);
        op.MapPost("/addresses", CreateAddress);
        op.MapDelete("/addresses/{id:long}", DeleteAddress);
    }

    // --- Subnetze -------------------------------------------------------------------------------------

    private static async Task<List<SubnetDto>> ListSubnets(Guid siteId, AppDbContext db, CancellationToken ct)
    {
        var subnets = await db.Subnets.Where(s => s.SiteId == siteId).OrderBy(s => s.Name).ToListAsync(ct);
        var used = await db.IpAddresses.Where(a => a.SiteId == siteId && a.SubnetId != null)
            .GroupBy(a => a.SubnetId!.Value)
            .Select(g => new { g.Key, Count = g.Select(a => a.Address).Distinct().Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return subnets
            .Select(s => new SubnetDto(s.Id, s.SiteId, s.Cidr, s.Name, s.Description, used.GetValueOrDefault(s.Id), UsableSize(s.Cidr)))
            .OrderBy(s => SortKey(s.Cidr))
            .ToList();
    }

    private static async Task<IResult> CreateSubnet(SubnetRequest request, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        if (await ValidateSubnetAsync(request, null, db, ct) is { } problem)
        {
            return problem;
        }

        var subnet = new Subnet { SiteId = request.SiteId, Cidr = Normalize(request.Cidr)!, Name = request.Name.Trim(), Description = Trim(request.Description) };
        db.Subnets.Add(subnet);
        await db.SaveChangesAsync(ct);
        await AssignSubnetsAsync(db, request.SiteId, ct);
        audit.Add("ipam.subnet.created", $"{subnet.Cidr} {subnet.Name}", siteId: subnet.SiteId);
        await db.SaveChangesAsync(ct);
        return Results.Ok(subnet.Id);
    }

    private static async Task<IResult> UpdateSubnet(Guid id, SubnetRequest request, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var subnet = await db.Subnets.FindAsync([id], ct);
        if (subnet is null)
        {
            return Results.NotFound();
        }

        if (await ValidateSubnetAsync(request with { SiteId = subnet.SiteId }, id, db, ct) is { } problem)
        {
            return problem;
        }

        subnet.Cidr = Normalize(request.Cidr)!;
        subnet.Name = request.Name.Trim();
        subnet.Description = Trim(request.Description);
        await db.SaveChangesAsync(ct);
        await AssignSubnetsAsync(db, subnet.SiteId, ct);
        audit.Add("ipam.subnet.updated", $"{subnet.Cidr} {subnet.Name}", siteId: subnet.SiteId);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteSubnet(Guid id, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var subnet = await db.Subnets.FindAsync([id], ct);
        if (subnet is null)
        {
            return Results.NotFound();
        }

        db.Subnets.Remove(subnet);
        audit.Add("ipam.subnet.deleted", $"{subnet.Cidr} {subnet.Name}", siteId: subnet.SiteId);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult?> ValidateSubnetAsync(SubnetRequest request, Guid? id, AppDbContext db, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var cidr = Normalize(request.Cidr);
        if (cidr is null)
        {
            errors["cidr"] = [L.T("Ungültiges Netz, z. B. 10.10.0.0/24 oder 2a01:4f8::/64.", "Invalid network, e.g. 10.10.0.0/24 or 2a01:4f8::/64.")];
        }
        else if (await db.Subnets.AnyAsync(s => s.SiteId == request.SiteId && s.Cidr == cidr && s.Id != id, ct))
        {
            errors["cidr"] = [L.T("Dieses Netz existiert in der Site bereits.", "This network already exists in the site.")];
        }

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100)
        {
            errors["name"] = [L.T("Name muss 1 bis 100 Zeichen lang sein.", "Name must be 1 to 100 characters long.")];
        }

        if (!await db.Sites.AnyAsync(s => s.Id == request.SiteId, ct))
        {
            errors["siteId"] = [L.T("Site existiert nicht.", "Site does not exist.")];
        }

        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }

    /// <summary>Erste freie IPv4-Adresse (ohne Netz-, Broadcast- und erste Adresse, die oft das Gateway ist).</summary>
    private static async Task<IResult> NextFree(Guid id, AppDbContext db, CancellationToken ct)
    {
        var subnet = await db.Subnets.FindAsync([id], ct);
        if (subnet is null)
        {
            return Results.NotFound();
        }

        var used = (await db.IpAddresses.Where(a => a.SiteId == subnet.SiteId).Select(a => a.Address).ToListAsync(ct)).ToHashSet();
        return Results.Ok(new NextFreeDto(FirstFree(subnet.Cidr, used)));
    }

    public static string? FirstFree(string cidr, IReadOnlySet<string> used)
    {
        if (!IPNetwork.TryParse(cidr, out var network) || network.BaseAddress.AddressFamily != AddressFamily.InterNetwork || network.PrefixLength > 30)
        {
            return null;
        }

        var start = ToUInt(network.BaseAddress);
        var count = 1u << (32 - network.PrefixLength);
        // .0 = Netz, .1 = meist Gateway, letzte = Broadcast
        for (var offset = 2u; offset < count - 1; offset++)
        {
            var candidate = FromUInt(start + offset).ToString();
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    // --- Adressen -------------------------------------------------------------------------------------

    private static async Task<List<AddressDto>> ListAddresses(Guid siteId, Guid? subnetId, AppDbContext db, CancellationToken ct)
    {
        var query = db.IpAddresses.Where(a => a.SiteId == siteId);
        if (subnetId is not null)
        {
            query = query.Where(a => a.SubnetId == subnetId);
        }

        var rows = await query
            .Select(a => new
            {
                a.Id, a.Address, a.PrefixLength, a.SubnetId, SubnetName = a.Subnet != null ? a.Subnet.Name : null, a.HostId,
                HostName = a.Host != null ? a.Host.Name : null, a.InterfaceName, a.MacAddress, a.Hostname, a.Source, a.Description, a.LastSeenAt,
            })
            .ToListAsync(ct);

        // Konflikt: dieselbe Adresse bei zwei Hosts, zwei MACs (laut OPNsense) oder manuell reserviert und trotzdem belegt.
        // OPNsense-ARP zu einem Host mit Agent ist kein Konflikt (Agents melden keine MAC, das ist dasselbe Gerät).
        var conflicts = rows.GroupBy(r => r.Address)
            .Where(g =>
            {
                var hosts = g.Where(r => r.HostId != null).Select(r => r.HostId).Distinct().Count();
                var macs = g.Where(r => r.MacAddress != null).Select(r => r.MacAddress!.ToLowerInvariant()).Distinct().Count();
                var manual = g.Count(r => r.Source == IpSource.Manual);
                return hosts > 1 || macs > 1 || (manual > 0 && (hosts > 0 || manual > 1));
            })
            .Select(g => g.Key)
            .ToHashSet();

        return rows
            .Select(r => new AddressDto(r.Id, r.Address, r.PrefixLength, r.SubnetId, r.SubnetName, r.HostId, r.HostName, r.InterfaceName, r.MacAddress,
                r.Hostname, r.Source, r.Description, r.LastSeenAt, conflicts.Contains(r.Address)))
            .OrderBy(r => SortKey(r.Address))
            .ThenBy(r => r.Source)
            .ToList();
    }

    private static async Task<IResult> CreateAddress(AddressRequest request, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        if (!IPAddress.TryParse(request.Address?.Trim(), out var ip))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["address"] = [L.T("Ungültige IP-Adresse.", "Invalid IP address.")] });
        }

        if (!await db.Sites.AnyAsync(s => s.Id == request.SiteId, ct))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["siteId"] = [L.T("Site existiert nicht.", "Site does not exist.")] });
        }

        var entry = new IpAddressEntry
        {
            SiteId = request.SiteId,
            Address = ip.ToString(),
            Source = IpSource.Manual,
            Hostname = Trim(request.Hostname, 200),
            Description = Trim(request.Description),
            SubnetId = await SubnetForAsync(db, request.SiteId, ip, ct),
            LastSeenAt = DateTime.UtcNow,
        };
        db.IpAddresses.Add(entry);
        audit.Add("ipam.address.created", $"{entry.Address} {entry.Hostname}", siteId: entry.SiteId);
        await db.SaveChangesAsync(ct);
        return Results.Ok(entry.Id);
    }

    private static async Task<IResult> DeleteAddress(long id, AppDbContext db, AuditLog audit, CancellationToken ct)
    {
        var entry = await db.IpAddresses.FindAsync([id], ct);
        if (entry is null)
        {
            return Results.NotFound();
        }

        if (entry.Source != IpSource.Manual)
        {
            return Results.Problem(L.T("Nur manuelle Einträge lassen sich löschen; die anderen kommen von Agent bzw. OPNsense.",
                "Only manual entries can be deleted; the others come from the agent or OPNsense."), statusCode: StatusCodes.Status409Conflict);
        }

        db.IpAddresses.Remove(entry);
        audit.Add("ipam.address.deleted", entry.Address, siteId: entry.SiteId);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // --- Hilfen ---------------------------------------------------------------------------------------

    /// <summary>Nach Änderung der Subnetze: Adressen der Site neu zuordnen (kleinstes passendes Netz gewinnt).</summary>
    public static async Task AssignSubnetsAsync(AppDbContext db, Guid siteId, CancellationToken ct)
    {
        var subnets = await LoadNetworksAsync(db, siteId, ct);
        foreach (var address in await db.IpAddresses.Where(a => a.SiteId == siteId).ToListAsync(ct))
        {
            address.SubnetId = IPAddress.TryParse(address.Address, out var ip) ? Match(subnets, ip) : null;
        }
    }

    public static async Task<Guid?> SubnetForAsync(AppDbContext db, Guid siteId, IPAddress ip, CancellationToken ct) =>
        Match(await LoadNetworksAsync(db, siteId, ct), ip);

    private static async Task<List<(Guid Id, IPNetwork Network)>> LoadNetworksAsync(AppDbContext db, Guid siteId, CancellationToken ct) =>
        (await db.Subnets.Where(s => s.SiteId == siteId).ToListAsync(ct))
        .Select(s => (s.Id, Ok: IPNetwork.TryParse(s.Cidr, out var n), Network: n))
        .Where(s => s.Ok)
        .Select(s => (s.Id, s.Network))
        .ToList();

    private static Guid? Match(List<(Guid Id, IPNetwork Network)> subnets, IPAddress ip) =>
        subnets.Where(s => s.Network.Contains(ip)).OrderByDescending(s => s.Network.PrefixLength).Select(s => (Guid?)s.Id).FirstOrDefault();

    public static string? Normalize(string? cidr) =>
        IPNetwork.TryParse(cidr?.Trim(), out var network) ? network.ToString() : null;

    private static long? UsableSize(string cidr)
    {
        if (!IPNetwork.TryParse(cidr, out var network))
        {
            return null;
        }

        var bits = (network.BaseAddress.AddressFamily == AddressFamily.InterNetwork ? 32 : 128) - network.PrefixLength;
        return bits > 32 ? null : Math.Max((1L << bits) - 2, 1);
    }

    private static string SortKey(string addressOrCidr)
    {
        var text = addressOrCidr.Split('/')[0];
        return IPAddress.TryParse(text, out var ip) ? Convert.ToHexString(ip.MapToIPv6().GetAddressBytes()) : text;
    }

    private static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    private static IPAddress FromUInt(uint value) => new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

    private static string? Trim(string? value, int max = 500)
    {
        var t = value?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > max ? t[..max] : t;
    }
}

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;

namespace LinkTest.Core;

/// <summary>What is known about an IP address: scope, reverse DNS and (for public addresses) ISP / location.</summary>
public sealed record IpInfo(
    string Ip,
    string Scope,
    string? Hostname = null,
    string? Org = null,
    string? City = null,
    string? Region = null,
    string? Country = null,
    string? Timezone = null,
    string? Error = null)
{
    public bool IsPublic => Scope == IpLookup.PublicScope;

    public string Location => string.Join(", ", new[] { City, Region, Country }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>One-line description, e.g. "203.0.113.5 (host.example.net) - AS64500 Example ISP - Toronto, Ontario, CA".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { Hostname != null && Hostname != Ip ? $"{Ip} ({Hostname})" : Ip };
            if (!IsPublic) parts.Add(Scope);
            if (!string.IsNullOrEmpty(Org)) parts.Add(Org);
            if (Location.Length > 0) parts.Add(Location);
            if (Error != null) parts.Add("lookup failed: " + Error);
            return string.Join(" - ", parts);
        }
    }
}

/// <summary>
/// IP address lookups: local classification (private / CGNAT / loopback …), reverse DNS, and an optional
/// online query to ipinfo.io for ISP (ASN) and approximate location of public addresses.
/// Only public addresses are ever sent to the online service. Results are cached for an hour.
/// </summary>
public static class IpLookup
{
    public const string PublicScope = "Public";
    public const string Service = "ipinfo.io";

    static readonly ConcurrentDictionary<string, (IpInfo info, DateTime at)> Cache = new();
    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LinkTest", Updater.CurrentVersion.ToString()));
        h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return h;
    }

    public static string Classify(IPAddress a)
    {
        a = Protocol.Normalize(a);
        if (IPAddress.IsLoopback(a)) return "Loopback";
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            if (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)) return "Private (RFC 1918)";
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return "Carrier-grade NAT (RFC 6598)";
            if (b[0] == 169 && b[1] == 254) return "Link-local";
            if (b[0] == 0 || b[0] >= 224) return "Reserved / multicast";
            return PublicScope;
        }
        if (a.IsIPv6LinkLocal) return "Link-local";
        if (a.IsIPv6UniqueLocal) return "Unique local (private IPv6)";
        if (a.IsIPv6Multicast) return "Reserved / multicast";
        return PublicScope;
    }

    /// <summary>Looks up <paramref name="ip"/>, or this machine's own public address when null.</summary>
    /// <param name="online">Query ipinfo.io for ISP / location (public addresses only).</param>
    public static async Task<IpInfo> LookupAsync(IPAddress? ip, bool online, CancellationToken ct = default)
    {
        if (ip == null)
        {
            if (!online) return new IpInfo("?", "Unknown", Error: "online lookup disabled");
            return await QueryAsync(null, ct) ?? new IpInfo("?", "Unknown", Error: "no response from " + Service);
        }

        ip = Protocol.Normalize(ip);
        string key = ip.ToString();
        if (Cache.TryGetValue(key, out var c) && DateTime.UtcNow - c.at < TimeSpan.FromHours(1)) return c.info;

        string scope = Classify(ip);
        IpInfo info;
        if (scope == PublicScope && online)
        {
            info = await QueryAsync(ip, ct) ?? new IpInfo(key, scope, Error: "no response from " + Service);
            if (info.Hostname == null) info = info with { Hostname = await ReverseDnsAsync(ip, ct) };
        }
        else
        {
            info = new IpInfo(key, scope, Hostname: await ReverseDnsAsync(ip, ct));
        }
        if (info.Error == null) Cache[key] = (info, DateTime.UtcNow);
        return info;
    }

    static async Task<IpInfo?> QueryAsync(IPAddress? ip, CancellationToken ct)
    {
        try
        {
            string url = ip == null ? $"https://{Service}/json" : $"https://{Service}/{ip}/json";
            using var resp = await Http.GetAsync(url, ct);
            if ((int)resp.StatusCode == 429) return new IpInfo(ip?.ToString() ?? "?", ip == null ? "Unknown" : Classify(ip), Error: "rate limited by " + Service);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var r = doc.RootElement;
            string? S(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Length > 0 ? v.GetString() : null;
            string addr = S("ip") ?? ip?.ToString() ?? "?";
            string scope = IPAddress.TryParse(addr, out var parsed) ? Classify(parsed) : PublicScope;
            var info = new IpInfo(addr, scope, S("hostname"), S("org"), S("city"), S("region"), S("country"), S("timezone"));
            if (ip == null && info.Hostname == null && parsed != null) info = info with { Hostname = await ReverseDnsAsync(parsed, ct) };
            return info;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new IpInfo(ip?.ToString() ?? "?", ip == null ? "Unknown" : Classify(ip), Error: Service + " timed out");
        }
        catch (Exception e) when (e is HttpRequestException or JsonException)
        {
            return new IpInfo(ip?.ToString() ?? "?", ip == null ? "Unknown" : Classify(ip), Error: e.Message);
        }
    }

    public static async Task<string?> ReverseDnsAsync(IPAddress ip, CancellationToken ct)
    {
        try
        {
            using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
            t.CancelAfter(TimeSpan.FromSeconds(3));
            var e = await Dns.GetHostEntryAsync(ip.ToString(), t.Token);
            return string.IsNullOrEmpty(e.HostName) || e.HostName == ip.ToString() ? null : e.HostName;
        }
        catch { return null; }
    }
}

/// <summary>How the client reaches the server, as seen from both ends, plus the client's public-IP details.</summary>
public sealed record ConnectionInfo(
    string Server,
    string LocalAddress,
    string? SeenByServer,
    string NatStatus,
    IpInfo? SeenByServerInfo,
    IpInfo? PublicInfo,
    bool LookupComplete);

using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace LinkTest.Core;

/// <summary>
/// TLS for the control connection (key, settings, results). The server uses a self-signed certificate created once
/// and kept; clients remember each server's certificate fingerprint on first use (like SSH) and warn if it changes.
/// Test traffic itself is not encrypted, so encryption doesn't distort the measurements.
/// </summary>
public static class Tls
{
    public const SslProtocols Protocols = SslProtocols.Tls12 | SslProtocols.Tls13;

    /// <summary>SHA-256 fingerprint as colon-separated hex (AB:CD:…).</summary>
    public static string Fingerprint(X509Certificate cert) =>
        string.Join(":", SHA256.HashData(cert.GetRawCertData()).Select(b => b.ToString("X2")));

    /// <summary>Short form for display (first and last 4 bytes).</summary>
    public static string ShortFingerprint(string fp)
    {
        var p = fp.Split(':');
        return p.Length <= 8 ? fp : string.Join(":", p.Take(4)) + "…" + string.Join(":", p.TakeLast(4));
    }

    /// <summary>Loads the server certificate from <paramref name="pfxPath"/>, creating it (ECDSA P-256, 20 years) the first time.</summary>
    public static X509Certificate2 LoadOrCreateServerCertificate(string pfxPath)
    {
        if (File.Exists(pfxPath))
        {
            try { return X509CertificateLoader.LoadPkcs12FromFile(pfxPath, null, X509KeyStorageFlags.Exportable); }
            catch { /* unreadable: make a new one */ }
        }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=LinkTest server", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        byte[] pfx = cert.Export(X509ContentType.Pkcs12);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pfxPath))!);
        File.WriteAllBytes(pfxPath, pfx);
        return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>Default place for the server certificate (per user).</summary>
    public static string DefaultServerCertPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            "LinkTest", "server-cert.pfx");

    public static async Task<SslStream> ServerHandshakeAsync(Stream inner, X509Certificate2 cert, CancellationToken ct)
    {
        var ssl = new SslStream(inner, false);
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = cert, EnabledSslProtocols = Protocols, ClientCertificateRequired = false,
            }, ct);
            return ssl;
        }
        catch { await ssl.DisposeAsync(); throw; }
    }

    /// <summary>Client handshake; the certificate is checked against the remembered fingerprint afterwards, not against a CA.</summary>
    public static async Task<(SslStream ssl, string fingerprint)> ClientHandshakeAsync(Stream inner, string host, CancellationToken ct)
    {
        var ssl = new SslStream(inner, false);
        string? fp = null;
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host, EnabledSslProtocols = Protocols,
                RemoteCertificateValidationCallback = (_, cert, _, _) => { if (cert != null) fp = Fingerprint(cert); return cert != null; },
            }, ct);
            return (ssl, fp ?? throw new AuthenticationException("The server sent no certificate."));
        }
        catch { await ssl.DisposeAsync(); throw; }
    }

    public static string Describe(SslStream ssl) => ssl.SslProtocol switch
    {
        SslProtocols.Tls13 => "TLS 1.3", SslProtocols.Tls12 => "TLS 1.2", var p => p.ToString(),
    };
}

/// <summary>The server's certificate differs from the one remembered for it.</summary>
public sealed class ServerCertificateChangedException : Exception
{
    public ServerCertificateChangedException(string server, string remembered, string presented)
        : base($"The certificate of {server} has changed since the last test.\n\n" +
               $"Remembered: {Tls.ShortFingerprint(remembered)}\nNow:        {Tls.ShortFingerprint(presented)}\n\n" +
               "This is expected if the server was reinstalled or moved to another computer. Otherwise someone may be intercepting the connection.")
    {
        Server = server; Remembered = remembered; Presented = presented;
    }
    public string Server { get; }
    public string Remembered { get; }
    public string Presented { get; }
}

/// <summary>Server certificate fingerprints trusted on first use, per "host:port" (a small JSON file).</summary>
public sealed class KnownServers
{
    readonly string _path;
    readonly object _lock = new();
    public KnownServers(string path) => _path = path;

    /// <summary>Shared by the desktop client and the CLI.</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
            "LinkTest", "known-servers.json");

    Dictionary<string, string> Load()
    {
        try { if (File.Exists(_path)) return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? new(); }
        catch { }
        return new();
    }

    public string? Get(string server) { lock (_lock) return Load().GetValueOrDefault(server.ToLowerInvariant()); }

    public void Trust(string server, string fingerprint)
    {
        lock (_lock)
        {
            var d = Load();
            d[server.ToLowerInvariant()] = fingerprint;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                File.WriteAllText(_path, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}

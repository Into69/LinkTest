using System.Text.Json;
using LinkTest.Core;
using LinkTest.Core.Tunnels;

namespace LinkTest.Client;

/// <summary>Last-used settings, persisted to %APPDATA%\LinkTest\client-settings.json.</summary>
sealed class ClientSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = Protocol.DefaultPort;
    public string Key { get; set; } = "";
    public TestConfig Config { get; set; } = new();
    public double ArtificialLossPct { get; set; }
    public bool CsvEnabled { get; set; }
    public string CsvFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LinkTest");
    public List<TunnelProfile> Tunnels { get; set; } = TunnelPresets.CreateDefaults();
    public bool CheckForUpdates { get; set; } = true;
    public bool IpLookup { get; set; } = true;
    /// <summary>Report this PC's location with the test data and to the server. Off by default.</summary>
    public bool ShareLocation { get; set; }
    /// <summary>"auto", "4", "6" or "both" (IPv4 then IPv6 with a comparison).</summary>
    public string Family { get; set; } = "auto";
    public bool RequireTls { get; set; }
    public bool CapacityTest { get; set; }
    public double CapacityPct { get; set; } = 80;
    public bool MeasureMtu { get; set; } = true;
    public bool AutoTraceroute { get; set; } = true;
    public string ResultsFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LinkTest", "Results");

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LinkTest", "client-settings.json");

    public static ClientSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(FilePath), Json.Options);
                if (s != null)
                {
                    s.Config ??= new();
                    if (s.Tunnels == null || s.Tunnels.Count == 0) s.Tunnels = TunnelPresets.CreateDefaults();
                    return s;
                }
            }
        }
        catch { /* fall back to defaults */ }
        return new ClientSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json.Pretty));
        }
        catch { }
    }
}

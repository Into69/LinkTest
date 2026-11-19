using System.Text.Json;
using LinkTest.Core;
using LinkTest.Server;

namespace LinkTest.ServerGui;

/// <summary>Persisted to %APPDATA%\LinkTest\server-settings.json.</summary>
sealed class ServerSettings
{
    public int Port { get; set; } = Protocol.DefaultPort;
    /// <summary>Empty = all interfaces.</summary>
    public string Bind { get; set; } = "";
    public string Key { get; set; } = "";
    public double MaxMbps { get; set; } = 1000;
    public int MaxSessions { get; set; } = 8;
    public bool StartOnLaunch { get; set; }
    public bool LookupClients { get; set; } = true;
    public bool RequireTls { get; set; }
    public bool CheckForUpdates { get; set; } = true;
    public bool AutoInstallUpdates { get; set; }
    /// <summary>Browser tests: web page + WebRTC on <see cref="WebPort"/> (TCP + UDP).</summary>
    public bool WebEnabled { get; set; }
    public int WebPort { get; set; } = ServerOptions.DefaultWebPort;
    /// <summary>Also serve the page over HTTPS on <see cref="WebPort"/> + 1.</summary>
    public bool WebHttps { get; set; }

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LinkTest", "server-settings.json");

    public static ServerSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(FilePath), Json.Options) ?? new();
        }
        catch { }
        return new();
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

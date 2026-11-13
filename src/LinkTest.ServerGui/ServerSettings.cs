using System.Text.Json;
using LinkTest.Core;

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
    public bool CheckForUpdates { get; set; } = true;
    public bool AutoInstallUpdates { get; set; }

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

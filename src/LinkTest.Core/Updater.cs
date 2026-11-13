using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace LinkTest.Core;

/// <summary>Identifies which release asset belongs to which program.</summary>
public sealed record UpdateChannel(string AssetName, string ExeName)
{
    public static readonly UpdateChannel Client = new("LinkTest-Client-win-x64.zip", "LinkTestClient.exe");
    public static readonly UpdateChannel Server = new("LinkTest-Server-win-x64.zip", "LinkTestServer.exe");
    public static readonly UpdateChannel Cli = new("LinkTest-Cli-win-x64.zip", "LinkTestCli.exe");
    public static readonly UpdateChannel ServerGui = new("LinkTest-ServerGui-win-x64.zip", "LinkTestServerGui.exe");
}

public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, long Size, string? Sha256, string Notes, string PageUrl);

/// <summary>
/// Self-update from GitHub Releases. Releases are produced by .github/workflows/release.yml when a
/// "vX.Y.Z" tag is pushed; each program downloads its own zip, verifies the SHA-256 digest GitHub
/// publishes for the asset, swaps its files in place and restarts.
/// </summary>
public static class Updater
{
    /// <summary>owner/repo; can be overridden with the LINKTEST_UPDATE_REPO environment variable (forks/testing).</summary>
    public static string Repository => Environment.GetEnvironmentVariable("LINKTEST_UPDATE_REPO") is { Length: > 0 } r ? r : "Into69/LinkTest";
    public static string ProjectUrl => $"https://github.com/{Repository}";

    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    static HttpClient CreateHttp(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LinkTest-Updater", CurrentVersion.ToString()));
        return http;
    }

    /// <summary>Returns the latest release if it is newer than this program, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(UpdateChannel channel, CancellationToken ct = default)
    {
        using var http = CreateHttp(TimeSpan.FromSeconds(20));
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;   // no releases published yet
        if (resp.StatusCode == HttpStatusCode.Forbidden || (int)resp.StatusCode == 429)
            throw new InvalidOperationException("GitHub API rate limit reached; try again later.");
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseVersion(tag, out var version) || version <= CurrentVersion) return null;

        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(a.GetProperty("name").GetString(), channel.AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            string url = a.GetProperty("browser_download_url").GetString() ?? "";
            if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) return null;
            string? sha = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String && d.GetString()!.StartsWith("sha256:")
                ? d.GetString()!["sha256:".Length..] : null;
            return new UpdateInfo(version, tag, url, a.GetProperty("size").GetInt64(), sha,
                root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ProjectUrl : ProjectUrl);
        }
        return null;   // release exists but has no package for this program
    }

    public static bool TryParseVersion(string tag, out Version version)
    {
        var s = tag.Trim().TrimStart('v', 'V');
        int dash = s.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) s = s[..dash];
        if (Version.TryParse(s, out var v))
        {
            version = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    /// <summary>Downloads the update package to a temp file and verifies its SHA-256 digest.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var http = CreateHttp(Timeout.InfiniteTimeSpan);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        using var resp = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? info.Size;

        string path = Path.Combine(Path.GetTempPath(), $"LinkTest-update-{info.Version}-{Guid.NewGuid():N}.zip");
        using (var sha = SHA256.Create())
        await using (var src = await resp.Content.ReadAsStreamAsync(timeout.Token))
        await using (var dst = File.Create(path))
        {
            var buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, timeout.Token)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), timeout.Token);
                sha.TransformBlock(buf, 0, n, null, 0);
                done += n;
                if (total > 0) progress?.Report(Math.Min(1.0, (double)done / total));
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            string actual = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            if (info.Sha256 != null && !string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                dst.Close();
                TryDelete(path);
                throw new InvalidDataException("Downloaded update failed its SHA-256 check; not installed.");
            }
        }
        return path;
    }

    /// <summary>Whether this process can replace its own files (installed exe, writable folder).</summary>
    public static bool CanSelfUpdate(UpdateChannel channel, out string reason)
    {
        string? exe = Environment.ProcessPath;
        if (exe == null || !string.Equals(Path.GetFileName(exe), channel.ExeName, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"not running as {channel.ExeName} (e.g. started through 'dotnet')";
            return false;
        }
        try
        {
            string probe = Path.Combine(AppContext.BaseDirectory, $".update-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            reason = $"no write permission to {AppContext.BaseDirectory} - run as Administrator or move the program to a writable folder";
            return false;
        }
        reason = "";
        return true;
    }

    /// <summary>
    /// Installs the package over the program's folder. Files in use are renamed to *.old (Windows allows
    /// renaming running executables) and removed by <see cref="CleanupOldFiles"/> on the next start.
    /// Rolls back on failure. The caller should then <see cref="Restart"/> and exit.
    /// </summary>
    public static void Apply(string zipPath, UpdateChannel channel)
    {
        string appDir = AppContext.BaseDirectory;
        string stagingRoot = Path.Combine(Path.GetTempPath(), $"LinkTest-staging-{Guid.NewGuid():N}");
        var moved = new List<(string target, string backup)>();
        var added = new List<string>();
        try
        {
            ZipFile.ExtractToDirectory(zipPath, stagingRoot);
            string staging = stagingRoot;
            if (!File.Exists(Path.Combine(staging, channel.ExeName)))
            {
                var dirs = Directory.GetDirectories(staging);
                if (dirs.Length == 1 && File.Exists(Path.Combine(dirs[0], channel.ExeName))) staging = dirs[0];
                else throw new InvalidDataException($"Update package does not contain {channel.ExeName}");
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
                {
                    string target = Path.Combine(appDir, Path.GetRelativePath(staging, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (File.Exists(target))
                    {
                        string backup = target + ".old";
                        if (File.Exists(backup) && !TryDelete(backup)) backup = $"{target}.{Guid.NewGuid():N}.old";
                        File.Move(target, backup);
                        moved.Add((target, backup));
                    }
                    else added.Add(target);
                    File.Copy(file, target);
                }
            }
            catch
            {
                foreach (var a in added) TryDelete(a);
                foreach (var (target, backup) in moved)
                {
                    TryDelete(target);
                    try { File.Move(backup, target); } catch { }
                }
                throw;
            }
        }
        finally
        {
            try { Directory.Delete(stagingRoot, true); } catch { }
            TryDelete(zipPath);
        }
    }

    /// <summary>Starts a new instance of this program with the given arguments.</summary>
    public static void Restart(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }

    /// <summary>
    /// Removes *.old files left behind by a previous update. Call at startup; a second pass runs a few
    /// seconds later because the previous instance may still be exiting right after a restart.
    /// </summary>
    public static void CleanupOldFiles()
    {
        DeleteOldFiles();
        _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(15)); DeleteOldFiles(); });
    }

    static void DeleteOldFiles()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.old", SearchOption.AllDirectories))
                TryDelete(f);
        }
        catch { }
    }

    static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; } catch { return false; }
    }
}

using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace WarpscoutGui;

public static class CoreService
{
    private const string DefaultAwgI1 = "<r 2><b 0x858000010001000000000669636c6f756403636f6d0000010001c00c000100010000105a00044d583737>";
    private const string GithubLatestApi = "https://api.github.com/repos/HattabbI4/warpscout/releases/latest";

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("warpscout-gui/1.0");
        return c;
    }

    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WarpscoutGui");

    public static string DefaultDownloadedCorePath =>
        Path.Combine(AppDataDir, "bin", "warpscout.exe");

    public static string SettingsPath => Path.Combine(AppDataDir, "settings.json");

    public static AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                if (s != null) return s;
            }
        }
        catch { }
        return new AppSettings();
    }

    public static void SaveSettings(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            var json = JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }

    public static string ResolveCorePath(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.CorePath) && File.Exists(settings.CorePath))
            return Path.GetFullPath(settings.CorePath);

        string[] candidates =
        [
            DefaultDownloadedCorePath,
            Path.Combine(AppContext.BaseDirectory, "warpscout.exe"),
        ];

        foreach (var c in candidates)
        {
            try
            {
                if (File.Exists(c)) return Path.GetFullPath(c);
            }
            catch { }
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.Combine(dir.Trim(), "warpscout.exe");
            if (File.Exists(full)) return full;
        }

        return "";
    }

    public static async Task<string> GetCoreVersionAsync(string corePath)
    {
        if (string.IsNullOrWhiteSpace(corePath) || !File.Exists(corePath)) return "";
        try
        {
            var psi = new ProcessStartInfo(corePath, "version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            var outTask = p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (await outTask).Trim();
        }
        catch
        {
            return "";
        }
    }

    public static async Task<(string Path, string Version, bool Updated)> DownloadOrUpdateCoreAsync(
        string targetPath,
        string? currentCorePath,
        Action? onBeforeReplace,
        IProgress<(double percent, string status)> progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            targetPath = DefaultDownloadedCorePath;

        var s = Strings.Instance;
        progress.Report((5, s.SearchingGithubRelease));
        using var relResp = await Http.GetAsync(GithubLatestApi, ct);
        if (!relResp.IsSuccessStatusCode)
            throw new InvalidOperationException(s.GithubApiError((int)relResp.StatusCode, relResp.ReasonPhrase));

        using var doc = await JsonDocument.ParseAsync(await relResp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = doc.RootElement;
        var tagName = root.TryGetProperty("tag_name", out var tagEl) ? (tagEl.GetString() ?? "latest") : "latest";

        var checkPath = !string.IsNullOrWhiteSpace(currentCorePath) && File.Exists(currentCorePath)
            ? currentCorePath
            : targetPath;
        if (File.Exists(checkPath))
        {
            var installedVer = await GetCoreVersionAsync(checkPath);
            var normInstalled = installedVer.Trim().TrimStart('v', 'V');
            var normTag = tagName.Trim().TrimStart('v', 'V');
            if (!string.IsNullOrEmpty(normInstalled) &&
                string.Equals(normInstalled, normTag, StringComparison.OrdinalIgnoreCase))
            {
                progress.Report((100, s.CoreUpToDateStatus(installedVer)));
                return (checkPath, installedVer, false);
            }
        }

        var archSuffix = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "windows_arm64.zip" : "windows_amd64.zip";
        string? dlUrl = null;
        string? assetName = null;

        if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsEl.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                var url = asset.TryGetProperty("browser_download_url", out var uEl) ? uEl.GetString() : null;
                if (string.IsNullOrEmpty(url)) continue;

                if (name.Contains(archSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    assetName = name;
                    dlUrl = url;
                    break;
                }
                if (dlUrl == null && name.Contains("windows_amd64.zip", StringComparison.OrdinalIgnoreCase))
                {
                    assetName = name;
                    dlUrl = url;
                }
            }
        }

        if (string.IsNullOrEmpty(dlUrl))
            throw new InvalidOperationException(s.ArchiveNotFound(archSuffix, tagName));

        progress.Report((12, s.DownloadingAsset(assetName ?? tagName)));
        using var dlResp = await Http.GetAsync(dlUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!dlResp.IsSuccessStatusCode)
            throw new InvalidOperationException(s.DownloadHttpError((int)dlResp.StatusCode));

        var totalBytes = dlResp.Content.Headers.ContentLength ?? -1L;
        await using var netStream = await dlResp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[65536];
        long readTotal = 0;
        int n;
        while ((n = await netStream.ReadAsync(buf, ct)) > 0)
        {
            await ms.WriteAsync(buf.AsMemory(0, n), ct);
            readTotal += n;
            if (totalBytes > 0)
            {
                var pct = 12.0 + 78.0 * readTotal / totalBytes;
                progress.Report((pct, s.DownloadProgressWithTotal(tagName, readTotal, totalBytes)));
            }
            else
            {
                progress.Report((50, s.DownloadProgressNoTotal(tagName, readTotal)));
            }
        }

        progress.Report((92, s.ExtractingCore));
        ms.Position = 0;
        var destDir = Path.GetDirectoryName(targetPath) ?? Path.Combine(AppDataDir, "bin");
        Directory.CreateDirectory(destDir);
        var tmpExe = targetPath + ".tmp";
        try
        {
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                var entry = archive.Entries.FirstOrDefault(e =>
                    e.Name.Equals("warpscout.exe", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException(s.CoreNotFoundInArchive);

                entry.ExtractToFile(tmpExe, overwrite: true);
            }

            onBeforeReplace?.Invoke();
            File.Move(tmpExe, targetPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmpExe)) File.Delete(tmpExe); } catch { }
        }
        progress.Report((100, s.CoreInstalledStatus(tagName)));

        var ver = await GetCoreVersionAsync(targetPath);
        return (targetPath, string.IsNullOrEmpty(ver) ? tagName : ver, true);
    }

    public static string ResolveAccountPath() => Path.Combine(AppDataDir, "warpscout-account.json");

    public static AccountData? ReadAccount(string accPath)
    {
        try
        {
            if (!File.Exists(accPath)) return null;
            var acc = JsonSerializer.Deserialize<AccountData>(File.ReadAllText(accPath));
            if (acc == null || string.IsNullOrWhiteSpace(acc.PrivateKey) || string.IsNullOrWhiteSpace(acc.PeerPublicKey))
                return null;
            return acc;
        }
        catch
        {
            return null;
        }
    }

    public static async Task EnsureAccountAsync(string corePath, string accPath, CancellationToken ct)
    {
        if (ReadAccount(accPath) != null) return;
        await RegisterAccountAsync(corePath, accPath, ct);
    }

    public static async Task<AccountData> RegisterAccountAsync(string corePath, string accPath, CancellationToken ct)
    {
        var s = Strings.Instance;
        if (string.IsNullOrWhiteSpace(corePath) || !File.Exists(corePath))
            throw new InvalidOperationException(s.CoreNotFoundErr);

        var dir = Path.GetDirectoryName(accPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var psi = new ProcessStartInfo(corePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("register");
        psi.ArgumentList.Add("-a");
        psi.ArgumentList.Add(accPath);
        psi.ArgumentList.Add("-plain");

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException(s.RegisterStartErr);
        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var stdout = await outTask;
        var stderr = await errTask;

        if (proc.ExitCode != 0)
        {
            var errMsg = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim();
            throw new InvalidOperationException(string.IsNullOrEmpty(errMsg) ? s.RegisterExitErr(proc.ExitCode) : errMsg);
        }

        return ReadAccount(accPath) ?? throw new InvalidOperationException(s.RegisterReadErr);
    }

    public static string ExportConfig(string accPath, string endpoint, string proto)
    {
        var s = Strings.Instance;
        var acc = ReadAccount(accPath) ?? throw new InvalidOperationException(s.AccountMissingErr);

        if (proto == "masque" || proto == "masque-h2")
        {
            if (acc.Masque == null)
                throw new InvalidOperationException(s.MasqueMissingErr);

            var host = ExtractHost(endpoint);
            var isV6 = host.Contains(':');
            var obj = new Dictionary<string, object>
            {
                ["private_key"] = acc.Masque.PrivateKey,
                ["endpoint_v4"] = (!isV6 && proto == "masque") ? host : "162.159.198.2",
                ["endpoint_v6"] = (isV6 && proto == "masque") ? host : "2606:4700:103::2",
                ["endpoint_pub_key"] = acc.Masque.PeerPublicKey,
                ["id"] = acc.Masque.ID,
                ["access_token"] = acc.Masque.Token,
                ["ipv4"] = acc.Masque.IPv4,
                ["ipv6"] = acc.Masque.IPv6,
            };
            if (proto == "masque-h2")
                obj[isV6 ? "endpoint_h2_v6" : "endpoint_h2_v4"] = host;

            return JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
        }

        var addr = string.IsNullOrWhiteSpace(acc.IPv4) ? "172.16.0.2" : acc.IPv4;
        var sb = new StringBuilder();
        sb.AppendLine("[Interface]");
        sb.AppendLine($"Address = {addr}/32");
        sb.AppendLine($"PrivateKey = {acc.PrivateKey}");
        sb.AppendLine("DNS = 1.1.1.1, 1.0.0.1");
        if (proto == "awg")
        {
            sb.AppendLine("Jc = 6");
            sb.AppendLine("Jmin = 10");
            sb.AppendLine("Jmax = 50");
            sb.AppendLine($"I1 = {DefaultAwgI1}");
        }
        sb.AppendLine();
        sb.AppendLine("[Peer]");
        sb.AppendLine($"PublicKey = {acc.PeerPublicKey}");
        sb.AppendLine($"Endpoint = {endpoint}");
        sb.AppendLine("AllowedIPs = 0.0.0.0/0");
        sb.AppendLine("PersistentKeepalive = 25");
        return sb.ToString();
    }

    private static string ExtractHost(string endpoint)
    {
        if (endpoint.StartsWith('['))
        {
            var end = endpoint.IndexOf(']');
            if (end > 1) return endpoint[1..end];
        }
        var lastColon = endpoint.LastIndexOf(':');
        if (lastColon > 0 && endpoint.Count(c => c == ':') == 1)
            return endpoint[..lastColon];
        return endpoint;
    }
}

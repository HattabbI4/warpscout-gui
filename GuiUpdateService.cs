using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WarpscoutGui;

public sealed class GuiReleaseInfo
{
    public string TagName { get; set; } = "";
    public string VersionString { get; set; } = "";
    [JsonIgnore] public Version Version { get; set; } = new(0, 0, 0);
    public DateTimeOffset PublishedAt { get; set; }
    public string Body { get; set; } = "";
    public string AssetName { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public long AssetSize { get; set; }
    public string Sha256Hex { get; set; } = "";
}

public sealed class StagedGuiUpdate
{
    public string TagName { get; set; } = "";
    public string VersionString { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string Sha256Hex { get; set; } = "";
    public long AssetSize { get; set; }
}

public sealed class GuiReleasesCache
{
    public string ETag { get; set; } = "";
    public string BuildVariant { get; set; } = "";
    public List<GuiReleaseInfo> Releases { get; set; } = [];
}

public static partial class GuiUpdateService
{
    public const string GuiGithubRepo = "HattabbI4/warpscout-gui";
    internal const string ReleasesApiUrl = $"https://api.github.com/repos/{GuiGithubRepo}/releases?per_page=30";
    internal static readonly Version MinSelfUpdateVersion = new(0, 5, 0);

    private static readonly JsonSerializerOptions CacheJsonOptions = new() { WriteIndented = true };
    private static readonly HttpClient DownloadHttp = CreateDownloadHttpClient();

    private static HttpClient CreateDownloadHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("warpscout-gui/1.0");
        return c;
    }

    public static string UpdatesDir => Path.Combine(CoreService.AppDataDir, "updates");
    public static string ReleasesCachePath => Path.Combine(CoreService.AppDataDir, "gui-releases-cache.json");

    internal static bool TryParseVersion(string? raw, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var s = raw.Trim().TrimStart('v', 'V');
        var end = s.IndexOfAny(['-', '+', ' ', '\t']);
        if (end >= 0)
            s = s[..end];

        if (!Version.TryParse(s, out var parsed) || parsed == null)
            return false;

        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
        return true;
    }

    internal static Version GetCurrentVersion(Assembly? asm = null)
    {
        asm ??= Assembly.GetExecutingAssembly();
        var infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (TryParseVersion(infoVer, out var parsed))
            return parsed;

        var asmVer = asm.GetName().Version;
        if (asmVer != null)
            return new Version(asmVer.Major, asmVer.Minor, Math.Max(0, asmVer.Build));

        return new Version(0, 1, 0);
    }

    internal static string GetBuildVariant(Assembly? asm = null, string? processPath = null)
    {
        asm ??= Assembly.GetExecutingAssembly();
        var meta = asm.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, "BuildVariant", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        if (string.Equals(meta, "lite", StringComparison.OrdinalIgnoreCase))
            return "lite";
        if (string.Equals(meta, "standalone", StringComparison.OrdinalIgnoreCase))
            return "standalone";

        var exeName = Path.GetFileNameWithoutExtension(processPath ?? Environment.ProcessPath ?? "");
        return exeName.Contains("_lite", StringComparison.OrdinalIgnoreCase) ? "lite" : "standalone";
    }

    internal static bool CanInstallOrRollback(Version target, Version current) =>
        target != current && target >= MinSelfUpdateVersion;

    internal static bool IsSelfUpdateSupported(string? processPath = null)
    {
        var path = processPath ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path) ||
            !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(path))
            return false;

        var fileName = Path.GetFileName(path);
        if (string.Equals(fileName, "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return false;

        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir))
            return false;

        // Non-single-file Debug/dotnet-run builds place warpscout-gui.dll next to the apphost exe.
        return !File.Exists(Path.Combine(dir, "warpscout-gui.dll"));
    }

    [GeneratedRegex(@"^##\s+(?:Что\s+нового|What's\s+New)\s*\r?\n*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingNotesHeadingRegex();

    [GeneratedRegex(@"\s*\(\[\`?[0-9a-fA-F]{6,40}\`?\]\([^\)]+\)\)")]
    private static partial Regex CommitHashLinkRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^\)]+\)")]
    private static partial Regex MarkdownLinkRegex();

    private static string? TryExtractMarkerBlock(string body, string markerLang)
    {
        var pattern = $@"<!--\s*changelog:{Regex.Escape(markerLang)}\s*-->(.*?)<!--\s*/changelog:{Regex.Escape(markerLang)}\s*-->";
        var match = Regex.Match(body, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
            return null;

        var inner = match.Groups[1].Value.Trim();
        inner = LeadingNotesHeadingRegex().Replace(inner, "").Trim();
        return string.IsNullOrWhiteSpace(inner) ? null : inner;
    }

    internal static string ExtractChangelog(string? body, string lang)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "";

        var normalized = body.Replace("\r\n", "\n");
        var primary = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "ru";
        var secondary = primary == "en" ? "ru" : "en";

        var extracted = TryExtractMarkerBlock(normalized, primary)
            ?? TryExtractMarkerBlock(normalized, "all")
            ?? TryExtractMarkerBlock(normalized, secondary);
        if (extracted != null)
            return extracted;

        // Legacy fallback for releases created before changelog markers (v0.1.0-v0.3.0).
        var lines = normalized.Split('\n');
        int start = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("## ", StringComparison.Ordinal) &&
                trimmed[3..].Trim().Equals("Commits", StringComparison.OrdinalIgnoreCase))
            {
                start = i + 1;
                break;
            }
        }
        if (start < 0)
            return "";

        var kept = new List<string>();
        for (int i = start; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith("## ", StringComparison.Ordinal) ||
                t.StartsWith("**Full Changelog**", StringComparison.OrdinalIgnoreCase))
                break;

            var cleaned = CommitHashLinkRegex().Replace(lines[i], "");
            cleaned = MarkdownLinkRegex().Replace(cleaned, "$1").TrimEnd();
            if (!string.IsNullOrWhiteSpace(cleaned))
                kept.Add(cleaned);
        }
        return string.Join('\n', kept).Trim();
    }

    internal static List<GuiReleaseInfo> ParseReleasesJson(string json, string buildVariant)
    {
        var result = new List<GuiReleaseInfo>();
        if (string.IsNullOrWhiteSpace(json))
            return result;

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return result;

        var expectedSuffix = $"windows_amd64_{buildVariant}.exe";
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("draft", out var draftEl) && draftEl.ValueKind == JsonValueKind.True)
                continue;
            if (item.TryGetProperty("prerelease", out var preEl) && preEl.ValueKind == JsonValueKind.True)
                continue;

            var tagName = item.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
            if (!TryParseVersion(tagName, out var ver))
                continue;

            string? assetName = null;
            string? downloadUrl = null;
            long assetSize = 0;
            string sha256 = "";

            if (item.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsEl.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                    var url = asset.TryGetProperty("browser_download_url", out var uEl) ? uEl.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(url) || !name.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    assetName = name;
                    downloadUrl = url;
                    if (asset.TryGetProperty("size", out var sEl) && sEl.TryGetInt64(out var parsedSize))
                        assetSize = parsedSize;
                    if (asset.TryGetProperty("digest", out var dEl) && dEl.ValueKind == JsonValueKind.String)
                    {
                        var digest = dEl.GetString() ?? "";
                        if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                            sha256 = digest[7..].Trim().ToLowerInvariant();
                    }
                    break;
                }
            }

            if (string.IsNullOrEmpty(downloadUrl) || string.IsNullOrEmpty(assetName))
                continue;

            var publishedAt = DateTimeOffset.MinValue;
            if (item.TryGetProperty("published_at", out var pubEl) && pubEl.ValueKind == JsonValueKind.String)
                DateTimeOffset.TryParse(pubEl.GetString(), out publishedAt);
            else if (item.TryGetProperty("created_at", out var crEl) && crEl.ValueKind == JsonValueKind.String)
                DateTimeOffset.TryParse(crEl.GetString(), out publishedAt);

            result.Add(new GuiReleaseInfo
            {
                TagName = tagName,
                VersionString = ver.ToString(3),
                Version = ver,
                PublishedAt = publishedAt,
                Body = item.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? "" : "",
                AssetName = assetName,
                DownloadUrl = downloadUrl,
                AssetSize = assetSize,
                Sha256Hex = sha256,
            });
        }

        return result
            .OrderByDescending(r => r.Version)
            .ThenByDescending(r => r.PublishedAt)
            .ToList();
    }

    private static List<GuiReleaseInfo> HydrateVersions(List<GuiReleaseInfo>? list)
    {
        if (list == null) return [];
        var valid = new List<GuiReleaseInfo>(list.Count);
        foreach (var r in list)
        {
            if (r == null || !TryParseVersion(string.IsNullOrEmpty(r.VersionString) ? r.TagName : r.VersionString, out var v))
                continue;
            r.Version = v;
            valid.Add(r);
        }
        return valid.OrderByDescending(r => r.Version).ThenByDescending(r => r.PublishedAt).ToList();
    }

    internal static GuiReleasesCache? LoadCachedReleases(string buildVariant, string? cachePath = null)
    {
        var path = string.IsNullOrWhiteSpace(cachePath) ? ReleasesCachePath : cachePath;
        try
        {
            if (!File.Exists(path)) return null;
            var cache = JsonSerializer.Deserialize<GuiReleasesCache>(File.ReadAllText(path));
            if (cache == null || !string.Equals(cache.BuildVariant, buildVariant, StringComparison.OrdinalIgnoreCase))
                return null;
            cache.Releases = HydrateVersions(cache.Releases);
            return cache;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCachedReleases(GuiReleasesCache cache, string? cachePath = null)
    {
        var path = string.IsNullOrWhiteSpace(cachePath) ? ReleasesCachePath : cachePath;
        var tmp = path + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(tmp, JsonSerializer.Serialize(cache, CacheJsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    public static async Task<List<GuiReleaseInfo>> FetchReleasesAsync(
        string buildVariant,
        CancellationToken ct,
        string? cachePath = null)
    {
        var cache = LoadCachedReleases(buildVariant, cachePath);
        using var req = new HttpRequestMessage(HttpMethod.Get, ReleasesApiUrl);
        if (!string.IsNullOrWhiteSpace(cache?.ETag))
            req.Headers.TryAddWithoutValidation("If-None-Match", cache.ETag);

        using var resp = await CoreService.Http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotModified && cache != null)
            return cache.Releases;

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(Strings.Instance.GithubApiError((int)resp.StatusCode, resp.ReasonPhrase));

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var releases = ParseReleasesJson(json, buildVariant);
        var etag = resp.Headers.ETag?.Tag ?? "";
        SaveCachedReleases(new GuiReleasesCache { ETag = etag, BuildVariant = buildVariant, Releases = releases }, cachePath);
        return releases;
    }

    internal static void VerifyDownloadedExe(string filePath, long expectedSize, string? expectedSha256Hex)
    {
        var s = Strings.Instance;
        if (!File.Exists(filePath))
            throw new InvalidOperationException(s.GuiErrInvalidExe);

        var info = new FileInfo(filePath);
        if (info.Length <= 0 || (expectedSize > 0 && info.Length != expectedSize))
            throw new InvalidOperationException(s.GuiErrSizeMismatch(info.Length, expectedSize));

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> magic = stackalloc byte[2];
        if (fs.Read(magic) < 2 || magic[0] != 0x4D || magic[1] != 0x5A)
            throw new InvalidOperationException(s.GuiErrInvalidExe);

        if (!string.IsNullOrWhiteSpace(expectedSha256Hex))
        {
            fs.Position = 0;
            var actualHex = Convert.ToHexString(SHA256.HashData(fs));
            if (!string.Equals(actualHex, expectedSha256Hex.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(s.GuiErrHashMismatch);
        }
    }

    public static async Task<StagedGuiUpdate> DownloadAndStageAsync(
        GuiReleaseInfo release,
        IProgress<(double percent, long read, long total)> progress,
        CancellationToken ct,
        string? updatesDir = null,
        bool stageForExit = true)
    {
        var dir = string.IsNullOrWhiteSpace(updatesDir) ? UpdatesDir : updatesDir;
        Directory.CreateDirectory(dir);
        var safeVer = string.IsNullOrWhiteSpace(release.VersionString) ? "0.0.0" : release.VersionString;
        var partPath = Path.Combine(dir, $"warpscout-gui-{safeVer}.exe.part");
        var targetPath = Path.Combine(dir, stageForExit ? $"staged-{safeVer}.exe" : $"pending-{safeVer}.exe");
        var metaPath = Path.Combine(dir, "staged.json");

        try
        {
            using var resp = await DownloadHttp.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(Strings.Instance.GuiErrDownload((int)resp.StatusCode));

            var totalBytes = resp.Content.Headers.ContentLength ?? release.AssetSize;
            progress.Report((0, 0, totalBytes));

            var netStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (netStream.ConfigureAwait(false))
            {
                var fs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
                await using (fs.ConfigureAwait(false))
                {
                    var buf = new byte[65536];
                    long readTotal = 0;
                    long lastReported = 0;
                    int n;
                    while ((n = await netStream.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                    {
                        await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        readTotal += n;
                        if (readTotal - lastReported >= 131072 || (totalBytes > 0 && readTotal == totalBytes))
                        {
                            lastReported = readTotal;
                            var pct = totalBytes > 0 ? Math.Clamp(100.0 * readTotal / totalBytes, 0, 100) : 50;
                            progress.Report((pct, readTotal, totalBytes));
                        }
                    }
                }
            }

            VerifyDownloadedExe(partPath, release.AssetSize, release.Sha256Hex);

            if (stageForExit)
            {
                foreach (var oldStaged in Directory.EnumerateFiles(dir, "staged-*.exe"))
                {
                    if (!string.Equals(oldStaged, targetPath, StringComparison.OrdinalIgnoreCase))
                        try { File.Delete(oldStaged); } catch { }
                }
            }

            File.Move(partPath, targetPath, overwrite: true);
            var staged = new StagedGuiUpdate
            {
                TagName = release.TagName,
                VersionString = release.VersionString,
                FilePath = targetPath,
                Sha256Hex = release.Sha256Hex,
                AssetSize = release.AssetSize,
            };

            if (stageForExit)
            {
                var tmpMeta = metaPath + ".tmp";
                File.WriteAllText(tmpMeta, JsonSerializer.Serialize(staged, CacheJsonOptions));
                File.Move(tmpMeta, metaPath, overwrite: true);
            }
            progress.Report((100, release.AssetSize, release.AssetSize));
            return staged;
        }
        finally
        {
            try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
        }
    }

    internal static StagedGuiUpdate? LoadValidStagedUpdate(Version currentVersion, string? updatesDir = null)
    {
        var dir = string.IsNullOrWhiteSpace(updatesDir) ? UpdatesDir : updatesDir;
        var metaPath = Path.Combine(dir, "staged.json");
        try
        {
            if (!File.Exists(metaPath)) return null;
            var staged = JsonSerializer.Deserialize<StagedGuiUpdate>(File.ReadAllText(metaPath));
            if (staged == null || !TryParseVersion(staged.VersionString, out var ver) || ver <= currentVersion)
                return null;

            VerifyDownloadedExe(staged.FilePath, staged.AssetSize, staged.Sha256Hex);
            return staged;
        }
        catch
        {
            return null;
        }
    }

    internal static StagedGuiUpdate? CleanupUpdatesDir(Version currentVersion, string? updatesDir = null, string? currentExePath = null)
    {
        var exe = currentExePath ?? Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exe))
        {
            try
            {
                var bak = exe + ".bak";
                if (File.Exists(bak))
                    File.Delete(bak);
            }
            catch { }
        }

        var dir = string.IsNullOrWhiteSpace(updatesDir) ? UpdatesDir : updatesDir;
        if (!Directory.Exists(dir)) return null;

        foreach (var part in SafeEnumerate(dir, "*.part"))
            try { File.Delete(part); } catch { }
        foreach (var pending in SafeEnumerate(dir, "pending-*.exe"))
            try { File.Delete(pending); } catch { }
        foreach (var script in SafeEnumerate(dir, "apply-update-*.ps1"))
            try { File.Delete(script); } catch { }

        var valid = LoadValidStagedUpdate(currentVersion, dir);
        if (valid == null)
        {
            try { if (File.Exists(Path.Combine(dir, "staged.json"))) File.Delete(Path.Combine(dir, "staged.json")); } catch { }
            foreach (var exeFile in SafeEnumerate(dir, "staged-*.exe"))
                try { File.Delete(exeFile); } catch { }
        }
        else
        {
            foreach (var exeFile in SafeEnumerate(dir, "staged-*.exe"))
            {
                if (!string.Equals(exeFile, valid.FilePath, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(exeFile); } catch { }
            }
        }
        return valid;
    }

    private static IEnumerable<string> SafeEnumerate(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern).ToList(); }
        catch { return []; }
    }

    internal static bool CanWriteToDirectory(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".write-probe-{Guid.NewGuid():N}.tmp");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string PsEscape(string value) => value.Replace("'", "''");

    internal static string BuildApplyScript(
        int pidToWait,
        string currentExePath,
        string newExePath,
        bool relaunch,
        string? stagedMetaPath = null,
        bool elevated = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"$pidToWait = {pidToWait}");
        sb.AppendLine($"$current = '{PsEscape(currentExePath)}'");
        sb.AppendLine($"$new = '{PsEscape(newExePath)}'");
        sb.AppendLine("$bak = \"$current.bak\"");
        sb.AppendLine($"$stagedMeta = '{PsEscape(stagedMetaPath ?? "")}'");
        sb.AppendLine("try {");
        sb.AppendLine("    $proc = Get-Process -Id $pidToWait -ErrorAction SilentlyContinue");
        sb.AppendLine("    if ($proc) { $proc.WaitForExit(30000) | Out-Null }");
        sb.AppendLine("} catch {}");
        sb.AppendLine("$swapped = $false");
        sb.AppendLine("for ($i = 0; $i -lt 20; $i++) {");
        sb.AppendLine("    try {");
        sb.AppendLine("        if (Test-Path -LiteralPath $bak) { Remove-Item -LiteralPath $bak -Force -ErrorAction SilentlyContinue }");
        sb.AppendLine("        if (Test-Path -LiteralPath $current) { Move-Item -LiteralPath $current -Destination $bak -Force }");
        sb.AppendLine("        Move-Item -LiteralPath $new -Destination $current -Force");
        sb.AppendLine("        $swapped = $true");
        sb.AppendLine("        break");
        sb.AppendLine("    } catch {");
        sb.AppendLine("        if ((Test-Path -LiteralPath $bak) -and -not (Test-Path -LiteralPath $current)) {");
        sb.AppendLine("            try { Move-Item -LiteralPath $bak -Destination $current -Force } catch {}");
        sb.AppendLine("        }");
        sb.AppendLine("        Start-Sleep -Milliseconds 250");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine("if ($swapped) {");
        sb.AppendLine("    if (Test-Path -LiteralPath $bak) { Remove-Item -LiteralPath $bak -Force -ErrorAction SilentlyContinue }");
        sb.AppendLine("    if ($stagedMeta -and (Test-Path -LiteralPath $stagedMeta)) { Remove-Item -LiteralPath $stagedMeta -Force -ErrorAction SilentlyContinue }");
        if (relaunch)
        {
            sb.AppendLine(elevated
                ? "    Start-Process -FilePath 'explorer.exe' -ArgumentList ('\"' + $current + '\"')"
                : "    Start-Process -FilePath $current -WorkingDirectory (Split-Path -Parent $current)");
        }
        sb.AppendLine("}");
        sb.AppendLine("Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue");
        return sb.ToString();
    }

    public static bool LaunchApplyUpdate(
        string currentExePath,
        string stagedExePath,
        bool relaunch,
        bool allowElevation,
        string? updatesDir = null)
    {
        var dir = string.IsNullOrWhiteSpace(updatesDir) ? UpdatesDir : updatesDir;
        Directory.CreateDirectory(dir);
        var metaPath = Path.Combine(dir, "staged.json");
        var scriptPath = Path.Combine(dir, $"apply-update-{Guid.NewGuid():N}.ps1");
        var exeDir = Path.GetDirectoryName(currentExePath) ?? "";
        bool writable = !string.IsNullOrEmpty(exeDir) && CanWriteToDirectory(exeDir);
        if (!writable && !allowElevation)
            return false;

        var script = BuildApplyScript(Environment.ProcessId, currentExePath, stagedExePath, relaunch, metaPath, elevated: !writable);
        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var sysPs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe");
        var psExe = File.Exists(sysPs) ? sysPs : "powershell.exe";
        var args = $"-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{scriptPath}\"";

        try
        {
            var psi = new ProcessStartInfo(psExe, args)
            {
                UseShellExecute = !writable,
                CreateNoWindow = writable,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            if (!writable)
                psi.Verb = "runas";

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                try { File.Delete(scriptPath); } catch { }
                return false;
            }
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            try { File.Delete(scriptPath); } catch { }
            throw new InvalidOperationException(Strings.Instance.GuiErrUacCancelled);
        }
        catch
        {
            try { File.Delete(scriptPath); } catch { }
            throw;
        }
    }
}

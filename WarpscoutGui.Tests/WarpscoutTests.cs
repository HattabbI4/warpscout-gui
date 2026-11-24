using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using Xunit;

namespace WarpscoutGui.Tests;

public sealed class WarpscoutTests : IDisposable
{
    private readonly string _tempDir;

    public WarpscoutTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"warpscout-gui-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        Strings.Instance.SetLanguage("ru");
    }

    public void Dispose()
    {
        Strings.Instance.SetLanguage("ru");
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    private string WriteTempFile(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string WriteValidAccount(bool includeMasque = true, string ipv4 = "172.16.0.5")
    {
        var acc = new AccountData
        {
            ID = "acc-123",
            Token = "tok-xyz",
            PrivateKey = "priv-key-wg",
            PeerPublicKey = "peer-pub-wg",
            IPv4 = ipv4,
            IPv6 = "2606:4700:110::2",
            Masque = includeMasque
                ? new MasqueAccount
                {
                    ID = "masque-id",
                    Token = "masque-tok",
                    PrivateKey = "masque-priv",
                    PeerPublicKey = "masque-pub",
                    IPv4 = "172.16.0.10",
                    IPv6 = "2606:4700:110::10",
                }
                : null,
        };
        return WriteTempFile($"account-{Guid.NewGuid():N}.json", JsonSerializer.Serialize(acc));
    }

    // --- CoreService tests ---

    [Theory]
    [InlineData("162.159.192.1:2408", "162.159.192.1")]
    [InlineData("162.159.192.1", "162.159.192.1")]
    [InlineData("[2606:4700:d0::1]:2408", "2606:4700:d0::1")]
    [InlineData("2606:4700:d0::1", "2606:4700:d0::1")]
    public void ExtractHost_HandlesIpv4AndIpv6Endpoints(string endpoint, string expectedHost)
    {
        Assert.Equal(expectedHost, CoreService.ExtractHost(endpoint));
    }

    [Fact]
    public void ReadAccount_ReturnsNull_ForMissingMalformedOrIncompleteFiles()
    {
        var missingPath = Path.Combine(_tempDir, "does-not-exist.json");
        Assert.Null(CoreService.ReadAccount(missingPath));

        var malformedPath = WriteTempFile("malformed.json", "{not valid json");
        Assert.Null(CoreService.ReadAccount(malformedPath));

        var missingPrivKey = WriteTempFile("no-priv.json", """{"peer_public_key":"pub"}""");
        Assert.Null(CoreService.ReadAccount(missingPrivKey));

        var missingPeerKey = WriteTempFile("no-peer.json", """{"private_key":"priv"}""");
        Assert.Null(CoreService.ReadAccount(missingPeerKey));
    }

    [Fact]
    public void ReadAccount_ReturnsPopulatedAccount_WhenValid()
    {
        var accPath = WriteValidAccount(includeMasque: true);
        var acc = CoreService.ReadAccount(accPath);

        Assert.NotNull(acc);
        Assert.Equal("acc-123", acc.ID);
        Assert.Equal("priv-key-wg", acc.PrivateKey);
        Assert.Equal("peer-pub-wg", acc.PeerPublicKey);
        Assert.Equal("172.16.0.5", acc.IPv4);
        Assert.NotNull(acc.Masque);
        Assert.Equal("masque-id", acc.Masque.ID);
        Assert.Equal("masque-priv", acc.Masque.PrivateKey);
        Assert.Equal("masque-pub", acc.Masque.PeerPublicKey);
    }

    [Fact]
    public void ExportConfig_ThrowsWhenAccountMissing()
    {
        var missingPath = Path.Combine(_tempDir, "missing-acc.json");
        Assert.Throws<InvalidOperationException>(() =>
            CoreService.ExportConfig(missingPath, "162.159.192.1:2408", "wg"));
    }

    [Fact]
    public void ExportConfig_WireGuard_UsesFallbackIpv4AndOmitsAwgParams()
    {
        var accPath = WriteValidAccount(includeMasque: false, ipv4: "");
        var conf = CoreService.ExportConfig(accPath, "162.159.192.1:2408", "wg");

        Assert.Contains("[Interface]", conf);
        Assert.Contains("Address = 172.16.0.2/32", conf);
        Assert.Contains("PrivateKey = priv-key-wg", conf);
        Assert.Contains("DNS = 1.1.1.1, 1.0.0.1", conf);
        Assert.Contains("[Peer]", conf);
        Assert.Contains("PublicKey = peer-pub-wg", conf);
        Assert.Contains("Endpoint = 162.159.192.1:2408", conf);
        Assert.Contains("AllowedIPs = 0.0.0.0/0", conf);
        Assert.Contains("PersistentKeepalive = 25", conf);
        Assert.DoesNotContain("Jc =", conf);
        Assert.DoesNotContain("Jmin =", conf);
        Assert.DoesNotContain("Jmax =", conf);
        Assert.DoesNotContain("I1 =", conf);
    }

    [Fact]
    public void ExportConfig_AmneziaWg_IncludesAwgObfuscationParams()
    {
        var accPath = WriteValidAccount(includeMasque: false, ipv4: "172.16.0.9");
        var conf = CoreService.ExportConfig(accPath, "162.159.192.2:2408", "awg");

        Assert.Contains("Address = 172.16.0.9/32", conf);
        Assert.Contains("Jc = 6", conf);
        Assert.Contains("Jmin = 10", conf);
        Assert.Contains("Jmax = 50", conf);
        Assert.Contains("I1 = <r 2><b 0x858000010001000000000669636c6f756403636f6d0000010001c00c000100010000105a00044d583737>", conf);
    }

    [Fact]
    public void ExportConfig_Masque_ThrowsWhenMasqueMissing()
    {
        var accPath = WriteValidAccount(includeMasque: false);
        Assert.Throws<InvalidOperationException>(() =>
            CoreService.ExportConfig(accPath, "162.159.198.1:443", "masque"));
    }

    [Fact]
    public void ExportConfig_Masque_MapsIpv4AndIpv6EndpointsCorrectly()
    {
        var accPath = WriteValidAccount(includeMasque: true);

        var jsonV4 = CoreService.ExportConfig(accPath, "162.159.192.5:2408", "masque");
        using (var doc = JsonDocument.Parse(jsonV4))
        {
            var root = doc.RootElement;
            Assert.Equal("masque-priv", root.GetProperty("private_key").GetString());
            Assert.Equal("162.159.192.5", root.GetProperty("endpoint_v4").GetString());
            Assert.Equal("2606:4700:103::2", root.GetProperty("endpoint_v6").GetString());
            Assert.Equal("masque-pub", root.GetProperty("endpoint_pub_key").GetString());
            Assert.Equal("masque-id", root.GetProperty("id").GetString());
            Assert.Equal("masque-tok", root.GetProperty("access_token").GetString());
            Assert.False(root.TryGetProperty("endpoint_h2_v4", out _));
        }

        var jsonV6 = CoreService.ExportConfig(accPath, "[2606:4700:d0::1]:2408", "masque");
        using (var doc = JsonDocument.Parse(jsonV6))
        {
            var root = doc.RootElement;
            Assert.Equal("162.159.198.2", root.GetProperty("endpoint_v4").GetString());
            Assert.Equal("2606:4700:d0::1", root.GetProperty("endpoint_v6").GetString());
        }
    }

    [Fact]
    public void ExportConfig_MasqueH2_KeepsDefaultEndpointsAndSetsH2Field()
    {
        var accPath = WriteValidAccount(includeMasque: true);

        var jsonV4 = CoreService.ExportConfig(accPath, "162.159.192.5:443", "masque-h2");
        using (var doc = JsonDocument.Parse(jsonV4))
        {
            var root = doc.RootElement;
            Assert.Equal("162.159.198.2", root.GetProperty("endpoint_v4").GetString());
            Assert.Equal("2606:4700:103::2", root.GetProperty("endpoint_v6").GetString());
            Assert.Equal("162.159.192.5", root.GetProperty("endpoint_h2_v4").GetString());
            Assert.False(root.TryGetProperty("endpoint_h2_v6", out _));
        }

        var jsonV6 = CoreService.ExportConfig(accPath, "[2606:4700:d0::9]:443", "masque-h2");
        using (var doc = JsonDocument.Parse(jsonV6))
        {
            var root = doc.RootElement;
            Assert.Equal("162.159.198.2", root.GetProperty("endpoint_v4").GetString());
            Assert.Equal("2606:4700:103::2", root.GetProperty("endpoint_v6").GetString());
            Assert.Equal("2606:4700:d0::9", root.GetProperty("endpoint_h2_v6").GetString());
            Assert.False(root.TryGetProperty("endpoint_h2_v4", out _));
        }
    }

    [Fact]
    public void ResolveCorePath_ReturnsExplicitExistingPath_AndIgnoresMissingPath()
    {
        var fakeExe = WriteTempFile("warpscout-custom.exe", "stub");
        var settingsWithExisting = new AppSettings { CorePath = fakeExe };

        Assert.Equal(Path.GetFullPath(fakeExe), CoreService.ResolveCorePath(settingsWithExisting));

        var missingExe = Path.Combine(_tempDir, "non-existent-warpscout.exe");
        var resolved = CoreService.ResolveCorePath(new AppSettings { CorePath = missingExe });
        Assert.NotEqual(Path.GetFullPath(missingExe), resolved);
    }

    [Fact]
    public async Task MissingCoreBinary_GetCoreVersionReturnsEmpty_AndRegisterThrows()
    {
        var missingExe = Path.Combine(_tempDir, "missing-core.exe");
        var accPath = Path.Combine(_tempDir, "out-account.json");

        Assert.Equal("", await CoreService.GetCoreVersionAsync(""));
        Assert.Equal("", await CoreService.GetCoreVersionAsync(missingExe));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CoreService.RegisterAccountAsync("", accPath, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CoreService.RegisterAccountAsync(missingExe, accPath, CancellationToken.None));
    }

    // --- Models tests ---

    [Fact]
    public void AppSettings_DefaultsAndJsonRoundTrip()
    {
        var defaults = new AppSettings();
        Assert.Equal("", defaults.CorePath);
        Assert.Equal("", defaults.CoreRepo);
        Assert.Equal("ru", defaults.Language);
        Assert.True(defaults.ExcludeNodeEnabled);
        Assert.Equal("DME", defaults.ExcludedNode);
        Assert.False(defaults.MinimizeToTray);
        Assert.True(defaults.MinimizeToTrayWithProxy);
        Assert.False(defaults.WarnOnCloseWithProxy);
        Assert.Equal("", defaults.DefaultExitAction);
        Assert.Equal("-p awg -P -exclude-node DME", defaults.CustomScanFlags);

        var custom = new AppSettings
        {
            CorePath = @"C:\tools\warpscout.exe",
            CoreRepo = CoreService.CoreGithubRepo,
            Language = "en",
            ExcludeNodeEnabled = false,
            ExcludedNode = "FRA,AMS",
            MinimizeToTray = true,
            MinimizeToTrayWithProxy = false,
            WarnOnCloseWithProxy = true,
            DefaultExitAction = "exit",
            CustomScanFlags = "-p masque -P",
        };
        var roundTripped = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(custom));

        Assert.NotNull(roundTripped);
        Assert.Equal(custom.CorePath, roundTripped.CorePath);
        Assert.Equal(custom.CoreRepo, roundTripped.CoreRepo);
        Assert.Equal(custom.Language, roundTripped.Language);
        Assert.Equal(custom.ExcludeNodeEnabled, roundTripped.ExcludeNodeEnabled);
        Assert.Equal(custom.ExcludedNode, roundTripped.ExcludedNode);
        Assert.Equal(custom.MinimizeToTray, roundTripped.MinimizeToTray);
        Assert.Equal(custom.MinimizeToTrayWithProxy, roundTripped.MinimizeToTrayWithProxy);
        Assert.Equal(custom.WarnOnCloseWithProxy, roundTripped.WarnOnCloseWithProxy);
        Assert.Equal(custom.DefaultExitAction, roundTripped.DefaultExitAction);
        Assert.Equal(custom.CustomScanFlags, roundTripped.CustomScanFlags);

        var legacy = JsonSerializer.Deserialize<AppSettings>("""{"MinimizeToTray":false}""");
        Assert.NotNull(legacy);
        Assert.False(legacy.MinimizeToTray);
        Assert.True(legacy.MinimizeToTrayWithProxy);
    }

    [Fact]
    public void ScanStreamEvent_DeserializesNdjsonPayloads()
    {
        foreach (var phase in new[] { "ports", "tunnels", "speed" })
        {
            var progress = JsonSerializer.Deserialize<ScanStreamEvent>(
                $$"""{"event":"progress","phase":"{{phase}}","done":10,"total":100}""");
            Assert.NotNull(progress);
            Assert.Equal("progress", progress.Event);
            Assert.Equal(phase, progress.Phase);
            Assert.Equal(10, progress.Done);
            Assert.Equal(100, progress.Total);
        }

        var epEvent = JsonSerializer.Deserialize<ScanStreamEvent>(
            """{"event":"endpoint","endpoint":{"endpoint":"162.159.192.1:2408","ip":"162.159.192.1","port":2408,"seen_as":"US","node":"FRA","node_city":"Frankfurt","node_country":"DE","ping_ms":42.5,"tun_ping_ms":55.1,"loss_pct":12.5,"speed_mbps":123.4,"measured":true,"torn":false}}""");
        Assert.NotNull(epEvent);
        Assert.Equal("endpoint", epEvent.Event);
        Assert.NotNull(epEvent.Endpoint);
        Assert.Equal("162.159.192.1:2408", epEvent.Endpoint.Endpoint);
        Assert.Equal("US", epEvent.Endpoint.SeenAs);
        Assert.Equal("FRA", epEvent.Endpoint.Node);
        Assert.Equal("Frankfurt", epEvent.Endpoint.NodeCity);
        Assert.Equal("DE", epEvent.Endpoint.NodeCountry);
        Assert.Equal(42.5, epEvent.Endpoint.PingMs);
        Assert.Equal(55.1, epEvent.Endpoint.TunPingMs);
        Assert.Equal(12.5, epEvent.Endpoint.LossPct);
        Assert.Equal(123.4, epEvent.Endpoint.SpeedMbps);
        Assert.False(epEvent.Endpoint.Torn);

        var nullEpEvent = JsonSerializer.Deserialize<ScanStreamEvent>(
            """{"event":"endpoint","endpoint":{"endpoint":"162.159.192.2:2408","ip":"162.159.192.2","port":2408,"seen_as":null,"node":null,"node_city":null,"node_country":null,"ping_ms":null,"tun_ping_ms":null,"loss_pct":null,"speed_mbps":null,"measured":false,"torn":true}}""");
        Assert.NotNull(nullEpEvent?.Endpoint);
        Assert.Null(nullEpEvent.Endpoint.SeenAs);
        Assert.Null(nullEpEvent.Endpoint.Node);
        Assert.Null(nullEpEvent.Endpoint.NodeCity);
        Assert.Null(nullEpEvent.Endpoint.NodeCountry);
        Assert.Null(nullEpEvent.Endpoint.PingMs);
        Assert.Null(nullEpEvent.Endpoint.TunPingMs);
        Assert.Null(nullEpEvent.Endpoint.LossPct);
        Assert.Null(nullEpEvent.Endpoint.SpeedMbps);
        Assert.True(nullEpEvent.Endpoint.Torn);

        var done = JsonSerializer.Deserialize<ScanStreamEvent>(
            """{"event":"done","result":{"$schema":"https://raw.githubusercontent.com/vernette/warpscout/master/schema/warpscout-v1.schema.json","version":1,"proto":"awg","total":10,"working_count":1,"best":"162.159.192.1:2408","picks":[{"endpoint":"162.159.192.1:2408","ip":"162.159.192.1","port":2408,"seen_as":"US","node":"FRA","node_city":"Frankfurt","node_country":"DE","ping_ms":40,"tun_ping_ms":null,"loss_pct":null,"speed_mbps":null,"measured":false,"torn":false}],"working":[{"endpoint":"162.159.192.1:2408","ip":"162.159.192.1","port":2408,"seen_as":"US","node":"FRA","node_city":"Frankfurt","node_country":"DE","ping_ms":40,"tun_ping_ms":null,"loss_pct":null,"speed_mbps":null,"measured":false,"torn":false}],"torn":[]}}""");
        Assert.NotNull(done?.Result);
        Assert.Equal("done", done.Event);
        Assert.Equal("awg", done.Result.Proto);
        Assert.Equal(1, done.Result.WorkingCount);
        Assert.Equal("162.159.192.1:2408", done.Result.Best);
        Assert.NotNull(done.Result.Working);
        Assert.Single(done.Result.Working);
        Assert.Equal("FRA", done.Result.Working[0].Node);

        var zeroDone = JsonSerializer.Deserialize<ScanStreamEvent>(
            """{"event":"done","result":{"$schema":"https://raw.githubusercontent.com/vernette/warpscout/master/schema/warpscout-v1.schema.json","version":1,"proto":"awg","total":10,"working_count":0,"best":null,"picks":[],"working":[],"torn":[{"endpoint":"162.159.192.9:2408","ip":"162.159.192.9","port":2408,"seen_as":null,"node":null,"node_city":null,"node_country":null,"ping_ms":null,"tun_ping_ms":null,"loss_pct":100,"speed_mbps":null,"measured":true,"torn":true}]}}""");
        Assert.NotNull(zeroDone?.Result);
        Assert.Equal(0, zeroDone.Result.WorkingCount);
        Assert.Null(zeroDone.Result.Best);
        Assert.NotNull(zeroDone.Result.Working);
        Assert.Empty(zeroDone.Result.Working);
        Assert.NotNull(zeroDone.Result.Torn);
        Assert.Single(zeroDone.Result.Torn);
        Assert.True(zeroDone.Result.Torn[0].Torn);

        var err = JsonSerializer.Deserialize<ScanStreamEvent>(
            """{"event":"error","error":"network unreachable"}""");
        Assert.NotNull(err);
        Assert.Equal("error", err.Event);
        Assert.Equal("network unreachable", err.Error);
    }

    [Fact]
    public void EndpointItem_ComputedPropertiesAndFallbacks()
    {
        var prevCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Strings.Instance.SetLanguage("en");
            var empty = new EndpointItem();
            Assert.Equal(9999, empty.EffectivePingMs);
            Assert.Equal("CF", empty.CountryBadge);
            Assert.Equal("Cloudflare", empty.DisplayLocation);
            Assert.Equal("—", empty.DisplayNode);
            Assert.Equal("—", empty.DisplayPing);
            Assert.Equal("—", empty.DisplayLoss);
            Assert.Equal("—", empty.DisplaySpeed);
            Assert.Equal(Strings.Instance.RowProxyStart, empty.ProxyButtonText);

            var withLocation = new EndpointItem
            {
                Endpoint = "162.159.192.1:2408",
                Country = "NL",
                Location = "Amsterdam, NL",
                Node = "AMS",
                PingMs = 64.4,
                Loss = 0,
                SpeedMbps = 85.24,
                IsProxied = true,
            };
            Assert.Equal(64.4, withLocation.EffectivePingMs);
            Assert.Equal("NL", withLocation.CountryBadge);
            Assert.Equal("Amsterdam, NL", withLocation.DisplayLocation);
            Assert.Equal("AMS", withLocation.DisplayNode);
            Assert.Equal("64 ms", withLocation.DisplayPing);
            Assert.Equal("0%", withLocation.DisplayLoss);
            withLocation.Loss = 2.6;
            Assert.Equal("3%", withLocation.DisplayLoss);
            Assert.Equal("85.2 Mbps", withLocation.DisplaySpeed);
            Assert.Equal(Strings.Instance.RowProxyActive, withLocation.ProxyButtonText);

            withLocation.City = "Schiphol";
            withLocation.TunPingMs = 71.8;
            Assert.Equal(71.8, withLocation.EffectivePingMs);
            Assert.Equal("Schiphol", withLocation.DisplayLocation);
            Assert.Equal("72 ms", withLocation.DisplayPing);
        }
        finally
        {
            CultureInfo.CurrentCulture = prevCulture;
        }
    }

    [Theory]
    [InlineData(50, 0, 52, 211, 153)]
    [InlineData(79.9, 0, 52, 211, 153)]
    [InlineData(80, 0, 251, 191, 36)]
    [InlineData(149.9, 0, 251, 191, 36)]
    [InlineData(150, 0, 248, 113, 113)]
    [InlineData(40, 120, 251, 191, 36)]
    [InlineData(0, 0, 248, 113, 113)]
    public void EndpointItem_PingBrushMatchesLatencyThresholds(
        double pingMs, double tunPingMs, byte r, byte g, byte b)
    {
        var item = new EndpointItem { PingMs = pingMs, TunPingMs = tunPingMs };
        var brush = Assert.IsType<SolidColorBrush>(item.PingBrush);
        Assert.Equal(System.Windows.Media.Color.FromRgb(r, g, b), brush.Color);
    }

    [Fact]
    public void EndpointItem_RaisesPropertyChangedOnUpdatesAndRefreshLocale()
    {
        var item = new EndpointItem();
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.PingMs = 45;
        Assert.Contains(nameof(EndpointItem.DisplayPing), changed);
        Assert.Contains(nameof(EndpointItem.PingBrush), changed);

        changed.Clear();
        item.TunPingMs = 60;
        Assert.Contains(nameof(EndpointItem.DisplayPing), changed);
        Assert.Contains(nameof(EndpointItem.PingBrush), changed);

        changed.Clear();
        item.Loss = 1;
        Assert.Equal([nameof(EndpointItem.DisplayLoss)], changed);

        changed.Clear();
        item.SpeedMbps = 10;
        Assert.Equal([nameof(EndpointItem.DisplaySpeed)], changed);

        changed.Clear();
        item.IsBest = true;
        Assert.Equal([nameof(EndpointItem.IsBest)], changed);

        changed.Clear();
        item.IsProxied = true;
        Assert.Contains(nameof(EndpointItem.IsProxied), changed);
        Assert.Contains(nameof(EndpointItem.ProxyButtonText), changed);

        changed.Clear();
        item.RefreshLocale();
        Assert.Equal(
            [nameof(EndpointItem.DisplayPing), nameof(EndpointItem.DisplaySpeed), nameof(EndpointItem.ProxyButtonText)],
            changed);
    }

    // --- Strings tests ---

    [Fact]
    public void Strings_SetLanguageSwitchesLocaleAndRaisesPropertyChangedOnlyOnChange()
    {
        var s = Strings.Instance;
        s.SetLanguage("ru");

        var events = new List<string?>();
        void Handler(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => events.Add(e.PropertyName);
        s.PropertyChanged += Handler;
        try
        {
            s.SetLanguage("ru");
            Assert.Empty(events);

            s.SetLanguage("en");
            Assert.Single(events);
            Assert.Equal(string.Empty, events[0]);
            Assert.True(s.IsEnglish);
            Assert.Equal("Scanner", s.NavScanner);
            Assert.Equal("ms", s.UnitMs);
            Assert.Equal("Mbps", s.UnitMbps);
            Assert.Equal("🟢 Active", s.RowProxyActive);
            Assert.Equal("v1.0 · Ready", s.StatusPillReady("v1.0"));
            Assert.Equal("★ Best server: DE Frankfurt (FRA)", s.BestServerTitle("DE", "Frankfurt", "FRA"));

            s.SetLanguage("EN");
            Assert.Single(events);

            s.SetLanguage("ru");
            Assert.Equal(2, events.Count);
            Assert.False(s.IsEnglish);
            Assert.Equal("Сканер", s.NavScanner);
            Assert.Equal("мс", s.UnitMs);
            Assert.Equal("Мбит/с", s.UnitMbps);
            Assert.Equal("🟢 Активен", s.RowProxyActive);
            Assert.Equal("v1.0 · Готов", s.StatusPillReady("v1.0"));
        }
        finally
        {
            s.PropertyChanged -= Handler;
        }
    }

    // --- MainWindow CLI & filter parsing tests ---

    [Fact]
    public void ParseExcludedNodes_SplitsNormalizesAndDeduplicates()
    {
        Assert.Equal(["DME", "FRA", "AMS"], MainWindow.ParseExcludedNodes("dme, fra; ams DME"));
        Assert.Empty(MainWindow.ParseExcludedNodes(null));
        Assert.Empty(MainWindow.ParseExcludedNodes(""));
        Assert.Empty(MainWindow.ParseExcludedNodes(" , ;  "));
    }

    [Fact]
    public void SplitCliArgs_HandlesFlagsQuotesAndWhitespace()
    {
        Assert.Equal(
            ["-p", "awg", "-P", "-exclude-node", "DME"],
            MainWindow.SplitCliArgs("-p awg -P -exclude-node DME"));

        Assert.Equal(
            ["-a", @"C:\My Folder\acc.json", "-exclude-node", "DME, FRA", "-jt", "20"],
            MainWindow.SplitCliArgs("""-a "C:\My Folder\acc.json" -exclude-node 'DME, FRA' -jt 20"""));

        Assert.Empty(MainWindow.SplitCliArgs(null));
        Assert.Empty(MainWindow.SplitCliArgs(""));
        Assert.Empty(MainWindow.SplitCliArgs("   \t  "));
    }

    // --- Last scan persistence & localization tests ---

    [Fact]
    public void LastScan_SaveAndLoadRoundTrip_AndHandlesInvalidFiles()
    {
        var missingPath = Path.Combine(_tempDir, "missing-scan.json");
        Assert.Null(CoreService.LoadLastScan(missingPath));

        var malformedPath = WriteTempFile("bad-scan.json", "{invalid");
        Assert.Null(CoreService.LoadLastScan(malformedPath));

        var emptyEndpointsPath = WriteTempFile("empty-scan.json", """{"ScannedAt":"2026-11-21T10:00:00+00:00","Proto":"awg","Endpoints":[]}""");
        Assert.Null(CoreService.LoadLastScan(emptyEndpointsPath));

        var scanPath = Path.Combine(_tempDir, "nested", "last-scan.json");
        var scannedAt = new DateTimeOffset(2026, 11, 21, 14, 5, 0, TimeSpan.FromHours(5));
        var scan = new SavedScan
        {
            ScannedAt = scannedAt,
            Proto = "masque",
            BestEndpoint = "162.159.198.2:443",
            Endpoints =
            [
                new SavedEndpoint
                {
                    Endpoint = "162.159.198.1:443",
                    Node = "FRA",
                    Country = "DE",
                    City = "Frankfurt",
                    Location = "Frankfurt, DE",
                    PingMs = 38.5,
                    TunPingMs = 44.2,
                    Loss = 0.0,
                    SpeedMbps = 95.4,
                },
                new SavedEndpoint { Endpoint = "   ", Node = "BAD" },
                new SavedEndpoint
                {
                    Endpoint = "162.159.198.2:443",
                    Node = "AMS",
                    Country = "NL",
                    City = "Amsterdam",
                    Location = "Amsterdam, NL",
                    PingMs = 41.0,
                    TunPingMs = 43.0,
                    Loss = null,
                    SpeedMbps = 120.8,
                },
            ],
        };

        CoreService.SaveLastScan(scan, scanPath);

        Assert.True(File.Exists(scanPath));
        Assert.False(File.Exists(scanPath + ".tmp"));

        var loaded = CoreService.LoadLastScan(scanPath);
        Assert.NotNull(loaded);
        Assert.Equal(scannedAt, loaded.ScannedAt);
        Assert.Equal("masque", loaded.Proto);
        Assert.Equal("162.159.198.2:443", loaded.BestEndpoint);
        Assert.Equal(2, loaded.Endpoints.Count);

        var first = loaded.Endpoints[0];
        Assert.Equal("162.159.198.1:443", first.Endpoint);
        Assert.Equal("FRA", first.Node);
        Assert.Equal("DE", first.Country);
        Assert.Equal("Frankfurt", first.City);
        Assert.Equal("Frankfurt, DE", first.Location);
        Assert.Equal(38.5, first.PingMs);
        Assert.Equal(44.2, first.TunPingMs);
        Assert.Equal(0.0, first.Loss);
        Assert.Equal(95.4, first.SpeedMbps);

        Assert.Equal("162.159.198.2:443", loaded.Endpoints[1].Endpoint);
        Assert.Null(loaded.Endpoints[1].Loss);
    }

    [Fact]
    public void Strings_LastScanTime_FormatsLocalizedDateAndTime()
    {
        var localOffset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 21, 14, 5, 0));
        var otherOffset = localOffset == TimeSpan.Zero ? TimeSpan.FromHours(3) : TimeSpan.Zero;
        var timestamp = new DateTimeOffset(2026, 11, 21, 14, 5, 0, localOffset).ToOffset(otherOffset);

        Strings.Instance.SetLanguage("ru");
        Assert.Equal("Сканирование: 21.11.2026 14:05", Strings.Instance.LastScanTime(timestamp));

        Strings.Instance.SetLanguage("en");
        Assert.Equal("Scanned: 2026-11-21 14:05", Strings.Instance.LastScanTime(timestamp));
    }

    [Fact]
    public void GithubCorner_RepoUrlAndLocalizedStringsMatchRequirements()
    {
        Assert.Equal("https://github.com/HattabbI4/warpscout-gui", MainWindow.GithubRepoUrl);

        Strings.Instance.SetLanguage("ru");
        Assert.Equal("Спасибо за звезду", Strings.Instance.GithubStarText);

        Strings.Instance.SetLanguage("en");
        Assert.Equal("Star on GitHub", Strings.Instance.GithubStarText);
    }

    [Fact]
    public void ResolveCloseAction_MatchesDefaultProxyAndSettingsMatrix()
    {
        var defaults = new AppSettings();
        Assert.Equal(CloseAction.Exit, MainWindow.ResolveCloseAction(defaults, proxyActive: false));
        Assert.Equal(CloseAction.MinimizeToTray, MainWindow.ResolveCloseAction(defaults, proxyActive: true));

        var noProxyTray = new AppSettings
        {
            MinimizeToTray = false,
            MinimizeToTrayWithProxy = false,
            WarnOnCloseWithProxy = false,
        };
        Assert.Equal(CloseAction.Exit, MainWindow.ResolveCloseAction(noProxyTray, proxyActive: false));
        Assert.Equal(CloseAction.Exit, MainWindow.ResolveCloseAction(noProxyTray, proxyActive: true));

        var alwaysTray = new AppSettings
        {
            MinimizeToTray = true,
            MinimizeToTrayWithProxy = false,
            WarnOnCloseWithProxy = false,
        };
        Assert.Equal(CloseAction.MinimizeToTray, MainWindow.ResolveCloseAction(alwaysTray, proxyActive: false));
        Assert.Equal(CloseAction.MinimizeToTray, MainWindow.ResolveCloseAction(alwaysTray, proxyActive: true));

        var warnOnProxy = new AppSettings
        {
            MinimizeToTray = false,
            MinimizeToTrayWithProxy = true,
            WarnOnCloseWithProxy = true,
        };
        Assert.Equal(CloseAction.Exit, MainWindow.ResolveCloseAction(warnOnProxy, proxyActive: false));
        Assert.Equal(CloseAction.Prompt, MainWindow.ResolveCloseAction(warnOnProxy, proxyActive: true));
    }

    [Fact]
    public void Strings_TrayMinimizeAndNotificationStringsAreLocalized()
    {
        var s = Strings.Instance;

        s.SetLanguage("ru");
        var ruOption = s.TrayMinimizeOption;
        var ruTitle = s.TrayMinimizedTitle;
        var ruBody = s.TrayMinimizedBody;
        var ruProxyBody = s.TrayMinimizedProxyBody;
        Assert.False(string.IsNullOrWhiteSpace(ruOption));
        Assert.False(string.IsNullOrWhiteSpace(ruTitle));
        Assert.False(string.IsNullOrWhiteSpace(ruBody));
        Assert.False(string.IsNullOrWhiteSpace(ruProxyBody));

        s.SetLanguage("en");
        Assert.False(string.IsNullOrWhiteSpace(s.TrayMinimizeOption));
        Assert.False(string.IsNullOrWhiteSpace(s.TrayMinimizedTitle));
        Assert.False(string.IsNullOrWhiteSpace(s.TrayMinimizedBody));
        Assert.False(string.IsNullOrWhiteSpace(s.TrayMinimizedProxyBody));
        Assert.NotEqual(ruOption, s.TrayMinimizeOption);
        Assert.NotEqual(ruTitle, s.TrayMinimizedTitle);
        Assert.NotEqual(ruBody, s.TrayMinimizedBody);
        Assert.NotEqual(ruProxyBody, s.TrayMinimizedProxyBody);
    }

    [Fact]
    public void LoadSettings_MigratesLegacySettingsOnceAndPreservesPostMigrationChoices()
    {
        var settingsPath = Path.Combine(_tempDir, "legacy-settings.json");
        File.WriteAllText(settingsPath, """
            {
              "CorePath": "C:\\tools\\warpscout.exe",
              "Language": "en",
              "ExcludeNodeEnabled": true,
              "ExcludedNode": "DME",
              "MinimizeToTray": true,
              "WarnOnCloseWithProxy": true,
              "DefaultExitAction": ""
            }
            """);

        var migrated = CoreService.LoadSettings(settingsPath);
        Assert.Equal(AppSettings.CurrentSettingsVersion, migrated.SettingsVersion);
        Assert.Equal("en", migrated.Language);
        Assert.Equal(@"C:\tools\warpscout.exe", migrated.CorePath);
        Assert.False(migrated.MinimizeToTray);
        Assert.True(migrated.MinimizeToTrayWithProxy);
        Assert.False(migrated.WarnOnCloseWithProxy);

        var persisted = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath));
        Assert.NotNull(persisted);
        Assert.Equal(AppSettings.CurrentSettingsVersion, persisted.SettingsVersion);

        // Choices made after migration must be preserved across reloads.
        migrated.MinimizeToTray = true;
        CoreService.SaveSettings(migrated, settingsPath);
        var reloaded = CoreService.LoadSettings(settingsPath);
        Assert.True(reloaded.MinimizeToTray);
        Assert.Equal(AppSettings.CurrentSettingsVersion, reloaded.SettingsVersion);

        // Legacy settings where the user explicitly chose "exit" keep both tray options off.
        var exitPath = Path.Combine(_tempDir, "legacy-exit-settings.json");
        File.WriteAllText(exitPath, """
            {
              "MinimizeToTray": true,
              "WarnOnCloseWithProxy": false,
              "DefaultExitAction": "exit"
            }
            """);
        var migratedExit = CoreService.LoadSettings(exitPath);
        Assert.Equal(AppSettings.CurrentSettingsVersion, migratedExit.SettingsVersion);
        Assert.False(migratedExit.MinimizeToTray);
        Assert.False(migratedExit.MinimizeToTrayWithProxy);
        Assert.False(migratedExit.WarnOnCloseWithProxy);
    }

    [Fact]
    public void NotifyIconDataSize_MatchesWin32StructLayout()
    {
        Assert.Equal(IntPtr.Size == 8 ? 976 : 956, MainWindow.NotifyIconDataSize);
    }

    [Fact]
    public void CoreService_TargetsUpstreamRepoAndDetectsAutoUpgrade()
    {
        Assert.Equal("vernette/warpscout", CoreService.CoreGithubRepo);
        Assert.Equal("https://api.github.com/repos/vernette/warpscout/releases/latest", CoreService.GithubLatestApi);

        Assert.True(CoreService.ShouldAutoUpgradeCore("", new AppSettings()));
        Assert.True(CoreService.ShouldAutoUpgradeCore(CoreService.DefaultDownloadedCorePath, new AppSettings { CoreRepo = "" }));
        Assert.False(CoreService.ShouldAutoUpgradeCore(CoreService.DefaultDownloadedCorePath, new AppSettings { CoreRepo = "vernette/warpscout" }));
        Assert.False(CoreService.ShouldAutoUpgradeCore(@"C:\custom\warpscout.exe", new AppSettings { CoreRepo = "" }));
    }

    [Fact]
    public void EndpointItem_FromScanEndpoint_MapsFullAndNullFields()
    {
        var full = EndpointItem.FromScanEndpoint(new ScanEndpoint
        {
            Endpoint = "162.159.192.1:2408",
            SeenAs = "NL",
            Node = "AMS",
            NodeCity = "Amsterdam",
            NodeCountry = "NL",
            PingMs = 35.4,
            TunPingMs = 41.2,
            LossPct = 5.0,
            SpeedMbps = 98.7,
        }, isProxied: true);
        Assert.Equal("162.159.192.1:2408", full.Endpoint);
        Assert.Equal("AMS", full.Node);
        Assert.Equal("NL", full.Country);
        Assert.Equal("Amsterdam", full.City);
        Assert.Equal("NL", full.Location);
        Assert.Equal(35.4, full.PingMs);
        Assert.Equal(41.2, full.TunPingMs);
        Assert.Equal(5.0, full.Loss);
        Assert.Equal(98.7, full.SpeedMbps);
        Assert.True(full.IsProxied);

        var sparse = EndpointItem.FromScanEndpoint(new ScanEndpoint { Endpoint = "162.159.192.2:2408" });
        Assert.Equal("", sparse.Node);
        Assert.Equal("", sparse.Country);
        Assert.Equal("", sparse.City);
        Assert.Equal("", sparse.Location);
        Assert.Equal(0, sparse.PingMs);
        Assert.Equal(0, sparse.TunPingMs);
        Assert.Null(sparse.Loss);
        Assert.Equal(0, sparse.SpeedMbps);
        Assert.False(sparse.IsProxied);
    }

    [Fact]
    public void CompareEndpoints_OrdersByLossThenEffectivePing()
    {
        var lowLossHighPing = new EndpointItem { Endpoint = "a", Loss = 0, TunPingMs = 120 };
        var higherLossLowPing = new EndpointItem { Endpoint = "b", Loss = 10, TunPingMs = 20 };
        var nullLossMediumPing = new EndpointItem { Endpoint = "c", Loss = null, PingMs = 60 };

        Assert.True(MainWindow.CompareEndpoints(lowLossHighPing, higherLossLowPing) < 0);
        Assert.True(MainWindow.CompareEndpoints(nullLossMediumPing, lowLossHighPing) < 0);
        Assert.True(MainWindow.CompareEndpoints(nullLossMediumPing, higherLossLowPing) < 0);
    }

    [Fact]
    public void Strings_V1StreamAndLogHelpers_FormatCleanly()
    {
        var s = Strings.Instance;
        s.SetLanguage("en");
        Assert.False(string.IsNullOrWhiteSpace(s.PhasePorts));
        Assert.False(string.IsNullOrWhiteSpace(s.PhaseTunnels));
        Assert.Equal("~ TORN 162.159.192.1:2408 (FRA)", s.LogEndpointTorn("162.159.192.1:2408", "FRA"));
        Assert.Equal("+ FOUND 162.159.192.1:2408 | — | ping=— tun=— loss=—", s.LogEndpointFound("162.159.192.1:2408", "", "", "—", null, null, null, ""));
        Assert.Equal("+ FOUND 162.159.192.1:2408 | DE Frankfurt (FRA) | ping=40ms tun=50ms loss=10%", s.LogEndpointFound("162.159.192.1:2408", "DE", "Frankfurt", "FRA", 40, 50, 10, ""));
        Assert.Equal("Done: 5 working servers, torn: 2, best: 162.159.192.1:2408", s.LogScanDone(5, "162.159.192.1:2408", 2));
        Assert.Equal("Done: 5 working servers, best: 162.159.192.1:2408", s.LogScanDone(5, "162.159.192.1:2408"));
    }
}

using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace WarpscoutGui;

public sealed class Awg31Options
{
    public string Padding { get; set; } = "";
    public string RekeyAfter { get; set; } = "";
    public string RekeyTimeout { get; set; } = "";
    public string RejectAfter { get; set; } = "";
    public string KeepaliveTimeout { get; set; } = "";
    public string MaxHandshakes { get; set; } = "";
    public bool DisableCookies { get; set; }
    public bool RandomTrailers { get; set; }

    internal (string Flag, string ConfKey, string Value)[] GetRangeFields() =>
    [
        ("padding", "ContentPaddingAddition", Padding),
        ("rekey-after", "RekeyAfterTime", RekeyAfter),
        ("rekey-timeout", "RekeyTimeout", RekeyTimeout),
        ("reject-after", "RejectAfterTime", RejectAfter),
        ("keepalive-timeout", "KeepaliveTimeout", KeepaliveTimeout),
        ("max-handshakes", "MaxHandshakeAttempts", MaxHandshakes),
    ];

    internal static bool IsValidRange(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        var s = raw.Trim();
        var dashIdx = s.IndexOf('-');
        if (dashIdx < 0)
            return uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out _);

        var parts = s.Split('-');
        return parts.Length == 2
            && uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var min)
            && uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var max)
            && min <= max;
    }

    internal (string Flag, string Value)? FindFirstInvalidRange()
    {
        foreach (var (flag, _, value) in GetRangeFields())
        {
            if (!string.IsNullOrWhiteSpace(value) && !IsValidRange(value))
                return ("-" + flag, value.Trim());
        }
        return null;
    }

    internal void AppendCliArgs(ICollection<string> args)
    {
        foreach (var (flag, _, value) in GetRangeFields())
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                args.Add("-" + flag);
                args.Add(value.Trim());
            }
        }
        if (DisableCookies)
            args.Add("-disable-cookies");
        if (RandomTrailers)
            args.Add("-random-trailers");
    }

    internal List<string> FormatSummaryParts()
    {
        var parts = new List<string>();
        foreach (var (flag, _, value) in GetRangeFields())
        {
            if (!string.IsNullOrWhiteSpace(value))
                parts.Add($"{flag} {value.Trim()}");
        }
        if (RandomTrailers)
            parts.Add("random-trailers");
        if (DisableCookies)
            parts.Add("disable-cookies");
        return parts;
    }

    internal static Awg31Options ParseFromCliArgs(IReadOnlyList<string> args, int startIdx = 0)
    {
        var opts = new Awg31Options();
        for (int i = Math.Max(0, startIdx); i < args.Count; i++)
        {
            var arg = args[i];
            if (string.IsNullOrEmpty(arg) || !arg.StartsWith('-'))
                continue;

            var eqIdx = arg.IndexOf('=');
            var rawName = eqIdx >= 0 ? arg[..eqIdx] : arg;
            var name = rawName.StartsWith("--", StringComparison.Ordinal)
                ? rawName[2..]
                : rawName[1..];

            if (name == "disable-cookies")
            {
                opts.DisableCookies = eqIdx < 0 || ParseBoolArg(arg[(eqIdx + 1)..]);
                continue;
            }
            if (name == "random-trailers")
            {
                opts.RandomTrailers = eqIdx < 0 || ParseBoolArg(arg[(eqIdx + 1)..]);
                continue;
            }

            string? val = null;
            if (eqIdx >= 0)
                val = arg[(eqIdx + 1)..].Trim();
            else if (i + 1 < args.Count && !args[i + 1].StartsWith('-'))
                val = args[++i].Trim();

            if (val == null)
                continue;

            switch (name)
            {
                case "padding": opts.Padding = val; break;
                case "rekey-after": opts.RekeyAfter = val; break;
                case "rekey-timeout": opts.RekeyTimeout = val; break;
                case "reject-after": opts.RejectAfter = val; break;
                case "keepalive-timeout": opts.KeepaliveTimeout = val; break;
                case "max-handshakes": opts.MaxHandshakes = val; break;
            }
        }
        return opts;
    }

    private static bool ParseBoolArg(string raw)
    {
        var v = raw.Trim();
        return v == "1" || (bool.TryParse(v, out var b) && b);
    }
}

public sealed class AppSettings
{
    internal const int CurrentSettingsVersion = 2;

    public int SettingsVersion { get; set; }
    public string CorePath { get; set; } = "";
    public string CoreRepo { get; set; } = "";
    public string Language { get; set; } = "ru";
    public bool ExcludeNodeEnabled { get; set; } = true;
    public string ExcludedNode { get; set; } = "DME";
    public bool MinimizeToTray { get; set; } = false;
    public bool MinimizeToTrayWithProxy { get; set; } = true;
    public bool WarnOnCloseWithProxy { get; set; } = false;
    public bool AutoUpdateGui { get; set; } = true;
    public string DefaultExitAction { get; set; } = "";
    public string CustomScanFlags { get; set; } = "-p awg -P -exclude-node DME";
    public Awg31Options Awg31 { get; set; } = new();
}

internal enum CloseAction
{
    Exit,
    MinimizeToTray,
    Prompt,
}

public sealed class SavedScan
{
    public DateTimeOffset ScannedAt { get; set; }
    public string Proto { get; set; } = "";
    public string BestEndpoint { get; set; } = "";
    public List<SavedEndpoint> Endpoints { get; set; } = [];
}

public sealed class SavedEndpoint
{
    public string Endpoint { get; set; } = "";
    public string Node { get; set; } = "";
    public string Country { get; set; } = "";
    public string City { get; set; } = "";
    public string Location { get; set; } = "";
    public double PingMs { get; set; }
    public double TunPingMs { get; set; }
    public double? Loss { get; set; }
    public double SpeedMbps { get; set; }
}

public sealed class MasqueAccount
{
    [JsonPropertyName("id")] public string ID { get; set; } = "";
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("private_key")] public string PrivateKey { get; set; } = "";
    [JsonPropertyName("peer_public_key")] public string PeerPublicKey { get; set; } = "";
    [JsonPropertyName("ipv4")] public string IPv4 { get; set; } = "";
    [JsonPropertyName("ipv6")] public string IPv6 { get; set; } = "";
}

public sealed class AccountData
{
    [JsonPropertyName("id")] public string ID { get; set; } = "";
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("private_key")] public string PrivateKey { get; set; } = "";
    [JsonPropertyName("peer_public_key")] public string PeerPublicKey { get; set; } = "";
    [JsonPropertyName("ipv4")] public string IPv4 { get; set; } = "";
    [JsonPropertyName("ipv6")] public string IPv6 { get; set; } = "";
    [JsonPropertyName("masque")] public MasqueAccount? Masque { get; set; }
}

public sealed class ScanEndpoint
{
    [JsonPropertyName("endpoint")] public string Endpoint { get; set; } = "";
    [JsonPropertyName("seen_as")] public string? SeenAs { get; set; }
    [JsonPropertyName("node")] public string? Node { get; set; }
    [JsonPropertyName("node_city")] public string? NodeCity { get; set; }
    [JsonPropertyName("node_country")] public string? NodeCountry { get; set; }
    [JsonPropertyName("ping_ms")] public double? PingMs { get; set; }
    [JsonPropertyName("tun_ping_ms")] public double? TunPingMs { get; set; }
    [JsonPropertyName("loss_pct")] public double? LossPct { get; set; }
    [JsonPropertyName("speed_mbps")] public double? SpeedMbps { get; set; }
    [JsonPropertyName("torn")] public bool Torn { get; set; }
}

public sealed class ScanResult
{
    [JsonPropertyName("proto")] public string? Proto { get; set; }
    [JsonPropertyName("working_count")] public int WorkingCount { get; set; }
    [JsonPropertyName("best")] public string? Best { get; set; }
    [JsonPropertyName("working")] public List<ScanEndpoint>? Working { get; set; }
    [JsonPropertyName("torn")] public List<ScanEndpoint>? Torn { get; set; }
}

public sealed class ScanStreamEvent
{
    [JsonPropertyName("event")] public string Event { get; set; } = "";
    [JsonPropertyName("phase")] public string? Phase { get; set; }
    [JsonPropertyName("done")] public int Done { get; set; }
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("endpoint")] public ScanEndpoint? Endpoint { get; set; }
    [JsonPropertyName("result")] public ScanResult? Result { get; set; }
}

public sealed class EndpointItem : INotifyPropertyChanged
{
    private static readonly SolidColorBrush BrushGreen = FreezeBrush(52, 211, 153);
    private static readonly SolidColorBrush BrushAmber = FreezeBrush(251, 191, 36);
    private static readonly SolidColorBrush BrushRed = FreezeBrush(248, 113, 113);

    private bool _isBest;
    private bool _isProxied;
    private double _pingMs;
    private double _tunPingMs;
    private double? _loss;
    private double _speedMbps;

    public string Endpoint { get; set; } = "";
    public string Node { get; set; } = "";
    public string Country { get; set; } = "";
    public string City { get; set; } = "";
    public string Location { get; set; } = "";

    public double PingMs
    {
        get => _pingMs;
        set { _pingMs = value; Notify(nameof(DisplayPing)); Notify(nameof(PingBrush)); }
    }

    public double TunPingMs
    {
        get => _tunPingMs;
        set { _tunPingMs = value; Notify(nameof(DisplayPing)); Notify(nameof(PingBrush)); }
    }

    public double? Loss
    {
        get => _loss;
        set { _loss = value; Notify(nameof(DisplayLoss)); }
    }

    public double SpeedMbps
    {
        get => _speedMbps;
        set { _speedMbps = value; Notify(nameof(DisplaySpeed)); }
    }

    public bool IsBest
    {
        get => _isBest;
        set { _isBest = value; Notify(nameof(IsBest)); }
    }

    public bool IsProxied
    {
        get => _isProxied;
        set { _isProxied = value; Notify(nameof(IsProxied)); Notify(nameof(ProxyButtonText)); }
    }

    public double EffectivePingMs => TunPingMs > 0 ? TunPingMs : (PingMs > 0 ? PingMs : 9999);
    public string CountryBadge => string.IsNullOrWhiteSpace(Country) ? "CF" : Country;
    public string DisplayLocation => !string.IsNullOrWhiteSpace(City) ? City : (!string.IsNullOrWhiteSpace(Location) ? Location : "Cloudflare");
    public string DisplayNode => string.IsNullOrWhiteSpace(Node) ? "—" : Node;
    public string DisplayPing => EffectivePingMs < 9999 ? $"{Math.Round(EffectivePingMs)} {Strings.Instance.UnitMs}" : "—";
    public System.Windows.Media.Brush PingBrush => EffectivePingMs < 80 ? BrushGreen : (EffectivePingMs < 150 ? BrushAmber : BrushRed);
    public string DisplayLoss => Loss.HasValue ? $"{Math.Round(Loss.Value)}%" : "—";
    public string DisplaySpeed => SpeedMbps > 0 ? $"{SpeedMbps:0.0} {Strings.Instance.UnitMbps}" : "—";
    public string ProxyButtonText => IsProxied ? Strings.Instance.RowProxyActive : Strings.Instance.RowProxyStart;

    internal static EndpointItem FromScanEndpoint(ScanEndpoint ep, bool isProxied = false) => new()
    {
        Endpoint = ep.Endpoint,
        Node = ep.Node ?? "",
        Country = ep.NodeCountry ?? "",
        City = ep.NodeCity ?? "",
        Location = ep.SeenAs ?? "",
        PingMs = ep.PingMs ?? 0,
        TunPingMs = ep.TunPingMs ?? 0,
        Loss = ep.LossPct,
        SpeedMbps = ep.SpeedMbps ?? 0,
        IsProxied = isProxied,
    };

    public void RefreshLocale()
    {
        Notify(nameof(DisplayPing));
        Notify(nameof(DisplaySpeed));
        Notify(nameof(ProxyButtonText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? prop = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));

    private static SolidColorBrush FreezeBrush(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
}

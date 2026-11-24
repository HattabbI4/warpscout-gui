using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace WarpscoutGui;

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
    public string DefaultExitAction { get; set; } = "";
    public string CustomScanFlags { get; set; } = "-p awg -P -exclude-node DME";
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

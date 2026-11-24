using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace WarpscoutGui;

public partial class MainWindow : FluentWindow
{
    internal const string GithubRepoUrl = "https://github.com/HattabbI4/warpscout-gui";

    private static readonly DependencyProperty GithubCornerProgressProperty =
        DependencyProperty.Register(
            nameof(GithubCornerProgress),
            typeof(double),
            typeof(MainWindow),
            new PropertyMetadata(0.0, OnGithubCornerProgressChanged));

    private double GithubCornerProgress
    {
        get => (double)GetValue(GithubCornerProgressProperty);
        set => SetValue(GithubCornerProgressProperty, value);
    }

    private static void OnGithubCornerProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MainWindow win)
            win.UpdateGithubCornerVisuals((double)e.NewValue);
    }

    private readonly AppSettings _settings;
    private readonly ObservableCollection<EndpointItem> _endpoints = [];
    private readonly Dictionary<string, EndpointItem> _endpointMap = new(StringComparer.OrdinalIgnoreCase);

    private string _corePath = "";
    private string _coreVersion = "";
    private string _accountPath = "";
    private AccountData? _account;
    private string _selectedProto = "awg";
    private DateTimeOffset? _lastScanTime;
    private string? _bestEndpoint;

    private Process? _scanProcess;
    private CancellationTokenSource? _scanCts;
    private bool _scanStoppedByUser;

    private Process? _socksProcess;
    private string _socksEndpoint = "";
    private string _socksProto = "";

    private bool _isDownloadingCore;
    private bool _isSidebarCollapsed;
    private bool _isAdvancedScanMode;
    private bool _isExiting;
    private bool _isInitializing = true;
    private bool _githubCornerPressed;
    private readonly DispatcherTimer _githubCornerHoverTimer;

    private WinForms.NotifyIcon? _trayIcon;
    private Drawing.Icon? _trayIconHandle;
    private WinForms.ToolStripMenuItem? _trayShowItem;
    private WinForms.ToolStripMenuItem? _trayStopProxyItem;
    private WinForms.ToolStripMenuItem? _trayMinimizeToTrayItem;
    private WinForms.ToolStripMenuItem? _trayExitItem;

    public MainWindow()
    {
        InitializeComponent();
        UpdateGithubCornerVisuals(0.0);
        _githubCornerHoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _githubCornerHoverTimer.Tick += (_, _) =>
        {
            _githubCornerHoverTimer.Stop();
            if (GithubCornerHost.IsMouseOver)
                AnimateGithubCorner(true);
        };
        _settings = CoreService.LoadSettings();

        GridEndpoints.ItemsSource = _endpoints;
        LoadSettingsIntoUI();
        LoadLastScanIntoUI();
        UpdateHeroSubtitle();
        _isInitializing = false;

        SetupTray();
        ApplyLanguageToDynamicControls();

        Loaded += MainWindow_Loaded;
    }

    private void LoadSettingsIntoUI()
    {
        var lang = string.Equals(_settings.Language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "ru";
        Strings.Instance.SetLanguage(lang);
        CmbLanguage.SelectedIndex = lang == "en" ? 1 : 0;

        TxtCorePath.Text = _settings.CorePath;
        TxtCustomFlags.Text = _settings.CustomScanFlags;
        ChkDme.IsChecked = _settings.ExcludeNodeEnabled;
        TxtExcludeNode.Text = _settings.ExcludedNode;
        TglMinimizeToTray.IsChecked = _settings.MinimizeToTray;
        TglMinimizeToTrayWithProxy.IsChecked = _settings.MinimizeToTrayWithProxy;
        TglWarnOnExit.IsChecked = _settings.WarnOnCloseWithProxy;

        var awg31 = _settings.Awg31 ??= new Awg31Options();
        TxtAwg31Padding.Text = awg31.Padding;
        TxtAwg31RekeyAfter.Text = awg31.RekeyAfter;
        TxtAwg31RekeyTimeout.Text = awg31.RekeyTimeout;
        TxtAwg31RejectAfter.Text = awg31.RejectAfter;
        TxtAwg31KeepaliveTimeout.Text = awg31.KeepaliveTimeout;
        TxtAwg31MaxHandshakes.Text = awg31.MaxHandshakes;
        ChkAwg31DisableCookies.IsChecked = awg31.DisableCookies;
        ChkAwg31RandomTrailers.IsChecked = awg31.RandomTrailers;
    }

    private void LoadLastScanIntoUI()
    {
        var saved = CoreService.LoadLastScan();
        if (saved == null) return;

        if (!string.IsNullOrWhiteSpace(saved.Proto))
        {
            _selectedProto = saved.Proto;
            foreach (var item in CmbProto.Items.OfType<ComboBoxItem>())
            {
                if (item.Tag is string tag && string.Equals(tag, saved.Proto, StringComparison.OrdinalIgnoreCase))
                {
                    CmbProto.SelectedItem = item;
                    break;
                }
            }
        }

        foreach (var ep in saved.Endpoints)
        {
            if (_endpointMap.ContainsKey(ep.Endpoint))
                continue;
            var item = new EndpointItem
            {
                Endpoint = ep.Endpoint,
                Node = ep.Node,
                Country = ep.Country,
                City = ep.City,
                Location = ep.Location,
                PingMs = ep.PingMs,
                TunPingMs = ep.TunPingMs,
                Loss = ep.Loss,
                SpeedMbps = ep.SpeedMbps,
            };
            _endpointMap[ep.Endpoint] = item;
            _endpoints.Add(item);
        }

        _bestEndpoint = string.IsNullOrWhiteSpace(saved.BestEndpoint) ? null : saved.BestEndpoint;
        _lastScanTime = saved.ScannedAt != default ? saved.ScannedAt.ToLocalTime() : null;
    }

    private void SaveCurrentScan(string proto)
    {
        if (_endpoints.Count == 0 || _lastScanTime == null) return;
        var scan = new SavedScan
        {
            ScannedAt = _lastScanTime.Value,
            Proto = proto,
            BestEndpoint = _endpoints.FirstOrDefault(x => x.IsBest)?.Endpoint ?? "",
            Endpoints = _endpoints.Select(ep => new SavedEndpoint
            {
                Endpoint = ep.Endpoint,
                Node = ep.Node,
                Country = ep.Country,
                City = ep.City,
                Location = ep.Location,
                PingMs = ep.PingMs,
                TunPingMs = ep.TunPingMs,
                Loss = ep.Loss,
                SpeedMbps = ep.SpeedMbps,
            }).ToList(),
        };
        CoreService.SaveLastScan(scan);
    }

    private void UpdateLastScanTimeUI()
    {
        if (_lastScanTime.HasValue && _endpoints.Count > 0)
        {
            TxtLastScanTime.Text = Strings.Instance.LastScanTime(_lastScanTime.Value);
            LastScanBadge.Visibility = Visibility.Visible;
        }
        else
        {
            TxtLastScanTime.Text = "";
            LastScanBadge.Visibility = Visibility.Collapsed;
        }
    }

    private void SetupTray()
    {
        var s = Strings.Instance;
        _trayIconHandle = LoadAppTrayIcon();
        var menu = new WinForms.ContextMenuStrip();
        _trayShowItem = new WinForms.ToolStripMenuItem(s.TrayShow, null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
        _trayStopProxyItem = new WinForms.ToolStripMenuItem(s.TrayStopProxy, null, (_, _) => Dispatcher.Invoke(StopSocksProcess));
        _trayMinimizeToTrayItem = new WinForms.ToolStripMenuItem(s.TrayMinimizeOption)
        {
            CheckOnClick = true,
            Checked = _settings.MinimizeToTray,
        };
        _trayMinimizeToTrayItem.Click += (_, _) => Dispatcher.Invoke(OnTrayMinimizeOptionClicked);
        _trayExitItem = new WinForms.ToolStripMenuItem(s.TrayExit, null, (_, _) => Dispatcher.Invoke(ForceShutdown));
        menu.Items.Add(_trayShowItem);
        menu.Items.Add(_trayStopProxyItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_trayMinimizeToTrayItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_trayExitItem);

        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = _trayIconHandle,
            Text = s.TrayTooltip,
            Visible = true,
            ContextMenuStrip = menu,
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        _trayIcon.BalloonTipClicked += (_, _) => Dispatcher.Invoke(RestoreFromTray);
    }

    private void OnTrayMinimizeOptionClicked()
    {
        if (_trayMinimizeToTrayItem == null) return;
        _settings.MinimizeToTray = _trayMinimizeToTrayItem.Checked;
        _settings.DefaultExitAction = "";
        SyncTraySettingsUI();
        CoreService.SaveSettings(_settings);
    }

    private void SyncTraySettingsUI()
    {
        _isInitializing = true;
        try
        {
            TglMinimizeToTray.IsChecked = _settings.MinimizeToTray;
            TglMinimizeToTrayWithProxy.IsChecked = _settings.MinimizeToTrayWithProxy;
            TglWarnOnExit.IsChecked = _settings.WarnOnCloseWithProxy;
        }
        finally
        {
            _isInitializing = false;
        }
        if (_trayMinimizeToTrayItem != null)
            _trayMinimizeToTrayItem.Checked = _settings.MinimizeToTray;
    }

    private static Drawing.Icon LoadAppTrayIcon()
    {
        try
        {
            var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (res?.Stream != null)
                return new Drawing.Icon(res.Stream);
        }
        catch { }
        return Drawing.SystemIcons.Application;
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshCoreAndAccountAsync();

        // First-launch or upgrading from the temporary fork / pre-0.17 core: download upstream core and lock UI.
        if (CoreService.ShouldAutoUpgradeCore(_corePath, _settings, _coreVersion))
            await DownloadOrUpdateCoreAsync();
    }

    private void SelectPage(string page)
    {
        if (page == "scanner" || page == "advanced")
        {
            _isAdvancedScanMode = page == "advanced";
            TxtScannerPageTitle.Text = _isAdvancedScanMode ? Strings.Instance.AdvancedTitle : Strings.Instance.ScannerTitle;
            ScanOptionsCards.Visibility = _isAdvancedScanMode ? Visibility.Collapsed : Visibility.Visible;
            AdvancedOptionsCard.Visibility = _isAdvancedScanMode ? Visibility.Visible : Visibility.Collapsed;
            if (!_isAdvancedScanMode && CmbProto.SelectedItem is ComboBoxItem item && item.Tag is string proto)
                _selectedProto = proto;
            UpdateHeroSubtitle();
        }

        PageScanner.Visibility = (page == "scanner" || page == "advanced") ? Visibility.Visible : Visibility.Collapsed;
        PageServers.Visibility = page == "servers" ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;

        var activeBg = new SolidColorBrush(System.Windows.Media.Color.FromArgb(20, 255, 255, 255));
        var clear = System.Windows.Media.Brushes.Transparent;
        var accent = new SolidColorBrush(System.Windows.Media.Color.FromRgb(96, 205, 255));
        var dimIcon = new SolidColorBrush(System.Windows.Media.Color.FromRgb(209, 213, 219));
        var dimText = new SolidColorBrush(System.Windows.Media.Color.FromRgb(229, 231, 235));
        var white = System.Windows.Media.Brushes.White;

        ApplyNavItemState(NavScanner, IndScanner, IconScanner, TxtNavScanner, page == "scanner", activeBg, clear, accent, dimIcon, white, dimText);
        ApplyNavItemState(NavAdvanced, IndAdvanced, IconAdvanced, TxtNavAdvanced, page == "advanced", activeBg, clear, accent, dimIcon, white, dimText);
        ApplyNavItemState(NavServers, IndServers, IconServers, TxtNavServers, page == "servers", activeBg, clear, accent, dimIcon, white, dimText);
        ApplyNavItemState(NavSettings, IndSettings, IconSettings, TxtNavSettings, page == "settings", activeBg, clear, accent, dimIcon, white, dimText);
    }

    private static void ApplyNavItemState(
        System.Windows.Controls.Button btn, Border ind, SymbolIcon icon, System.Windows.Controls.TextBlock label, bool active,
        System.Windows.Media.Brush activeBg, System.Windows.Media.Brush clear, System.Windows.Media.Brush accent,
        System.Windows.Media.Brush dimIcon, System.Windows.Media.Brush white, System.Windows.Media.Brush dimText)
    {
        btn.Background = active ? activeBg : clear;
        ind.Background = active ? accent : clear;
        icon.Foreground = active ? accent : dimIcon;
        label.Foreground = active ? white : dimText;
        label.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void SetSidebarCollapsed(bool collapsed)
    {
        _isSidebarCollapsed = collapsed;
        SidebarCol.Width = new GridLength(collapsed ? 56 : 232);
        var labelVis = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarBrandText.Visibility = labelVis;
        TxtNavScanner.Visibility = labelVis;
        TxtNavAdvanced.Visibility = labelVis;
        TxtNavServers.Visibility = labelVis;
        TxtNavSettings.Visibility = labelVis;
        NavServersBadge.Visibility = !collapsed && _endpoints.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnToggleSidebar_Click(object sender, RoutedEventArgs e) => SetSidebarCollapsed(!_isSidebarCollapsed);

    private void BtnVersionStatus_Click(object sender, RoutedEventArgs e) => SelectPage("settings");

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string page)
            SelectPage(page);
    }

    private void BtnGoToServers_Click(object sender, RoutedEventArgs e) => SelectPage("servers");
    private void BtnGoToScanner_Click(object sender, RoutedEventArgs e) => SelectPage(_isAdvancedScanMode ? "advanced" : "scanner");

    private void GithubCorner_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _githubCornerHoverTimer.Stop();
        _githubCornerHoverTimer.Start();
    }

    private void GithubCorner_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _githubCornerHoverTimer.Stop();
        _githubCornerPressed = false;
        AnimateGithubCorner(false);
    }

    private void GithubCorner_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _githubCornerPressed = true;
        e.Handled = true;
    }

    private void GithubCorner_Click(object sender, MouseButtonEventArgs e)
    {
        if (!_githubCornerPressed) return;
        _githubCornerPressed = false;
        e.Handled = true;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(GithubRepoUrl) { UseShellExecute = true });
        }
        catch { }
    }

    private void AnimateGithubCorner(bool open)
    {
        var anim = new DoubleAnimation(
            open ? 1.0 : 0.0,
            TimeSpan.FromMilliseconds(open ? 260 : 210))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(GithubCornerProgressProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    internal void UpdateGithubCornerVisuals(double p)
    {
        p = Math.Clamp(p, 0.0, 1.0);
        const double box = 140.0;
        double s = 50.0 + 82.0 * p;
        double edge = box - s;

        System.Windows.Point Pt(double u, double v) => new(edge + s * u, edge + s * v);

        var revealTri = new StreamGeometry();
        using (var ctx = revealTri.Open())
        {
            ctx.BeginFigure(new System.Windows.Point(edge, box), isFilled: true, isClosed: true);
            ctx.LineTo(new System.Windows.Point(box, edge), isStroked: true, isSmoothJoin: false);
            ctx.LineTo(new System.Windows.Point(box, box), isStroked: true, isSmoothJoin: false);
        }
        revealTri.Freeze();
        GithubCornerRevealLayer.Clip = revealTri;
        GithubCornerRevealBg.Data = revealTri;

        double d = 0.22 * s;
        var creaseShadow = new StreamGeometry();
        using (var ctx = creaseShadow.Open())
        {
            ctx.BeginFigure(new System.Windows.Point(edge, box), isFilled: true, isClosed: true);
            ctx.LineTo(new System.Windows.Point(box, edge), isStroked: false, isSmoothJoin: false);
            ctx.LineTo(new System.Windows.Point(box, edge + d), isStroked: false, isSmoothJoin: false);
            ctx.LineTo(new System.Windows.Point(edge + d, box), isStroked: false, isSmoothJoin: false);
        }
        creaseShadow.Freeze();
        GithubCornerCreaseShadow.Data = creaseShadow;

        double glowR = 0.36 * s;
        double glowC = box - 0.22 * s;
        GithubCornerGlow.Width = glowR * 2;
        GithubCornerGlow.Height = glowR * 2;
        Canvas.SetLeft(GithubCornerGlow, glowC - glowR);
        Canvas.SetTop(GithubCornerGlow, glowC - glowR);

        var flapGeo = new StreamGeometry();
        using (var ctx = flapGeo.Open())
        {
            ctx.BeginFigure(Pt(0.00, 1.00), isFilled: true, isClosed: true);
            ctx.BezierTo(Pt(0.05, 0.78), Pt(0.145, 0.485), Pt(0.18, 0.38), isStroked: true, isSmoothJoin: true);
            ctx.QuadraticBezierTo(Pt(0.23, 0.23), Pt(0.38, 0.18), isStroked: true, isSmoothJoin: true);
            ctx.BezierTo(Pt(0.485, 0.145), Pt(0.78, 0.05), Pt(1.00, 0.00), isStroked: true, isSmoothJoin: true);
        }
        flapGeo.Freeze();
        GithubCornerFlapPath.Data = flapGeo;

        double octoScale = 0.77 + 0.40 * p;
        GithubCornerOctoScale.ScaleX = octoScale;
        GithubCornerOctoScale.ScaleY = octoScale;
        GithubCornerOctoRotate.Angle = -6.0 * p;

        double octoOffset = 111.0 - 9.0 * p;
        GithubCornerOctoTranslate.X = octoOffset;
        GithubCornerOctoTranslate.Y = octoOffset;

        GithubCornerCta.Opacity = Math.Clamp((p - 0.25) / 0.75, 0.0, 1.0);
        GithubCornerStarGlint.Opacity = Math.Clamp((p - 0.45) / 0.55, 0.0, 1.0);
    }

    private async Task RefreshCoreAndAccountAsync()
    {
        _corePath = CoreService.ResolveCorePath(_settings);
        _accountPath = CoreService.ResolveAccountPath();

        if (!string.IsNullOrEmpty(_corePath))
        {
            TxtCorePath.Text = _corePath;
            _coreVersion = await CoreService.GetCoreVersionAsync(_corePath);
        }
        else
        {
            _coreVersion = "";
        }

        _account = CoreService.ReadAccount(_accountPath);
        UpdateCoreAndAccountLabels();
        SetWorkspaceEnabled(!_isDownloadingCore && !string.IsNullOrEmpty(_corePath));
    }

    private void UpdateCoreAndAccountLabels()
    {
        var s = Strings.Instance;
        if (!string.IsNullOrEmpty(_corePath))
        {
            StatusDot.Fill = new SolidColorBrush(_account != null
                ? System.Windows.Media.Color.FromRgb(16, 185, 129)
                : System.Windows.Media.Color.FromRgb(245, 158, 11));

            var verShown = string.IsNullOrEmpty(_coreVersion) ? s.CoreFoundFallback : _coreVersion;
            TxtStatusPill.Text = _account != null ? s.StatusPillReady(verShown) : s.StatusPillNoKey(verShown);
            TxtCoreVersionCard.Text = s.CoreVersionInstalled(verShown, _corePath);
            TxtAccountDetails.Text = _account != null
                ? s.AccountDetailsReady(_account.ID, _account.IPv4)
                : s.AccountWillAutoRegister;
        }
        else
        {
            StatusDot.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
            TxtStatusPill.Text = s.StatusPillMissing;
            TxtCoreVersionCard.Text = s.CoreVersionMissing;
            TxtAccountDetails.Text = _account != null ? $"ID: {_account.ID}" : s.AccountRequiresCore;
        }
    }

    private void SetWorkspaceEnabled(bool enabled)
    {
        MainWorkspace.IsHitTestVisible = enabled;
        MainWorkspace.Opacity = enabled ? 1.0 : 0.45;
        BtnScan.IsEnabled = enabled;
        BtnRefreshServers.IsEnabled = enabled;
        ScanOptionsCards.IsEnabled = enabled;
        AdvancedOptionsCard.IsEnabled = enabled;
        BestServerCard.IsEnabled = enabled;
        BtnStopSocks.IsEnabled = enabled;
        GridEndpoints.IsHitTestVisible = enabled;
    }

    private void SetCoreDownloadingUI(bool downloading, string? statusText = null, double percent = 0)
    {
        _isDownloadingCore = downloading;
        CoreDownloadPanel.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        if (downloading)
        {
            CoreNoticePanel.Visibility = Visibility.Collapsed;
            TxtCoreDownloadStatus.Text = statusText ?? Strings.Instance.ScanPreparing;
            PbCoreDownload.Value = percent;
        }

        SidebarNavPanel.IsEnabled = !downloading;
        BtnCheckDownloadCore.IsEnabled = !downloading;
        BtnBrowseCore.IsEnabled = !downloading;
        TxtCorePath.IsEnabled = !downloading;
        BtnRegisterAccount.IsEnabled = !downloading;
        bool coreReady = !string.IsNullOrEmpty(_corePath) && File.Exists(_corePath);
        SetWorkspaceEnabled(!downloading && coreReady);
    }

    private void ShowCoreNotice(bool isError, string title, string message)
    {
        CoreNoticePanel.Visibility = Visibility.Visible;
        CoreNoticePanel.Background = new SolidColorBrush(isError
            ? System.Windows.Media.Color.FromArgb(40, 239, 68, 68)
            : System.Windows.Media.Color.FromArgb(35, 16, 185, 129));
        CoreNoticePanel.BorderBrush = new SolidColorBrush(isError
            ? System.Windows.Media.Color.FromArgb(120, 239, 68, 68)
            : System.Windows.Media.Color.FromArgb(120, 16, 185, 129));
        TxtCoreNoticeTitle.Text = title;
        TxtCoreNoticeMessage.Text = message;
        BtnRetryDownload.Visibility = isError ? Visibility.Visible : Visibility.Collapsed;
        BtnPickCoreFromError.Visibility = isError ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task DownloadOrUpdateCoreAsync()
    {
        if (_isDownloadingCore) return;
        var s = Strings.Instance;

        SetCoreDownloadingUI(true, s.CheckingGithubRelease, 5);
        try
        {
            var progress = new Progress<(double percent, string status)>(p =>
            {
                PbCoreDownload.Value = p.percent;
                TxtCoreDownloadStatus.Text = p.status;
            });

            bool forceUpdate = !string.Equals(_settings.CoreRepo, CoreService.CoreGithubRepo, StringComparison.OrdinalIgnoreCase);
            var (savedPath, ver, updated) = await CoreService.DownloadOrUpdateCoreAsync(
                CoreService.DefaultDownloadedCorePath,
                _corePath,
                () => Dispatcher.Invoke(() =>
                {
                    StopScanProcess();
                    StopSocksProcess();
                }),
                progress,
                CancellationToken.None,
                forceUpdate);

            if (updated)
            {
                _settings.CorePath = savedPath;
                _settings.CoreRepo = CoreService.CoreGithubRepo;
                CoreService.SaveSettings(_settings);
            }
            SetCoreDownloadingUI(false);
            await RefreshCoreAndAccountAsync();
            if (updated)
                ShowCoreNotice(false, s.CoreReadyTitle, s.CoreReadyMsg(savedPath, ver));
            else
                ShowCoreNotice(false, s.CoreUpToDateTitle, s.CoreUpToDateMsg(ver));
        }
        catch (Exception ex)
        {
            SetCoreDownloadingUI(false);
            await RefreshCoreAndAccountAsync();
            ShowCoreNotice(true, s.CoreDownloadErrorTitle, s.CoreDownloadErrorMsg(ex.Message));
        }
    }

    private async void BtnUpdateCore_Click(object sender, RoutedEventArgs e) => await DownloadOrUpdateCoreAsync();

    private void BtnDismissCoreNotice_Click(object sender, RoutedEventArgs e) =>
        CoreNoticePanel.Visibility = Visibility.Collapsed;

    private async void BtnBrowseCore_Click(object sender, RoutedEventArgs e)
    {
        var s = Strings.Instance;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = s.BrowseCoreTitle,
            Filter = s.BrowseCoreFilter,
            CheckFileExists = true,
        };
        if (!string.IsNullOrEmpty(_settings.CorePath))
        {
            var dir = Path.GetDirectoryName(_settings.CorePath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }

        if (dlg.ShowDialog() == true)
            await ApplyManualCorePathAsync(dlg.FileName);
    }

    private async void TxtCorePath_LostFocus(object sender, RoutedEventArgs e)
    {
        if (BtnBrowseCore.IsMouseOver || BtnPickCoreFromError.IsMouseOver) return;
        var entered = TxtCorePath.Text.Trim();
        if (!string.Equals(entered, _settings.CorePath, StringComparison.OrdinalIgnoreCase))
            await ApplyManualCorePathAsync(entered);
    }

    private async void TxtCorePath_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await ApplyManualCorePathAsync(TxtCorePath.Text.Trim());
        }
    }

    private async Task ApplyManualCorePathAsync(string path)
    {
        var s = Strings.Instance;
        if (string.IsNullOrWhiteSpace(path))
        {
            _settings.CorePath = "";
            CoreService.SaveSettings(_settings);
            CoreNoticePanel.Visibility = Visibility.Collapsed;
            await RefreshCoreAndAccountAsync();
            return;
        }

        if (!File.Exists(path))
        {
            ShowCoreNotice(true, s.CorePathMissingTitle, s.CorePathMissingMsg(path));
            return;
        }

        var fullPath = Path.GetFullPath(path);
        _settings.CorePath = fullPath;
        if (!string.Equals(fullPath, Path.GetFullPath(CoreService.DefaultDownloadedCorePath), StringComparison.OrdinalIgnoreCase))
            _settings.CoreRepo = "";
        CoreService.SaveSettings(_settings);
        CoreNoticePanel.Visibility = Visibility.Collapsed;
        await RefreshCoreAndAccountAsync();
    }

    private async void BtnRegisterAccount_Click(object sender, RoutedEventArgs e)
    {
        var s = Strings.Instance;
        if (string.IsNullOrEmpty(_corePath) || !File.Exists(_corePath))
        {
            ShowCoreNotice(true, s.CoreNotSelectedTitle, s.CoreNotSelectedMsg);
            return;
        }

        BtnRegisterAccount.IsEnabled = false;
        TxtAccountDetails.Text = s.RegisteringAccount;
        try
        {
            var acc = await CoreService.RegisterAccountAsync(_corePath, _accountPath, CancellationToken.None);
            _account = acc;
            await RefreshCoreAndAccountAsync();
            ShowCoreNotice(false, s.AccountRegisteredTitle, s.AccountRegisteredMsg(acc.ID));
        }
        catch (Exception ex)
        {
            ShowCoreNotice(true, s.RegisterErrorTitle, ex.Message);
            await RefreshCoreAndAccountAsync();
        }
        finally
        {
            BtnRegisterAccount.IsEnabled = true;
        }
    }

    private void SettingsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.MinimizeToTray = TglMinimizeToTray.IsChecked == true;
        _settings.MinimizeToTrayWithProxy = TglMinimizeToTrayWithProxy.IsChecked == true;
        _settings.WarnOnCloseWithProxy = TglWarnOnExit.IsChecked == true;
        _settings.DefaultExitAction = "";
        if (_trayMinimizeToTrayItem != null)
            _trayMinimizeToTrayItem.Checked = _settings.MinimizeToTray;
        CoreService.SaveSettings(_settings);
    }

    private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (CmbLanguage.SelectedItem is ComboBoxItem item && item.Tag is string lang)
        {
            _settings.Language = lang;
            CoreService.SaveSettings(_settings);
            Strings.Instance.SetLanguage(lang);
            ApplyLanguageToDynamicControls();
        }
    }

    private void ApplyLanguageToDynamicControls()
    {
        var s = Strings.Instance;
        if (_trayShowItem != null) _trayShowItem.Text = s.TrayShow;
        if (_trayStopProxyItem != null) _trayStopProxyItem.Text = s.TrayStopProxy;
        if (_trayMinimizeToTrayItem != null) _trayMinimizeToTrayItem.Text = s.TrayMinimizeOption;
        if (_trayExitItem != null) _trayExitItem.Text = s.TrayExit;
        if (_trayIcon != null) _trayIcon.Text = s.TrayTooltip;

        TxtScannerPageTitle.Text = _isAdvancedScanMode ? s.AdvancedTitle : s.ScannerTitle;
        UpdateHeroSubtitle();
        SetScanControlsEnabled(_scanCts == null);
        BtnToggleScanLogs.Content = ScanLogsPanel.Visibility == Visibility.Visible ? s.BtnHideLog : s.BtnShowLog;
        UpdateCoreAndAccountLabels();
        RefreshBestFlags();
        foreach (var item in _endpoints)
            item.RefreshLocale();
        UpdateSocksUI(_socksProcess != null && !_socksProcess.HasExited);
        TxtServersSubtitle.Text = s.ServersSubtitle;
        UpdateLastScanTimeUI();
        if (_endpoints.Count == 0 && ScanProgressCard.Visibility != Visibility.Visible)
            TxtEmptyTable.Text = s.EmptyTableInitial;
        if (_scanCts == null && ScanProgressCard.Visibility != Visibility.Visible)
            TxtScanPhase.Text = s.ScanPreparing;
    }

    private void CmbProto_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbProto.SelectedItem is ComboBoxItem item && item.Tag is string proto)
        {
            _selectedProto = proto;
            UpdateHeroSubtitle();
        }
    }

    private Awg31Options GetEffectiveAwg31Options() =>
        _isAdvancedScanMode
            ? Awg31Options.ParseFromCliArgs(SplitCliArgs(TxtCustomFlags.Text))
            : _settings.Awg31;

    private void SyncAwg31FromUI()
    {
        var a = _settings.Awg31;
        a.Padding = TxtAwg31Padding.Text.Trim();
        a.RekeyAfter = TxtAwg31RekeyAfter.Text.Trim();
        a.RekeyTimeout = TxtAwg31RekeyTimeout.Text.Trim();
        a.RejectAfter = TxtAwg31RejectAfter.Text.Trim();
        a.KeepaliveTimeout = TxtAwg31KeepaliveTimeout.Text.Trim();
        a.MaxHandshakes = TxtAwg31MaxHandshakes.Text.Trim();
        a.DisableCookies = ChkAwg31DisableCookies.IsChecked == true;
        a.RandomTrailers = ChkAwg31RandomTrailers.IsChecked == true;
    }

    private void Awg31Option_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        SyncAwg31FromUI();
        UpdateHeroSubtitle();
    }

    private void Awg31Option_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SyncAwg31FromUI();
        CoreService.SaveSettings(_settings);
    }

    private void Awg31Option_ToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SyncAwg31FromUI();
        UpdateHeroSubtitle();
        CoreService.SaveSettings(_settings);
    }

    private void ScanOption_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isInitializing)
        {
            bool ex = ChkDme.IsChecked == true;
            if (_settings.ExcludeNodeEnabled != ex)
            {
                _settings.ExcludeNodeEnabled = ex;
                CoreService.SaveSettings(_settings);
            }
        }
        UpdateHeroSubtitle();
    }

    private void TxtExcludeNode_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.ExcludedNode = TxtExcludeNode.Text.Trim();
        UpdateHeroSubtitle();
    }

    private void TxtExcludeNode_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.ExcludedNode = TxtExcludeNode.Text.Trim();
        CoreService.SaveSettings(_settings);
    }

    private void TxtCustomFlags_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.CustomScanFlags = TxtCustomFlags.Text.Trim();
        UpdateHeroSubtitle();
    }

    private void TxtCustomFlags_LostFocus(object sender, RoutedEventArgs e) =>
        CoreService.SaveSettings(_settings);

    internal static List<string> ParseExcludedNodes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return raw.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void UpdateHeroSubtitle()
    {
        if (TxtHeroSubtitle == null) return;
        var s = Strings.Instance;
        if (_isAdvancedScanMode)
        {
            var custom = TxtCustomFlags?.Text.Trim();
            TxtHeroSubtitle.Text = string.IsNullOrEmpty(custom)
                ? s.HeroDefaultFlags
                : s.HeroCustomFlags(custom);
            return;
        }

        if (_settings == null || ChkDme == null || ChkPing == null || ChkFull == null || ChkSpeed == null)
            return;

        var isMasque = _selectedProto.StartsWith("masque", StringComparison.OrdinalIgnoreCase);
        var isAwg31 = string.Equals(_selectedProto, "awg31", StringComparison.OrdinalIgnoreCase);
        if (CardAwg31 != null)
            CardAwg31.Visibility = isAwg31 ? Visibility.Visible : Visibility.Collapsed;

        var awg31Parts = _settings.Awg31.FormatSummaryParts();
        if (TxtAwg31CardDesc != null)
            TxtAwg31CardDesc.Text = awg31Parts.Count > 0
                ? s.Awg31CardActiveDesc(string.Join(", ", awg31Parts))
                : s.Awg31CardDefaultDesc;

        if (CardExcludeNode != null)
        {
            CardExcludeNode.IsEnabled = !isMasque;
            CardExcludeNode.Opacity = isMasque ? 0.55 : 1.0;
            CardExcludeNode.ToolTip = isMasque ? s.ExcludeNodeMasqueTooltip : null;
        }

        var protoLabel = _selectedProto switch
        {
            "awg31" => "AmneziaWG 3.1",
            "wg" => "WireGuard",
            "masque" => "MASQUE (QUIC)",
            "masque-h2" => "MASQUE H2 (TCP)",
            _ => "AmneziaWG"
        };
        var flags = new List<string> { s.HeroProto(protoLabel) };
        if (isAwg31 && awg31Parts.Count > 0)
            flags.AddRange(awg31Parts);
        if (!isMasque && ChkDme.IsChecked == true)
        {
            var nodes = ParseExcludedNodes(TxtExcludeNode?.Text);
            if (nodes.Count > 0)
                flags.Add(s.HeroExcludeNode(string.Join(", ", nodes)));
        }
        if (ChkPing.IsChecked == true) flags.Add(s.HeroPing);
        if (ChkFull.IsChecked == true) flags.Add(s.HeroFullScan);
        if (ChkSpeed.IsChecked == true) flags.Add(s.HeroSpeed);
        TxtHeroSubtitle.Text = string.Join(" · ", flags);
    }

    private void SetScanControlsEnabled(bool enabled)
    {
        var s = Strings.Instance;
        BtnScan.Content = enabled ? s.BtnFindServers : s.BtnStopScan;
        BtnScan.Icon = new SymbolIcon(enabled ? SymbolRegular.Play24 : SymbolRegular.Stop24);
        BtnScan.Appearance = enabled ? ControlAppearance.Primary : ControlAppearance.Danger;

        BtnRefreshServers.Content = enabled ? s.BtnFindServers : s.BtnStopScan;
        BtnRefreshServers.Appearance = enabled ? ControlAppearance.Primary : ControlAppearance.Danger;

        TxtHeroTitle.Text = enabled ? s.HeroReady : s.HeroScanning;
        ScanOptionsCards.IsEnabled = enabled;
        AdvancedOptionsCard.IsEnabled = enabled;
        NavScanner.IsEnabled = enabled || !_isAdvancedScanMode;
        NavAdvanced.IsEnabled = enabled || _isAdvancedScanMode;
    }

    private void BtnToggleScanLogs_Click(object sender, RoutedEventArgs e)
    {
        bool show = ScanLogsPanel.Visibility != Visibility.Visible;
        ScanLogsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        BtnToggleScanLogs.Content = show ? Strings.Instance.BtnHideLog : Strings.Instance.BtnShowLog;
        if (show)
            TxtScanLogs.ScrollToEnd();
    }

    private void AppendScanLog(string line)
    {
        TxtScanLogs.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        if (ScanLogsPanel.Visibility == Visibility.Visible)
            TxtScanLogs.ScrollToEnd();
    }

    private async void BtnScan_Click(object sender, RoutedEventArgs e)
    {
        var s = Strings.Instance;
        if (_scanCts != null)
        {
            _scanStoppedByUser = true;
            StopScanProcess();
            SetScanControlsEnabled(true);
            TxtScanPhase.Text = s.StoppedByUserPhase(_endpoints.Count);
            AppendScanLog(s.StoppedByUserLog);
            return;
        }

        await StartScanAsync();
    }

    private async Task StartScanAsync()
    {
        if (_scanCts != null) return;
        var s = Strings.Instance;
        if (string.IsNullOrEmpty(_corePath) || !File.Exists(_corePath))
        {
            ShowCoreNotice(true, s.StatusPillMissing, s.CoreMissingScanMsg);
            return;
        }

        if (!_isAdvancedScanMode &&
            string.Equals(_selectedProto, "awg31", StringComparison.OrdinalIgnoreCase))
        {
            SyncAwg31FromUI();
            if (_settings.Awg31.FindFirstInvalidRange() is { } badRange)
            {
                ScanProgressCard.Visibility = Visibility.Visible;
                PbScan.Value = 0;
                ShowScanError(s.Awg31InvalidRangeErr(badRange.Flag, badRange.Value));
                return;
            }
        }

        _endpoints.Clear();
        _endpointMap.Clear();
        _bestEndpoint = null;
        _lastScanTime = null;
        UpdateLastScanTimeUI();
        RefreshBestFlags();
        TxtEmptyTable.Text = s.TableScanning;

        ScanProgressCard.Visibility = Visibility.Visible;
        ScanErrorBox.Visibility = Visibility.Collapsed;
        TxtScanPhase.Text = s.ScanPreparing;
        TxtScanCount.Text = s.ScanFoundCount(0);
        PbScan.Value = 5;
        TxtScanLogs.Clear();

        _scanStoppedByUser = false;
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        SetScanControlsEnabled(false);

        bool gotDone = false;
        bool hasError = false;
        string scanProto = _selectedProto;

        try
        {
            if (CoreService.ReadAccount(_accountPath) == null)
            {
                TxtScanPhase.Text = s.RegisteringAccount;
                AppendScanLog(s.RegisteringNewAccount);
                await CoreService.EnsureAccountAsync(_corePath, _accountPath, ct);
                await RefreshCoreAndAccountAsync();
                AppendScanLog(s.AccountSavedLog(_accountPath));
            }

            var psi = new ProcessStartInfo(_corePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("scan");
            psi.ArgumentList.Add("-stream-json");

            CoreService.SaveSettings(_settings);
            var excludedNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_isAdvancedScanMode)
            {
                var customArgs = SplitCliArgs(TxtCustomFlags.Text);
                int startIdx = (customArgs.Count > 0 && customArgs[0].Equals("scan", StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
                bool hasAccount = false;
                bool hasProto = false;

                for (int i = startIdx; i < customArgs.Count; i++)
                {
                    var arg = customArgs[i];
                    var eqIdx = arg.IndexOf('=');
                    var flagName = (eqIdx >= 0 ? arg[..eqIdx] : arg).TrimStart('-');
                    var flagVal = eqIdx >= 0 ? arg[(eqIdx + 1)..] : (i + 1 < customArgs.Count ? customArgs[i + 1] : null);

                    if (flagName.Equals("stream-json", StringComparison.Ordinal))
                        continue;
                    if (flagName.Equals("a", StringComparison.Ordinal) ||
                        flagName.Equals("account", StringComparison.Ordinal))
                    {
                        hasAccount = true;
                    }
                    if ((flagName.Equals("p", StringComparison.Ordinal) ||
                         flagName.Equals("proto", StringComparison.Ordinal)) &&
                        !string.IsNullOrWhiteSpace(flagVal) &&
                        !flagVal.StartsWith('-'))
                    {
                        hasProto = true;
                        _selectedProto = flagVal;
                    }
                    if (flagName.Equals("exclude-node", StringComparison.Ordinal) &&
                        !string.IsNullOrWhiteSpace(flagVal))
                    {
                        foreach (var node in ParseExcludedNodes(flagVal))
                            excludedNodes.Add(node);
                    }
                    psi.ArgumentList.Add(arg);
                }

                if (!hasProto)
                    _selectedProto = "wg";

                if (!hasAccount)
                {
                    psi.ArgumentList.Add("-a");
                    psi.ArgumentList.Add(_accountPath);
                }
            }
            else
            {
                psi.ArgumentList.Add("-a");
                psi.ArgumentList.Add(_accountPath);
                psi.ArgumentList.Add("-p");
                psi.ArgumentList.Add(_selectedProto);

                bool isMasque = _selectedProto.StartsWith("masque", StringComparison.OrdinalIgnoreCase);
                var nodes = (!isMasque && ChkDme.IsChecked == true) ? ParseExcludedNodes(TxtExcludeNode.Text) : [];
                foreach (var node in nodes)
                    excludedNodes.Add(node);

                if (string.Equals(_selectedProto, "awg31", StringComparison.OrdinalIgnoreCase))
                    _settings.Awg31.AppendCliArgs(psi.ArgumentList);
                if (ChkPing.IsChecked == true) psi.ArgumentList.Add("-P");
                if (nodes.Count > 0) { psi.ArgumentList.Add("-exclude-node"); psi.ArgumentList.Add(string.Join(",", nodes)); }
                if (ChkFull.IsChecked == true) psi.ArgumentList.Add("-f");
                if (ChkSpeed.IsChecked == true) psi.ArgumentList.Add("-speed");
            }

            scanProto = _selectedProto;
            AppendScanLog($"$ warpscout {string.Join(' ', psi.ArgumentList)}");

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException(s.ScanStartErr);
            _scanProcess = proc;

            var stderrLines = new List<string>();
            var errTask = Task.Run(async () =>
            {
                while (await proc.StandardError.ReadLineAsync() is { } line)
                {
                    var t = line.Trim();
                    if (!string.IsNullOrEmpty(t))
                    {
                        lock (stderrLines) stderrLines.Add(t);
                        _ = Dispatcher.BeginInvoke(() => AppendScanLog(t));
                    }
                }
            }, CancellationToken.None);

            while (await proc.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                if (!trimmed.StartsWith('{'))
                {
                    AppendScanLog(trimmed);
                    continue;
                }

                ScanStreamEvent? ev;
                try { ev = JsonSerializer.Deserialize<ScanStreamEvent>(trimmed); }
                catch { AppendScanLog(trimmed); continue; }
                if (ev == null) continue;

                if (ev.Event == "progress")
                {
                    var pct = ev.Total > 0 ? Math.Min(100.0, 100.0 * ev.Done / ev.Total) : 10.0;
                    PbScan.Value = pct;
                    var phase = ev.Phase ?? "";
                    var phaseName = phase switch
                    {
                        "ports" => s.PhasePorts,
                        "speed" => s.PhaseSpeed,
                        _ => s.PhaseTunnels,
                    };
                    TxtScanPhase.Text = ev.Total > 0 ? s.PhaseProgress(phaseName, ev.Done, ev.Total) : phaseName;
                    if (ev.Done == 0)
                        AppendScanLog(s.LogPhaseStart(phase, ev.Total));
                    else if (ev.Total <= 0 || ev.Done == ev.Total || ev.Done % Math.Max(1, ev.Total / 20) == 0)
                        AppendScanLog(s.LogPhaseProgress(phase, ev.Done, ev.Total));
                }
                else if (ev.Event == "endpoint" && ev.Endpoint is { } ep)
                {
                    var nodeLabel = string.IsNullOrWhiteSpace(ep.Node) ? "—" : ep.Node;
                    if (ep.Torn)
                    {
                        AppendScanLog(s.LogEndpointTorn(ep.Endpoint, nodeLabel));
                        continue;
                    }
                    if (excludedNodes.Count > 0 && !string.IsNullOrEmpty(ep.Node) && excludedNodes.Contains(ep.Node))
                        continue;
                    UpsertEndpoint(ep);
                    var speedPart = ep.SpeedMbps is > 0 ? $" speed={ep.SpeedMbps.Value:0.1}Mbps" : "";
                    AppendScanLog(s.LogEndpointFound(ep.Endpoint, ep.NodeCountry ?? "", ep.NodeCity ?? ep.SeenAs ?? "", nodeLabel, ep.PingMs, ep.TunPingMs, ep.LossPct, speedPart));
                }
                else if (ev.Event == "done" && ev.Result is { } res)
                {
                    gotDone = true;
                    if (!string.IsNullOrWhiteSpace(res.Proto))
                    {
                        scanProto = res.Proto;
                        if (_isAdvancedScanMode)
                            _selectedProto = res.Proto;
                    }
                    ApplyDoneEndpoints(res);
                    AppendScanLog(s.LogScanDone(res.Working?.Count ?? res.WorkingCount, string.IsNullOrEmpty(res.Best) ? "—" : res.Best, res.Torn?.Count ?? 0));
                }
                else if (ev.Event == "error")
                {
                    hasError = true;
                    ShowScanError(string.IsNullOrWhiteSpace(ev.Error) ? s.ScanErrorDefault : ev.Error);
                }
            }

            await Task.WhenAll(proc.WaitForExitAsync(CancellationToken.None), errTask);

            if (!hasError && !_scanStoppedByUser && !gotDone && proc.ExitCode != 0)
            {
                hasError = true;
                string errText;
                lock (stderrLines) errText = string.Join(" | ", stderrLines);
                ShowScanError(string.IsNullOrEmpty(errText) ? s.ProcessExitedCode(proc.ExitCode) : errText);
            }
            else if (!_scanStoppedByUser && gotDone)
            {
                PbScan.Value = 100;
                TxtScanPhase.Text = s.ScanDonePhase(_endpoints.Count);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!_scanStoppedByUser)
            {
                hasError = true;
                ShowScanError(ex.Message);
            }
        }
        finally
        {
            _scanProcess = null;
            _scanCts?.Dispose();
            _scanCts = null;
            SetScanControlsEnabled(true);
            if (_endpoints.Count > 0 && !hasError && (gotDone || _scanStoppedByUser))
            {
                _lastScanTime = DateTimeOffset.Now;
                UpdateLastScanTimeUI();
                SaveCurrentScan(scanProto);
            }
            else if (_endpoints.Count == 0 && ScanErrorBox.Visibility != Visibility.Visible)
            {
                TxtEmptyTable.Text = s.EmptyTableAfterScan;
            }
        }
    }

    private void UpsertEndpoint(ScanEndpoint ep, bool refreshFlags = true, bool preserveOrder = false)
    {
        if (string.IsNullOrWhiteSpace(ep.Endpoint)) return;
        if (_endpointMap.TryGetValue(ep.Endpoint, out var existing))
        {
            existing.PingMs = ep.PingMs ?? 0;
            existing.TunPingMs = ep.TunPingMs ?? 0;
            existing.Loss = ep.LossPct;
            existing.SpeedMbps = ep.SpeedMbps ?? 0;
            if (!preserveOrder)
            {
                _endpoints.Remove(existing);
                InsertSorted(existing);
            }
        }
        else
        {
            var item = EndpointItem.FromScanEndpoint(ep, _socksProcess != null && ep.Endpoint == _socksEndpoint);
            _endpointMap[ep.Endpoint] = item;
            if (preserveOrder)
                _endpoints.Add(item);
            else
                InsertSorted(item);
        }
        if (refreshFlags)
            RefreshBestFlags();
    }

    private void ApplyDoneEndpoints(ScanResult res)
    {
        if (res.Working == null) return;
        _endpoints.Clear();
        _endpointMap.Clear();
        foreach (var w in res.Working)
            UpsertEndpoint(w, refreshFlags: false, preserveOrder: true);
        RefreshBestFlags(res.Best ?? "");
    }

    internal static int CompareEndpoints(EndpointItem a, EndpointItem b)
    {
        var lossCmp = (a.Loss ?? 0).CompareTo(b.Loss ?? 0);
        return lossCmp != 0 ? lossCmp : a.EffectivePingMs.CompareTo(b.EffectivePingMs);
    }

    private void InsertSorted(EndpointItem item)
    {
        int idx = 0;
        while (idx < _endpoints.Count && CompareEndpoints(_endpoints[idx], item) <= 0)
            idx++;
        _endpoints.Insert(idx, item);
    }

    private void RefreshBestFlags(string? explicitBest = null)
    {
        var s = Strings.Instance;
        if (explicitBest != null)
            _bestEndpoint = string.IsNullOrWhiteSpace(explicitBest) ? null : explicitBest;
        var hasExplicitBest = !string.IsNullOrEmpty(_bestEndpoint) && _endpoints.Any(x => x.Endpoint == _bestEndpoint);
        for (int i = 0; i < _endpoints.Count; i++)
            _endpoints[i].IsBest = hasExplicitBest ? _endpoints[i].Endpoint == _bestEndpoint : i == 0;

        EmptyTablePanel.Visibility = _endpoints.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        NavServersBadge.Visibility = !_isSidebarCollapsed && _endpoints.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtNavServersBadge.Text = _endpoints.Count.ToString();
        TxtScanCount.Text = s.ScanFoundCount(_endpoints.Count);

        var best = _endpoints.FirstOrDefault(x => x.IsBest) ?? _endpoints.FirstOrDefault();
        if (best != null)
        {
            BestServerCard.Visibility = Visibility.Visible;
            TxtBestTitle.Text = s.BestServerTitle(best.CountryBadge, best.DisplayLocation, best.DisplayNode);
            TxtBestSubtitle.Text = s.BestServerSubtitle(best.Endpoint, best.DisplayPing, best.DisplayLoss);
            BtnBestProxy.Content = best.IsProxied ? s.BtnProxyStop : s.BtnProxyStart;
        }
        else
        {
            BestServerCard.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowScanError(string message)
    {
        var s = Strings.Instance;
        ScanErrorBox.Visibility = Visibility.Visible;
        TxtScanError.Text = message;
        TxtScanPhase.Text = s.ScanStoppedErrorPhase;
        AppendScanLog($"[{s.LogErrorPrefix}] {message}");
    }

    private void StopScanProcess()
    {
        try { _scanCts?.Cancel(); } catch { }
        try
        {
            if (_scanProcess != null && !_scanProcess.HasExited)
                _scanProcess.Kill(entireProcessTree: true);
        }
        catch { }
        _scanProcess = null;
    }

    private void BtnCopyEndpoint_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Wpf.Ui.Controls.Button btn && btn.Content is string ep)
        {
            try { System.Windows.Clipboard.SetText(ep); } catch { }
            TxtServersSubtitle.Text = Strings.Instance.CopiedEndpoint(ep);
        }
    }

    private void BtnBestDownload_Click(object sender, RoutedEventArgs e)
    {
        var best = _endpoints.FirstOrDefault(x => x.IsBest) ?? _endpoints.FirstOrDefault();
        if (best != null) ExportConfigFor(best);
    }

    private async void BtnBestProxy_Click(object sender, RoutedEventArgs e)
    {
        var best = _endpoints.FirstOrDefault(x => x.IsBest) ?? _endpoints.FirstOrDefault();
        if (best != null) await ToggleSocksForAsync(best);
    }

    private void BtnDownloadConf_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Wpf.Ui.Controls.Button btn && btn.Tag is EndpointItem ep)
            ExportConfigFor(ep);
    }

    private void ExportConfigFor(EndpointItem ep)
    {
        var s = Strings.Instance;
        try
        {
            var awg31 = string.Equals(_selectedProto, "awg31", StringComparison.OrdinalIgnoreCase)
                ? GetEffectiveAwg31Options()
                : null;
            var confText = CoreService.ExportConfig(_accountPath, ep.Endpoint, _selectedProto, awg31);
            var isMasque = _selectedProto.StartsWith("masque", StringComparison.OrdinalIgnoreCase);
            var nodeTag = string.IsNullOrWhiteSpace(ep.Node) ? "warp" : ep.Node;
            var host = ep.Endpoint.StartsWith('[') && ep.Endpoint.IndexOf(']') is int end and > 1
                ? ep.Endpoint[1..end]
                : (ep.Endpoint.LastIndexOf(':') is int colon and > 0 && ep.Endpoint.IndexOf(':') == colon
                    ? ep.Endpoint[..colon]
                    : ep.Endpoint);
            var ipTag = host.Replace(':', '-');
            var baseName = string.IsNullOrWhiteSpace(ipTag) ? $"warp-{nodeTag}" : $"warp-{nodeTag}-{ipTag}";
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = isMasque ? $"{baseName}.json" : $"{baseName}.conf",
                DefaultExt = isMasque ? ".json" : ".conf",
                Filter = isMasque ? "MASQUE Config (*.json)|*.json" : "WireGuard Config (*.conf)|*.conf",
            };
            if (dlg.ShowDialog() == true)
            {
                File.WriteAllText(dlg.FileName, confText);
                ShowCoreNotice(false, s.ConfigSavedTitle, dlg.FileName);
            }
        }
        catch (Exception ex)
        {
            ShowCoreNotice(true, s.ConfigExportErrTitle, ex.Message);
        }
    }

    private async void BtnToggleSocks_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Wpf.Ui.Controls.Button btn && btn.Tag is EndpointItem ep)
            await ToggleSocksForAsync(ep);
    }

    private async Task ToggleSocksForAsync(EndpointItem ep)
    {
        if (_socksProcess != null && _socksEndpoint == ep.Endpoint)
        {
            StopSocksProcess();
            return;
        }
        await StartSocksAsync(ep.Endpoint, _selectedProto);
    }

    private async Task StartSocksAsync(string endpoint, string proto)
    {
        var s = Strings.Instance;
        if (string.IsNullOrEmpty(_corePath) || !File.Exists(_corePath))
        {
            ShowCoreNotice(true, s.StatusPillMissing, s.CoreMissingSocksMsg);
            return;
        }

        Awg31Options? awg31 = null;
        if (string.Equals(proto, "awg31", StringComparison.OrdinalIgnoreCase))
        {
            awg31 = GetEffectiveAwg31Options();
            if (awg31.FindFirstInvalidRange() is { } badRange)
            {
                ShowCoreNotice(true, s.SocksStartErr, s.Awg31InvalidRangeErr(badRange.Flag, badRange.Value));
                return;
            }
        }

        StopSocksProcess();
        try
        {
            await CoreService.EnsureAccountAsync(_corePath, _accountPath, CancellationToken.None);
            var psi = new ProcessStartInfo(_corePath)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("socks");
            psi.ArgumentList.Add("-e"); psi.ArgumentList.Add(endpoint);
            psi.ArgumentList.Add("-p"); psi.ArgumentList.Add(proto);
            psi.ArgumentList.Add("-P"); psi.ArgumentList.Add("1080");
            psi.ArgumentList.Add("-a"); psi.ArgumentList.Add(_accountPath);
            awg31?.AppendCliArgs(psi.ArgumentList);

            var proc = Process.Start(psi) ?? throw new InvalidOperationException(s.SocksStartErr);
            var errTask = proc.StandardError.ReadToEndAsync();
            await Task.Delay(350);
            if (proc.HasExited)
            {
                var err = (await errTask).Trim();
                int exitCode = proc.ExitCode;
                proc.Dispose();
                throw new InvalidOperationException(string.IsNullOrEmpty(err) ? s.SocksExitedCode(exitCode) : err);
            }

            _socksProcess = proc;
            _socksEndpoint = endpoint;
            _socksProto = proto;
            UpdateSocksUI(true);

            _ = Task.Run(async () =>
            {
                try { await proc.WaitForExitAsync(); } catch { }
                try
                {
                    if (!Dispatcher.HasShutdownStarted)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (_socksProcess == proc)
                            {
                                _socksProcess = null;
                                _socksEndpoint = "";
                                UpdateSocksUI(false);
                            }
                        });
                    }
                }
                catch { }
                proc.Dispose();
            });
        }
        catch (Exception ex)
        {
            ShowCoreNotice(true, s.SocksStartErr, ex.Message);
            UpdateSocksUI(false);
        }
    }

    private void BtnStopSocks_Click(object sender, RoutedEventArgs e) => StopSocksProcess();

    private void StopSocksProcess()
    {
        try
        {
            if (_socksProcess != null && !_socksProcess.HasExited)
                _socksProcess.Kill(entireProcessTree: true);
        }
        catch { }
        _socksProcess = null;
        _socksEndpoint = "";
        UpdateSocksUI(false);
    }

    private void UpdateSocksUI(bool running)
    {
        var s = Strings.Instance;
        SocksBanner.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        TxtSocksBanner.Text = s.SocksBannerText(_socksEndpoint, _socksProto);
        foreach (var item in _endpoints)
            item.IsProxied = running && item.Endpoint == _socksEndpoint;

        var best = _endpoints.FirstOrDefault(x => x.IsBest) ?? _endpoints.FirstOrDefault();
        if (best != null)
            BtnBestProxy.Content = best.IsProxied ? s.BtnProxyStop : s.BtnProxyStart;
    }

    internal static CloseAction ResolveCloseAction(AppSettings settings, bool proxyActive)
    {
        if (proxyActive)
        {
            if (settings.WarnOnCloseWithProxy)
                return CloseAction.Prompt;
            if (settings.MinimizeToTray || settings.MinimizeToTrayWithProxy)
                return CloseAction.MinimizeToTray;
            return CloseAction.Exit;
        }

        return settings.MinimizeToTray ? CloseAction.MinimizeToTray : CloseAction.Exit;
    }

    private void MinimizeToTrayWithNotice(bool proxyActive)
    {
        Hide();
        if (_trayIcon == null) return;
        var s = Strings.Instance;
        var body = proxyActive ? s.TrayMinimizedProxyBody : s.TrayMinimizedBody;
        try
        {
            if (TryShowSilentBalloonTip(_trayIcon, s.TrayMinimizedTitle, body, 4000))
                return;

            _trayIcon.BalloonTipTitle = s.TrayMinimizedTitle;
            _trayIcon.BalloonTipText = body;
            _trayIcon.BalloonTipIcon = WinForms.ToolTipIcon.Info;
            _trayIcon.ShowBalloonTip(4000);
        }
        catch { }
    }

    private const uint NimModify = 0x00000001;
    private const uint NifInfo = 0x00000010;
    private const uint NiifInfo = 0x00000001;
    private const uint NiifNoSound = 0x00000010;

    internal static int NotifyIconDataSize => Marshal.SizeOf<NotifyIconData>();

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NotifyIconData lpData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private static bool TryShowSilentBalloonTip(WinForms.NotifyIcon trayIcon, string title, string text, uint timeoutMs = 4000)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(WinForms.NotifyIcon);
        var windowObj = (type.GetField("_window", flags) ?? type.GetField("window", flags))?.GetValue(trayIcon);
        var idObj = (type.GetField("_id", flags) ?? type.GetField("id", flags))?.GetValue(trayIcon);
        if (windowObj is not WinForms.NativeWindow nativeWindow || nativeWindow.Handle == IntPtr.Zero || idObj == null)
            return false;

        var safeTitle = title ?? "";
        var safeText = text ?? "";
        var nid = new NotifyIconData
        {
            cbSize = NotifyIconDataSize,
            hWnd = nativeWindow.Handle,
            uID = Convert.ToUInt32(idObj),
            uFlags = NifInfo,
            szTip = "",
            szInfoTitle = safeTitle.Length > 63 ? safeTitle[..63] : safeTitle,
            szInfo = safeText.Length > 255 ? safeText[..255] : safeText,
            uTimeoutOrVersion = timeoutMs,
            dwInfoFlags = NiifInfo | NiifNoSound,
        };
        return Shell_NotifyIcon(NimModify, ref nid);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_isExiting)
        {
            CleanupAll();
            base.OnClosing(e);
            return;
        }

        bool proxyActive = _socksProcess != null && !_socksProcess.HasExited;
        switch (ResolveCloseAction(_settings, proxyActive))
        {
            case CloseAction.Prompt:
                e.Cancel = true;
                ChkRememberExitChoice.IsChecked = false;
                ExitWarningOverlay.Visibility = Visibility.Visible;
                return;

            case CloseAction.MinimizeToTray:
                e.Cancel = true;
                MinimizeToTrayWithNotice(proxyActive);
                return;
        }

        CleanupAll();
        base.OnClosing(e);
    }

    private void BtnExitCancel_Click(object sender, RoutedEventArgs e) =>
        ExitWarningOverlay.Visibility = Visibility.Collapsed;

    private void BtnExitMinimize_Click(object sender, RoutedEventArgs e)
    {
        if (ChkRememberExitChoice.IsChecked == true)
        {
            _settings.WarnOnCloseWithProxy = false;
            _settings.MinimizeToTrayWithProxy = true;
            _settings.DefaultExitAction = "minimize";
            SyncTraySettingsUI();
            CoreService.SaveSettings(_settings);
        }
        ExitWarningOverlay.Visibility = Visibility.Collapsed;
        MinimizeToTrayWithNotice(_socksProcess != null && !_socksProcess.HasExited);
    }

    private void BtnExitConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (ChkRememberExitChoice.IsChecked == true)
        {
            _settings.WarnOnCloseWithProxy = false;
            _settings.MinimizeToTrayWithProxy = false;
            _settings.MinimizeToTray = false;
            _settings.DefaultExitAction = "exit";
            SyncTraySettingsUI();
            CoreService.SaveSettings(_settings);
        }
        ExitWarningOverlay.Visibility = Visibility.Collapsed;
        ForceShutdown();
    }

    private void ForceShutdown()
    {
        _isExiting = true;
        CleanupAll();
        System.Windows.Application.Current.Shutdown();
    }

    private void CleanupAll()
    {
        if (!_isInitializing)
        {
            SyncAwg31FromUI();
            CoreService.SaveSettings(_settings);
        }
        _githubCornerHoverTimer.Stop();
        StopScanProcess();
        StopSocksProcess();
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        _trayIconHandle?.Dispose();
        _trayIconHandle = null;
    }

    internal static List<string> SplitCliArgs(string? input)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(input)) return result;

        var current = new System.Text.StringBuilder();
        char? quote = null;
        foreach (var ch in input)
        {
            if (quote != null)
            {
                if (ch == quote)
                    quote = null;
                else
                    current.Append(ch);
            }
            else if (ch is '"' or '\'')
            {
                quote = ch;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }
        if (current.Length > 0)
            result.Add(current.ToString());
        return result;
    }
}

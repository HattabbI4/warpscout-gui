using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Controls;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace WarpscoutGui;

public partial class MainWindow : FluentWindow
{
    private readonly AppSettings _settings;
    private readonly ObservableCollection<EndpointItem> _endpoints = [];
    private readonly Dictionary<string, EndpointItem> _endpointMap = new(StringComparer.OrdinalIgnoreCase);

    private string _corePath = "";
    private string _coreVersion = "";
    private string _accountPath = "";
    private AccountData? _account;
    private string _selectedProto = "awg";

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

    private WinForms.NotifyIcon? _trayIcon;
    private Drawing.Icon? _trayIconHandle;
    private WinForms.ToolStripMenuItem? _trayShowItem;
    private WinForms.ToolStripMenuItem? _trayStopProxyItem;
    private WinForms.ToolStripMenuItem? _trayExitItem;

    public MainWindow()
    {
        InitializeComponent();
        _settings = CoreService.LoadSettings();

        GridEndpoints.ItemsSource = _endpoints;
        LoadSettingsIntoUI();
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
        TglWarnOnExit.IsChecked = _settings.WarnOnCloseWithProxy;
    }

    private void SetupTray()
    {
        var s = Strings.Instance;
        _trayIconHandle = LoadAppTrayIcon();
        var menu = new WinForms.ContextMenuStrip();
        _trayShowItem = new WinForms.ToolStripMenuItem(s.TrayShow, null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
        _trayStopProxyItem = new WinForms.ToolStripMenuItem(s.TrayStopProxy, null, (_, _) => Dispatcher.Invoke(StopSocksProcess));
        _trayExitItem = new WinForms.ToolStripMenuItem(s.TrayExit, null, (_, _) => Dispatcher.Invoke(ForceShutdown));
        menu.Items.Add(_trayShowItem);
        menu.Items.Add(_trayStopProxyItem);
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

        // First-launch: if core binary is missing, show explicit download progress and lock UI.
        if (string.IsNullOrEmpty(_corePath))
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
    private void BtnGoToScanner_Click(object sender, RoutedEventArgs e) => SelectPage("scanner");

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

            var (savedPath, ver, updated) = await CoreService.DownloadOrUpdateCoreAsync(
                CoreService.DefaultDownloadedCorePath,
                _corePath,
                () => Dispatcher.Invoke(() =>
                {
                    StopScanProcess();
                    StopSocksProcess();
                }),
                progress,
                CancellationToken.None);

            if (updated)
            {
                _settings.CorePath = savedPath;
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

        _settings.CorePath = Path.GetFullPath(path);
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
        _settings.WarnOnCloseWithProxy = TglWarnOnExit.IsChecked == true;
        if (_settings.WarnOnCloseWithProxy)
            _settings.DefaultExitAction = "";
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

    private static List<string> ParseExcludedNodes(string? raw)
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

        if (ChkDme == null || ChkPing == null || ChkFull == null || ChkSpeed == null)
            return;

        var isMasque = _selectedProto.StartsWith("masque", StringComparison.OrdinalIgnoreCase);
        if (CardExcludeNode != null)
        {
            CardExcludeNode.IsEnabled = !isMasque;
            CardExcludeNode.Opacity = isMasque ? 0.55 : 1.0;
            CardExcludeNode.ToolTip = isMasque ? s.ExcludeNodeMasqueTooltip : null;
        }

        var protoLabel = _selectedProto switch
        {
            "wg" => "WireGuard",
            "masque" => "MASQUE (QUIC)",
            "masque-h2" => "MASQUE H2 (TCP)",
            _ => "AmneziaWG"
        };
        var flags = new List<string> { s.HeroProto(protoLabel) };
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
        var s = Strings.Instance;
        if (string.IsNullOrEmpty(_corePath) || !File.Exists(_corePath))
        {
            ShowCoreNotice(true, s.StatusPillMissing, s.CoreMissingScanMsg);
            return;
        }

        _endpoints.Clear();
        _endpointMap.Clear();
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

            var excludedNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_isAdvancedScanMode)
            {
                CoreService.SaveSettings(_settings);
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

                if (ChkPing.IsChecked == true) psi.ArgumentList.Add("-P");
                if (nodes.Count > 0) { psi.ArgumentList.Add("-exclude-node"); psi.ArgumentList.Add(string.Join(",", nodes)); }
                if (ChkFull.IsChecked == true) psi.ArgumentList.Add("-f");
                if (ChkSpeed.IsChecked == true) psi.ArgumentList.Add("-speed");
            }

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

            bool gotDone = false;
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
                    var phaseName = ev.Phase == "speed" ? s.PhaseSpeed : s.PhaseScan;
                    TxtScanPhase.Text = ev.Total > 0 ? s.PhaseProgress(phaseName, ev.Done, ev.Total) : phaseName;
                    if (ev.Done == 0)
                        AppendScanLog(s.LogPhaseStart(ev.Phase, ev.Total));
                    else if (ev.Total <= 0 || ev.Done == ev.Total || ev.Done % Math.Max(1, ev.Total / 20) == 0)
                        AppendScanLog(s.LogPhaseProgress(ev.Phase, ev.Done, ev.Total));
                }
                else if (ev.Event == "endpoint")
                {
                    if (excludedNodes.Count > 0 && excludedNodes.Contains(ev.Node))
                        continue;
                    UpsertEndpoint(ev);
                    var speedPart = ev.SpeedMbps > 0 ? $" speed={ev.SpeedMbps:0.1}Mbps" : "";
                    AppendScanLog(s.LogEndpointFound(ev.Endpoint, ev.Country, ev.City, ev.Node, ev.PingMs, ev.TunPingMs, ev.Loss, speedPart));
                }
                else if (ev.Event == "done")
                {
                    gotDone = true;
                    ApplyDoneEndpoints(ev);
                    AppendScanLog(s.LogScanDone(ev.Working?.Count ?? _endpoints.Count, string.IsNullOrEmpty(ev.Best) ? "—" : ev.Best));
                }
                else if (ev.Event == "error")
                {
                    ShowScanError(string.IsNullOrWhiteSpace(ev.Error) ? s.ScanErrorDefault : ev.Error);
                }
            }

            await Task.WhenAll(proc.WaitForExitAsync(CancellationToken.None), errTask);

            if (!_scanStoppedByUser && !gotDone && proc.ExitCode != 0)
            {
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
            if (!_scanStoppedByUser) ShowScanError(ex.Message);
        }
        finally
        {
            _scanProcess = null;
            _scanCts?.Dispose();
            _scanCts = null;
            SetScanControlsEnabled(true);
            if (_endpoints.Count == 0 && ScanErrorBox.Visibility != Visibility.Visible)
                TxtEmptyTable.Text = s.EmptyTableAfterScan;
        }
    }

    private void UpsertEndpoint(ScanStreamEvent ev, bool refreshFlags = true)
    {
        if (string.IsNullOrWhiteSpace(ev.Endpoint)) return;
        if (_endpointMap.TryGetValue(ev.Endpoint, out var existing))
        {
            existing.PingMs = ev.PingMs;
            existing.TunPingMs = ev.TunPingMs;
            existing.Loss = ev.Loss;
            existing.SpeedMbps = ev.SpeedMbps;
            _endpoints.Remove(existing);
            InsertSorted(existing);
        }
        else
        {
            var item = new EndpointItem
            {
                Endpoint = ev.Endpoint,
                Node = ev.Node,
                Country = ev.Country,
                City = ev.City,
                Location = ev.Location,
                PingMs = ev.PingMs,
                TunPingMs = ev.TunPingMs,
                Loss = ev.Loss,
                SpeedMbps = ev.SpeedMbps,
                IsProxied = _socksProcess != null && ev.Endpoint == _socksEndpoint,
            };
            _endpointMap[ev.Endpoint] = item;
            InsertSorted(item);
        }
        if (refreshFlags)
            RefreshBestFlags();
    }

    private void ApplyDoneEndpoints(ScanStreamEvent ev)
    {
        if (ev.Working == null) return;
        _endpoints.Clear();
        _endpointMap.Clear();
        foreach (var w in ev.Working)
            UpsertEndpoint(w, refreshFlags: false);
        RefreshBestFlags(ev.Best);
    }

    private void InsertSorted(EndpointItem item)
    {
        int idx = 0;
        while (idx < _endpoints.Count && _endpoints[idx].EffectivePingMs <= item.EffectivePingMs)
            idx++;
        _endpoints.Insert(idx, item);
    }

    private void RefreshBestFlags(string? explicitBest = null)
    {
        var s = Strings.Instance;
        for (int i = 0; i < _endpoints.Count; i++)
            _endpoints[i].IsBest = !string.IsNullOrEmpty(explicitBest) ? _endpoints[i].Endpoint == explicitBest : i == 0;

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
            var confText = CoreService.ExportConfig(_accountPath, ep.Endpoint, _selectedProto);
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

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_isExiting)
        {
            CleanupAll();
            base.OnClosing(e);
            return;
        }

        bool proxyActive = _socksProcess != null && !_socksProcess.HasExited;
        if (proxyActive)
        {
            if (_settings.WarnOnCloseWithProxy)
            {
                e.Cancel = true;
                ChkRememberExitChoice.IsChecked = false;
                ExitWarningOverlay.Visibility = Visibility.Visible;
                return;
            }
            if (_settings.MinimizeToTray || _settings.DefaultExitAction == "minimize")
            {
                e.Cancel = true;
                Hide();
                return;
            }
        }
        else if (_settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
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
            _settings.MinimizeToTray = true;
            _settings.DefaultExitAction = "minimize";
            TglMinimizeToTray.IsChecked = true;
            TglWarnOnExit.IsChecked = false;
            CoreService.SaveSettings(_settings);
        }
        ExitWarningOverlay.Visibility = Visibility.Collapsed;
        Hide();
    }

    private void BtnExitConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (ChkRememberExitChoice.IsChecked == true)
        {
            _settings.WarnOnCloseWithProxy = false;
            _settings.MinimizeToTray = false;
            _settings.DefaultExitAction = "exit";
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

    private static List<string> SplitCliArgs(string? input)
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

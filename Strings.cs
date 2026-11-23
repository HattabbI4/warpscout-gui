using System.ComponentModel;
using System.Globalization;

namespace WarpscoutGui;

public sealed class Strings : INotifyPropertyChanged
{
    public static Strings Instance { get; } = new();

    private bool _en;

    public bool IsEnglish => _en;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetLanguage(string? lang)
    {
        bool next = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);
        if (_en == next) return;
        _en = next;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private string T(string ru, string en) => _en ? en : ru;

    // Sidebar & Navigation
    public string TooltipCoreSettings => T("Открыть настройки ядра", "Open core settings");
    public string StatusChecking => T("Проверка...", "Checking...");
    public string TooltipToggleSidebar => T("Свернуть или развернуть меню", "Collapse or expand sidebar");
    public string NavScanner => T("Сканер", "Scanner");
    public string NavAdvanced => T("Продвинутый сканер", "Advanced Scanner");
    public string NavServers => T("Серверы", "Servers");
    public string NavSettings => T("Настройки", "Settings");

    // GitHub Star Corner
    public string GithubStarText => T("Спасибо за звезду", "Star on GitHub");
    public string GithubStarTooltip => T(
        "Открыть репозиторий на GitHub и поставить звезду",
        "Open GitHub repository and give it a star");

    // Global Banners
    public string CoreDownloadTitle => T("Обновление / скачивание ядра warpscout.exe", "Updating / downloading warpscout.exe core");
    public string CoreDownloadLockNote => T("Действия заблокированы до завершения", "Actions locked until complete");
    public string CoreDownloadConnecting => T("Подключение к GitHub...", "Connecting to GitHub...");
    public string BtnRetry => T("Повторить", "Retry");
    public string BtnBrowse => T("Обзор...", "Browse...");
    public string BtnHide => T("Скрыть", "Dismiss");
    public string BtnStopSocks => T("Выключить прокси", "Stop Proxy");

    // Scanner Page
    public string ScannerTitle => T("Сканер серверов", "Server Scanner");
    public string AdvancedTitle => T("Продвинутый сканер", "Advanced Scanner");
    public string HeroReady => T("Готов к поиску оптимального сервера", "Ready to find the optimal server");
    public string HeroScanning => T("Идёт поиск рабочих серверов...", "Searching for working servers...");
    public string BtnFindServers => T("Найти серверы", "Find Servers");
    public string BtnStopScan => T("Остановить", "Stop");
    public string ScanPreparing => T("Подготовка...", "Preparing...");
    public string BtnShowLog => T("Показать лог", "Show log");
    public string BtnHideLog => T("Скрыть лог", "Hide log");

    // Best Server Card
    public string BtnBestDownload => T("Скачать .conf", "Save .conf");
    public string BtnProxyStart => T("Прокси :1080", "Proxy :1080");
    public string BtnProxyStop => T("Выкл. прокси", "Stop proxy");
    public string BtnAllServers => T("Все серверы", "All servers");

    // Scan Parameters
    public string ScanParamsHeader => T("Параметры сканирования", "Scan Parameters");
    public string ProtoTitle => T("Протокол подключения", "Connection protocol");
    public string ProtoDesc => T("Выбор протокола туннеля для проверки узлов и создания конфигурации", "Tunnel protocol used to test nodes and generate config files");
    public string ExcludeNodeTitle => T("Исключить узел", "Exclude node");
    public string ExcludeNodeDesc => T("Исключать указанные узлы Cloudflare (например, DME, через запятую)", "Exclude specified Cloudflare nodes (e.g., DME, comma-separated)");
    public string ExcludeNodePlaceholder => "DME";
    public string ExcludeNodeMasqueTooltip => T(
        "Недоступно для MASQUE: в этом протоколе все эндпоинты выходят через один узел",
        "Unavailable for MASQUE: in this protocol all endpoints exit through the same node");
    public string TunPingTitle => T("Пинг внутри туннеля (-P)", "In-tunnel ping (-P)");
    public string TunPingDesc => T("Проверять реальную задержку и потери пакетов до 1.1.1.1 через поднятый туннель", "Measure real latency and packet loss to 1.1.1.1 through the active tunnel");
    public string FullScanTitle => T("Полное сканирование подсетей (-f)", "Full subnet scan (-f)");
    public string FullScanDesc => T("Перебирать все 256 адресов в подсетях вместо быстрой выборки (-sample 5)", "Scan all 256 addresses in subnets instead of quick sampling (-sample 5)");
    public string SpeedTestTitle => T("Тест скорости (-speed)", "Speed test (-speed)");
    public string SpeedTestDesc => T("Замерять скорость скачивания у лучших найденных серверов после скана", "Measure download speed on the best discovered servers after scanning");

    // Advanced Scanner
    public string AdvFlagsTitle => T("Флаги запуска ядра (warpscout scan)", "Core CLI flags (warpscout scan)");
    public string AdvFlagsDesc => T(
        "Введённые флаги заменяют стандартные переключатели и передаются напрямую в warpscout scan (аргументы -stream-json и -a добавляются автоматически)",
        "Entered flags override standard toggles and pass directly to warpscout scan (-stream-json and -a are added automatically)");
    public string AdvFlagsPlaceholder => T("Например: -p awg -P -exclude-node DME -jt 20", "Example: -p awg -P -exclude-node DME -jt 20");
    public string AdvFlagsExamples => T(
        "Примеры: -p awg -P -exclude-node DME  |  -p masque -P  |  -p wg -f -speed -t 3",
        "Examples: -p awg -P -exclude-node DME  |  -p masque -P  |  -p wg -f -speed -t 3");

    // Servers Page
    public string ServersTitle => T("Рабочие серверы", "Working Servers");
    public string ServersSubtitle => T("Нажмите на эндпоинт, чтобы скопировать адрес в буфер обмена", "Click an endpoint to copy its address to clipboard");
    public string LastScanTime(DateTimeOffset scannedAt)
    {
        var local = scannedAt.ToLocalTime();
        return T(
            $"Сканирование: {local.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)}",
            $"Scanned: {local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}");
    }
    public string ColLocation => T("Локация", "Location");
    public string ColNode => T("Нода", "Node");
    public string ColEndpoint => T("Эндпоинт", "Endpoint");
    public string ColPing => T("Пинг", "Ping");
    public string ColLoss => T("Потери", "Loss");
    public string ColSpeed => T("Скорость", "Speed");
    public string ColActions => T("Действия", "Actions");
    public string BtnRowConf => T("⬇ .conf", "⬇ .conf");
    public string EmptyTableInitial => T("Список пуст — запустите сканирование на вкладке «Сканер»", "List is empty — start a scan in the Scanner tab");
    public string BtnGoToScanner => T("Перейти в Сканер", "Go to Scanner");

    // Settings Page
    public string SettingsTitle => T("Настройки", "Settings");
    public string SettingsSubtitle => T("Путь к ядру warpscout.exe, обновления, ключ WARP, язык и системный трей", "Core binary path, updates, WARP key, language, and system tray");
    public string CoreSectionHeader => T("Ядро сканера (warpscout.exe)", "Scanner Core (warpscout.exe)");
    public string CoreBinaryTitle => T("Исполняемый файл ядра", "Core executable");
    public string CoreBinaryDesc => T("Укажите путь или имя файла вручную либо выберите кнопкой «Обзор...»", "Enter the file path manually or pick it with «Browse...»");
    public string CorePathPlaceholder => T("Автопоиск warpscout.exe", "Auto-detect warpscout.exe");
    public string CoreUpdateTitle => T("Обновление ядра с GitHub", "GitHub core updates");
    public string CoreVersionChecking => T("Текущая версия: проверка...", "Current version: checking...");
    public string BtnCheckUpdates => T("Проверить обновления", "Check updates");
    public string AccountSectionHeader => T("Аккаунт Cloudflare WARP", "Cloudflare WARP Account");
    public string AccountKeyTitle => T("Ключ устройства WARP", "WARP device key");
    public string AccountChecking => T("Проверка аккаунта...", "Checking account...");
    public string BtnRegenerateKey => T("Пересоздать ключ", "Regenerate key");
    public string AppSectionHeader => T("Поведение окна, язык и прокси", "Window, Language & Proxy");
    public string LanguageTitle => T("Язык интерфейса", "Interface language");
    public string LanguageDesc => T("Переключение языка интерфейса приложения (RU / EN)", "Switch application UI language (RU / EN)");
    public string TrayTitle => T("Сворачивать в системный трей", "Minimize to system tray");
    public string TrayDesc => T("При нажатии на крестик оставлять приложение работать в трее", "Keep the app running in the system tray when closing the window");
    public string WarnExitTitle => T("Предупреждать при выходе с активным SOCKS5", "Warn on exit when SOCKS5 is active");
    public string WarnExitDesc => T("Показывать окно выбора перед завершением, если запущен прокси", "Ask before exiting if the local proxy is currently running");

    // Exit Warning Modal
    public string ExitModalTitle => T("Активен локальный SOCKS5-прокси", "Local SOCKS5 proxy is active");
    public string ExitModalBody => T(
        "Сейчас запущен прокси 127.0.0.1:1080. Свернуть окно в системный трей (прокси продолжит работать) или выключить его и выйти?",
        "Proxy 127.0.0.1:1080 is currently running. Minimize to system tray (proxy keeps running) or stop it and exit?");
    public string ExitRemember => T("Запомнить мой выбор", "Remember my choice");
    public string BtnCancel => T("Отмена", "Cancel");
    public string BtnExitAndStop => T("Выйти и выключить", "Stop & Exit");
    public string BtnMinimizeTray => T("Свернуть в трей", "Minimize to tray");

    // Table Row Units & Buttons (Models.cs)
    public string UnitMs => T("мс", "ms");
    public string UnitMbps => T("Мбит/с", "Mbps");
    public string RowProxyActive => T("🟢 Активен", "🟢 Active");
    public string RowProxyStart => T("⚡ Прокси :1080", "⚡ Proxy :1080");

    // Tray Menu
    public string TrayShow => T("Показать Warpscout", "Show Warpscout");
    public string TrayStopProxy => T("Выключить SOCKS5 прокси", "Stop SOCKS5 proxy");
    public string TrayExit => T("Выход", "Exit");
    public string TrayTooltip => "Warpscout — Cloudflare WARP";

    // CoreService Messages
    public string SearchingGithubRelease => T("Поиск последнего релиза на GitHub...", "Checking latest release on GitHub...");
    public string GithubApiError(int code, string? reason) => T($"GitHub API вернул HTTP {code} ({reason})", $"GitHub API returned HTTP {code} ({reason})");
    public string CoreUpToDateStatus(string ver) => T($"Актуальная версия: {ver}", $"Up to date: {ver}");
    public string ArchiveNotFound(string arch, string tag) => T($"Архив {arch} не найден в релизе {tag}", $"Archive {arch} not found in release {tag}");
    public string DownloadingAsset(string label) => T($"Скачивание {label}...", $"Downloading {label}...");
    public string DownloadHttpError(int code) => T($"Ошибка скачивания архива: HTTP {code}", $"Archive download failed: HTTP {code}");
    public string DownloadProgressWithTotal(string tag, long read, long total) => T(
        $"Скачивание {tag}: {read / 1048576.0:0.1} / {total / 1048576.0:0.1} МБ ({100.0 * read / total:0}%)",
        $"Downloading {tag}: {read / 1048576.0:0.1} / {total / 1048576.0:0.1} MB ({100.0 * read / total:0}%)");
    public string DownloadProgressNoTotal(string tag, long read) => T(
        $"Скачивание {tag}: {read / 1048576.0:0.1} МБ...",
        $"Downloading {tag}: {read / 1048576.0:0.1} MB...");
    public string ExtractingCore => T("Распаковка warpscout.exe...", "Extracting warpscout.exe...");
    public string CoreNotFoundInArchive => T("В скачанном архиве не найден warpscout.exe", "warpscout.exe not found in downloaded archive");
    public string CoreInstalledStatus(string tag) => T($"Установлено: {tag}", $"Installed: {tag}");
    public string CoreNotFoundErr => T("Не найдено ядро warpscout.exe", "warpscout.exe core not found");
    public string RegisterStartErr => T("Не удалось запустить warpscout register", "Failed to start warpscout register");
    public string RegisterExitErr(int code) => T($"Ошибка регистрации (код {code})", $"Registration failed (exit code {code})");
    public string RegisterReadErr => T("Регистрация завершилась, но файл аккаунта не прочитан", "Registration completed, but account file could not be read");
    public string AccountMissingErr => T("Файл аккаунта WARP не найден", "WARP account file not found");
    public string MasqueMissingErr => T(
        "В файле аккаунта нет устройства MASQUE. Нажмите «Пересоздать ключ» в настройках.",
        "MASQUE device data missing in account file. Click «Regenerate key» in Settings.");

    // MainWindow Dynamic Strings
    public string CoreFoundFallback => T("найдено", "found");
    public string StatusPillReady(string ver) => T($"{ver} · Готов", $"{ver} · Ready");
    public string StatusPillNoKey(string ver) => T($"{ver} · Нет ключа", $"{ver} · No key");
    public string CoreVersionInstalled(string ver, string path) => T($"Текущая версия: {ver} ({path})", $"Current version: {ver} ({path})");
    public string AccountDetailsReady(string id, string ipv4) => $"ID: {id} (IPv4: {ipv4})";
    public string AccountWillAutoRegister => T("Будет зарегистрирован автоматически при первом скане или по кнопке", "Will be registered automatically on first scan or via button");
    public string StatusPillMissing => T("Ядро не найдено", "Core not found");
    public string CoreVersionMissing => T("Ядро не установлено — выберите файл через «Обзор...» или скачайте", "Core not installed — pick a file via «Browse...» or download");
    public string AccountRequiresCore => T("Требуется warpscout.exe для регистрации", "Requires warpscout.exe to register");
    public string CheckingGithubRelease => T("Проверка последнего релиза на GitHub...", "Checking latest release on GitHub...");
    public string CoreReadyTitle => T("Ядро готово к работе", "Core is ready");
    public string CoreReadyMsg(string path, string ver) => T($"Установлен {path} (версия: {ver}).", $"Installed {path} (version: {ver}).");
    public string CoreUpToDateTitle => T("Обновление не требуется", "Already up to date");
    public string CoreUpToDateMsg(string ver) => T($"Установлена актуальная версия ядра ({ver}).", $"Latest core version is already installed ({ver}).");
    public string CoreDownloadErrorTitle => T("Ошибка при скачивании / обновлении ядра", "Core download / update error");
    public string CoreDownloadErrorMsg(string msg) => T(
        $"{msg}\nВы можете повторить попытку или выбрать warpscout.exe вручную через «Обзор...».",
        $"{msg}\nPlease try again or select warpscout.exe manually via «Browse...».");
    public string BrowseCoreTitle => T("Выберите файл ядра warpscout.exe", "Select warpscout.exe core binary");
    public string BrowseCoreFilter => T(
        "Warpscout Core (warpscout.exe)|warpscout.exe|Исполняемые файлы (*.exe)|*.exe|Все файлы (*.*)|*.*",
        "Warpscout Core (warpscout.exe)|warpscout.exe|Executables (*.exe)|*.exe|All files (*.*)|*.*");
    public string CorePathMissingTitle => T("Указанный файл ядра не найден", "Specified core file not found");
    public string CorePathMissingMsg(string path) => T(
        $"Файл «{path}» не существует. Проверьте имя файла или выберите его кнопкой «Обзор...».",
        $"File «{path}» does not exist. Check the file path or choose it via «Browse...».");
    public string CoreNotSelectedTitle => T("Ядро не выбрано", "Core not selected");
    public string CoreNotSelectedMsg => T("Сначала скачайте или выберите warpscout.exe.", "Download or select warpscout.exe first.");
    public string RegisteringAccount => T("Регистрация аккаунта WARP...", "Registering WARP account...");
    public string AccountRegisteredTitle => T("Аккаунт WARP зарегистрирован", "WARP account registered");
    public string AccountRegisteredMsg(string id) => T($"Новый ключ сохранён (ID: {id}).", $"New key saved (ID: {id}).");
    public string RegisterErrorTitle => T("Ошибка регистрации аккаунта", "Account registration failed");

    public string HeroDefaultFlags => T("Флаги по умолчанию ядра warpscout", "Default warpscout core flags");
    public string HeroCustomFlags(string custom) => T($"Флаги: {custom}", $"Flags: {custom}");
    public string HeroProto(string protoLabel) => T($"Протокол {protoLabel}", $"Protocol: {protoLabel}");
    public string HeroExcludeNode(string node) => T($"Исключить узел: {node}", $"Exclude node: {node}");
    public string HeroPing => T("Пинг (-P)", "Ping (-P)");
    public string HeroFullScan => T("Полный скан", "Full scan");
    public string HeroSpeed => T("Скорость", "Speed");

    public string StoppedByUserPhase(int count) => T($"Остановлено пользователем · Найдено: {count}", $"Stopped by user · Found: {count}");
    public string StoppedByUserLog => T("Остановлено пользователем.", "Stopped by user.");
    public string CoreMissingScanMsg => T("Скачайте ядро или укажите путь к warpscout.exe в Настройках.", "Download the core or set the warpscout.exe path in Settings.");
    public string TableScanning => T("Идёт сканирование...", "Scanning...");
    public string RegisteringNewAccount => T("Регистрация нового аккаунта WARP...", "Registering new WARP account...");
    public string AccountSavedLog(string path) => T($"Аккаунт сохранён: {path}", $"Account saved: {path}");
    public string ScanStartErr => T("Не удалось запустить warpscout scan", "Failed to start warpscout scan");
    public string PhaseSpeed => T("Замер скорости...", "Testing speed...");
    public string PhaseScan => T("Поиск открытых портов и проверка туннелей...", "Scanning ports and testing tunnels...");
    public string PhaseProgress(string phaseName, int done, int total) => T(
        $"{phaseName} Проверено {done} из {total}",
        $"{phaseName} Checked {done} of {total}");
    public string LogPhaseStart(string phase, int total) => T(
        $"Фаза {phase}: старт (всего целей: {total})",
        $"Phase {phase}: started (total targets: {total})");
    public string LogPhaseProgress(string phase, int done, int total) => T(
        $"[{phase}] проверено {done}/{total}",
        $"[{phase}] checked {done}/{total}");
    public string LogEndpointFound(string ep, string country, string city, string node, double ping, double tunPing, double loss, string speedPart) => T(
        $"+ НАЙДЕН {ep} | {country} {city} ({node}) | ping={ping:0}ms tun={tunPing:0}ms loss={loss:0}%{speedPart}",
        $"+ FOUND {ep} | {country} {city} ({node}) | ping={ping:0}ms tun={tunPing:0}ms loss={loss:0}%{speedPart}");
    public string LogScanDone(int count, string best) => T(
        $"Готово: рабочих серверов {count}, лучший: {best}",
        $"Done: {count} working servers, best: {best}");
    public string ScanErrorDefault => T("Ошибка сканирования", "Scan error");
    public string ProcessExitedCode(int code) => T($"Процесс завершился с кодом {code}", $"Process exited with code {code}");
    public string ScanDonePhase(int count) => T($"Готово · Найдено рабочих серверов: {count}", $"Done · Working servers found: {count}");
    public string EmptyTableAfterScan => T(
        "Рабочие серверы не найдены. Попробуйте отключить фильтр «Исключить узел» или сменить протокол.",
        "No working servers found. Try disabling «Exclude node» or switching protocol.");

    public string ScanFoundCount(int count) => T($"Найдено: {count}", $"Found: {count}");
    public string BestServerTitle(string badge, string loc, string node) => T(
        $"★ Лучший сервер: {badge} {loc} ({node})",
        $"★ Best server: {badge} {loc} ({node})");
    public string BestServerSubtitle(string ep, string ping, string loss) => T(
        $"{ep} · Пинг: {ping} · Потери: {loss}",
        $"{ep} · Ping: {ping} · Loss: {loss}");
    public string ScanStoppedErrorPhase => T("Остановлено из-за ошибки", "Stopped due to error");
    public string LogErrorPrefix => T("ОШИБКА", "ERROR");
    public string CopiedEndpoint(string ep) => T($"Скопировано в буфер обмена: {ep}", $"Copied to clipboard: {ep}");
    public string ConfigSavedTitle => T("Конфиг сохранён", "Config saved");
    public string ConfigExportErrTitle => T("Ошибка экспорта конфига", "Config export failed");
    public string CoreMissingSocksMsg => T("Выберите или скачайте warpscout.exe.", "Select or download warpscout.exe.");
    public string SocksStartErr => T("Не удалось запустить SOCKS5", "Failed to start SOCKS5");
    public string SocksExitedCode(int code) => T($"SOCKS5 завершился с кодом {code}", $"SOCKS5 exited with code {code}");
    public string SocksBannerText(string ep, string proto) => T(
        $"SOCKS5 активен: 127.0.0.1:1080 → {ep} ({proto})",
        $"SOCKS5 active: 127.0.0.1:1080 → {ep} ({proto})");
}

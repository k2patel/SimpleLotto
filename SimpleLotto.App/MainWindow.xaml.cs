using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SimpleLotto.App.Models;
using SimpleLotto.App.Services;
using SimpleLotto.App.Services.Win32;
using WinRT.Interop;
using Windows.Graphics;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;

namespace SimpleLotto.App;

public sealed partial class MainWindow : Window
{
    private readonly RdisplayService _rdisplay;
    private readonly LocalStore _store;
    private readonly AppUpdateService _updates;
    private readonly LicenseService _license;
    private readonly ScannerInputService _scannerInput;
    private readonly ObservableCollection<SaleLine> _sales = new();
    private readonly ObservableCollection<SaleLine> _allSales = new();
    private readonly HashSet<string> _voidedSaleKeys = new(StringComparer.Ordinal);
    private readonly HashSet<long> _voidedSaleIds = new();
    private readonly ObservableCollection<ImportLine> _imports = new();
    private readonly ObservableCollection<ReceivedBundleLine> _receivedBundles = new();
    private readonly List<ActivationLine> _activations = new();
    private readonly ObservableCollection<ImportBin> _importBins = new();
    private readonly ObservableCollection<BinCard> _binCards = new();
    private readonly ObservableCollection<BundleDetailLine> _selectedBinBundles = new();
    private readonly ObservableCollection<InventoryRecord> _receivingRecords = new();
    private readonly ObservableCollection<InventoryRecord> _pagedReceivingRecords = new();
    private readonly ObservableCollection<InventoryRecord> _inventoryRecords = new();
    private readonly ObservableCollection<InventoryRecord> _pagedInventoryRecords = new();
    private readonly ObservableCollection<GameCatalogRecord> _gameCatalog = new();
    private readonly ObservableCollection<GameCatalogRecord> _pagedGameCatalog = new();
    private readonly ObservableCollection<ClosingBinCard> _closingBinCards = new();
    private readonly ObservableCollection<ClosingScanRow> _closingScanRows = new();
    private readonly ObservableCollection<ClosingHistoryRow> _closingHistoryRows = new();
    private readonly ObservableCollection<ClosingHistoryRow> _pagedClosingHistoryRows = new();
    private readonly ObservableCollection<AuditLogRow> _auditLogRows = new();
    private readonly ObservableCollection<AuditLogRow> _pagedAuditLogRows = new();
    private readonly ObservableCollection<RegisteredDisplayCard> _registeredDisplayCards = new();
    private readonly List<GameCatalogRecord> _manualGameCatalog = new();
    private readonly HashSet<int> _closingScannedBins = new();
    private readonly HashSet<string> _closingScannedBundleKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ImportLine> _closingCurrentPlacements = new();
    private readonly List<ImportTicket> _closingUnmatchedTickets = new();
    private readonly List<ImportLine> _closingResolvedPlacements = new();
    private readonly List<ClosingScanIssue> _closingScanIssues = new();
    private readonly List<ClosingScanSale> _closingScanSales = new();
    private bool _closingScanCaptured;
    private readonly SpeechSynthesizer _speechSynthesizer = new();
    private readonly MediaPlayer _speechPlayer = new()
    {
        AutoPlay = false,
        Volume = 1.0
    };
    private readonly SemaphoreSlim _speechGate = new(1, 1);
    private static readonly HttpClient ImageHttpClient = CreateImageHttpClient();
    private const string DefaultGameImageUri = "ms-appx:///Assets/SimpleLottoLogo64.png";
    private readonly IntPtr _hwnd;
    private readonly SubclassProc _subclassProc;
    private IntPtr _trayIconHandle;
    private bool _trayIconLoadedFromFile;
    private bool _trayIconVisible;
    private bool _allowExit;
    private bool _isNavCollapsed;
    private StartupStage _startupStage = StartupStage.Setup;
    private string _clerkName = string.Empty;
    private string _managerPasswordHash = string.Empty;
    private string _clerkPasswordHash = string.Empty;
    private UserRole _activeUserRole = UserRole.None;
    private string _activeUserName = string.Empty;
    private string _activeActorId = string.Empty;
    private string _managerActorId = string.Empty;
    private string _clerkActorId = string.Empty;
    private string _storeState = string.Empty;
    private string? _storeBarcodeLayout;
    private string _storeName = string.Empty;
    private string _storeStreet = string.Empty;
    private string _storeCity = string.Empty;
    private string _databaseSchemaVersion = string.Empty;
    private ClosingHistoryRow? _selectedClosingReport;
    private int _configuredBinCount = 90;
    private int _globalFirstTicketSerial;
    private int _scanPairTimeoutSeconds = 5;
    private bool _displayBurnInEnabled = true;
    private int _displayBurnInIntervalMinutes = 15;
    private string _scannerVid = string.Empty;
    private string _scannerPid = string.Empty;
    private string _scannerSerial = string.Empty;
    private DateTime _lastCloseUtc = DateTime.MinValue;
    private long _openIntervalId;
    private int _ledgerConflictCount;
    private int _blockingLedgerConflictCount;
    private bool _setupComplete;
    private bool _initialImportComplete;
    private bool _isWindowInitialized;
    private bool _isScannerPaired;
    private bool _useFocusedScannerCapture;
    private bool _automaticUpgradeCheckRunning;
    private bool _loginInProgress;
    private bool _auditLogPageDirty;
    private string _lastAutomaticUpgradeCheckDate = string.Empty;
    private ImportBin? _pendingImportBin;
    private ImportTicket? _pendingImportTicket;
    private bool _hasImportFailure;
    private int _receivingPageSize = 8;
    private int _inventoryPageSize = 8;
    private int _gameCatalogPageSize = 8;
    private int _closingHistoryPageSize = 8;
    private int _auditLogPageSize = 8;
    private int _receivingPage = 1;
    private int _inventoryPage = 1;
    private int _gameCatalogPage = 1;
    private int _closingHistoryPage = 1;
    private int _auditLogPage = 1;
    private readonly StringBuilder _startupScanBuffer = new();
    private readonly StringBuilder _focusedScanBuffer = new();
    private string _dashboardPendingBin = string.Empty;
    private ImportTicket? _dashboardPendingTicket;
    private long? _dashboardPendingPriceCents;
    private DateTime? _dashboardPendingBinAtUtc;
    private DateTime? _dashboardPendingTicketAtUtc;
    private int? _selectedBinNumber;
    private bool _isWorkflowDialogOpen;
    private Func<ClassifiedScan, bool>? _scannerScanOverride;
    private const string ScannerVidSettingKey = "barcode_scanner_vid";
    private const string ScannerPidSettingKey = "barcode_scanner_pid";
    private const string ScannerSerialSettingKey = "barcode_scanner_serial";
    private const string ScanPairTimeoutSettingKey = "scan_pair_timeout_seconds";
    private const string DisplayBurnInEnabledSettingKey = "display_burn_in_enabled";
    private const string DisplayBurnInIntervalSettingKey = "display_burn_in_interval_minutes";
    private const string AutomaticUpgradeLastCheckDateSettingKey = "automatic_upgrade_last_check_date";
    private const string GlobalFirstTicketSerialSettingKey = "global_first_ticket_serial";
    private const long StandardBundlePriceCents = 50_000;
    private const long FiftyDollarBundlePriceCents = 90_000;
    private const int LicenseExpiryWarningDays = 7;
    private const string EmailSendClosingSettingKey = "email_send_closing";
    private const string EmailIncludeShiftSummarySettingKey = "email_include_shift_summary";
    private const string EmailIncludeInventorySettingKey = "email_include_inventory";
    private const string EmailIncludeSalesDetailSettingKey = "email_include_sales_detail";
    private const string EmailIncludeCorrectionsSettingKey = "email_include_corrections";
    private const string EmailIncludeAnomaliesSettingKey = "email_include_anomalies";
    private const string EmailIncludePlacementEventsSettingKey = "email_include_placement_events";
    private const string EmailIncludeBinAssignmentsSettingKey = "email_include_bin_assignments";
    private const string EmailIncludeInitializationSettingKey = "email_include_initialization";
    private const string EmailIncludeClosingAuditSettingKey = "email_include_closing_audit";
    private const string EmailIncludePdfSettingKey = "email_include_pdf";
    private static readonly Regex BinCommandBarcode = new(
        @"^BIN-(\d{1,4})$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PriceCommandBarcode = new(
        @"^PRICE-(\d{1,5})$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private enum ScanKind
    {
        Bin,
        Price,
        Ticket
    }

    private sealed record ClassifiedScan(
        ScanKind Kind,
        string Raw,
        ImportTicket? Ticket = null,
        int? BinNumber = null,
        long? PriceCents = null);

    private sealed record ActivationBinSelection(int BinNumber, long? PriceCents);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const int ShowWindowRestore = 9;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NotifyIconData lpData);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(
        IntPtr hInst,
        string lpszName,
        uint uType,
        int cxDesired,
        int cyDesired,
        uint fuLoad);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(
        IntPtr hMenu,
        uint uFlags,
        int x,
        int y,
        int nReserved,
        IntPtr hWnd,
        IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(
        IntPtr hWnd,
        SubclassProc pfnSubclass,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(
        IntPtr hWnd,
        SubclassProc pfnSubclass,
        UIntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, UIntPtr wParam, IntPtr lParam);

    public MainWindow(RdisplayService rdisplay, LocalStore store)
    {
        _rdisplay = rdisplay;
        _store = store;
        _updates = new AppUpdateService(store);
        _license = new LicenseService();
        _license.StatusChanged += License_StatusChanged;
        _subclassProc = TraySubclassProc;
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        _scannerInput = new ScannerInputService(DispatcherQueue);
        _scannerInput.ScanReceived += ScannerInput_ScanReceived;
        _scannerInput.CaptureAvailabilityChanged += ScannerInput_CaptureAvailabilityChanged;
        Title = "SimpleLotto";
        ResizeWindow(1120, 720);
        SetScannerPaired(false);
        PopulateStateComboBox();
        SalesListView.ItemsSource = _sales;
        ImportListView.ItemsSource = _imports;
        ImportBinsGridView.ItemsSource = _importBins;
        BinsGridView.ItemsSource = _binCards;
        BinBundlesListView.ItemsSource = _selectedBinBundles;
        ReceivingListView.ItemsSource = _pagedReceivingRecords;
        InventoryListView.ItemsSource = _pagedInventoryRecords;
        GameCatalogListView.ItemsSource = _pagedGameCatalog;
        ClosingBinsGridView.ItemsSource = _closingBinCards;
        ClosingHistoryListView.ItemsSource = _pagedClosingHistoryRows;
        AuditLogListView.ItemsSource = _pagedAuditLogRows;
        RegisteredDisplaysListView.ItemsSource = _registeredDisplayCards;
        _rdisplay.DisplaysChanged += Rdisplay_DisplaysChanged;
        OrderInventoryTabs();
        RootGrid.AddHandler(
            UIElement.KeyDownEvent,
            new KeyEventHandler(OnGlobalKeyDown),
            handledEventsToo: true);
        AppWindow.Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        _ = WarmAudioEngineAsync();
        _isWindowInitialized = true;
        LoadApplicationState();
        RefreshTotals();
        QueueScheduledUpgradeCheck();
    }

    private void License_StatusChanged(LicenseStatus status)
    {
        _ = DispatcherQueue.TryEnqueue(() => ApplyLicenseStatus(status));
    }

    private void LoadApplicationState()
    {
        PersistedState state;
        try
        {
            state = _store.Load();
        }
        catch (Exception ex)
        {
            ShowSetupStage();
            StartupStatusText.Text = $"SQLite database could not be opened: {ex.Message}";
            return;
        }

        _managerActorId = ReadSetting(state, "manager_actor_id");
        _clerkActorId = ReadSetting(state, "clerk_actor_id");
        _openIntervalId = state.OpenIntervalId;
        _ledgerConflictCount = state.LedgerMigrationConflicts.Count;
        _blockingLedgerConflictCount = state.LedgerMigrationConflicts.Count(conflict =>
            string.Equals(conflict.Severity, "blocking", StringComparison.OrdinalIgnoreCase));

        if (!ReadBoolSetting(state, "setup_complete") ||
            string.IsNullOrWhiteSpace(ReadSetting(state, "manager_password_hash")))
        {
            ShowSetupStage();
            return;
        }

        ApplyApplicationState(state);
    }

    private void ApplyApplicationState(PersistedState state)
    {
        _setupComplete = ReadBoolSetting(state, "setup_complete");
        _initialImportComplete = ReadBoolSetting(state, "initial_import_complete");
        _storeState = ReadSetting(state, "store_state");
        _storeBarcodeLayout = ReadSetting(state, "store_barcode_layout");
        _storeName = ReadSetting(state, "store_name");
        _storeStreet = ReadSetting(state, "store_street");
        _storeCity = ReadSetting(state, "store_city");
        _databaseSchemaVersion = ReadSetting(state, "schema_version");
        _configuredBinCount = Math.Clamp(ReadIntSetting(state, "configured_bin_count", 90), 1, 500);
        var savedGlobalFirstTicket = ReadSetting(state, GlobalFirstTicketSerialSettingKey);
        _globalFirstTicketSerial = savedGlobalFirstTicket is "0" or "1"
            ? int.Parse(savedGlobalFirstTicket, CultureInfo.InvariantCulture)
            : state.ManualGames
                .Where(game => game.FirstTicketSerial is 0 or 1)
                .GroupBy(game => game.FirstTicketSerial)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key)
                .Select(group => group.Key)
                .FirstOrDefault();
        _scanPairTimeoutSeconds = Math.Clamp(ReadIntSetting(state, ScanPairTimeoutSettingKey, 5), 1, 30);
        _displayBurnInEnabled = ReadBoolSetting(state, DisplayBurnInEnabledSettingKey, true);
        _displayBurnInIntervalMinutes = Math.Clamp(ReadIntSetting(state, DisplayBurnInIntervalSettingKey, 15), 1, 1440);
        _scannerVid = ReadSetting(state, ScannerVidSettingKey);
        _scannerPid = ReadSetting(state, ScannerPidSettingKey);
        _scannerSerial = ReadSetting(state, ScannerSerialSettingKey);
        _lastAutomaticUpgradeCheckDate = ReadSetting(state, AutomaticUpgradeLastCheckDateSettingKey);
        _managerPasswordHash = ReadSetting(state, "manager_password_hash");
        _managerActorId = ReadSetting(state, "manager_actor_id");
        _clerkName = ReadSetting(state, "clerk_name");
        _clerkPasswordHash = ReadSetting(state, "clerk_password_hash");
        _clerkActorId = ReadSetting(state, "clerk_actor_id");
        _lastCloseUtc = ReadDateTimeSetting(state, "last_close_utc");
        _openIntervalId = state.OpenIntervalId;
        _ledgerConflictCount = state.LedgerMigrationConflicts.Count;
        _blockingLedgerConflictCount = state.LedgerMigrationConflicts.Count(conflict =>
            string.Equals(conflict.Severity, "blocking", StringComparison.OrdinalIgnoreCase));

        StoreNameBox.Text = _storeName;
        StoreStreetBox.Text = _storeStreet;
        StoreCityBox.Text = _storeCity;
        BinCountBox.Value = _configuredBinCount;
        GlobalFirstTicketEditComboBox.SelectedIndex = _globalFirstTicketSerial;
        ScanPairTimeoutBox.Value = _scanPairTimeoutSeconds;
        DisplayBurnInCheckBox.IsChecked = _displayBurnInEnabled;
        DisplayBurnInIntervalBox.Value = _displayBurnInIntervalMinutes;
        _rdisplay.ConfigureDisplaySettings(_displayBurnInEnabled, _displayBurnInIntervalMinutes);
        ManagerPasswordBox.Password = string.Empty;
        ClerkNameBox.Text = _clerkName;
        ClerkPasswordBox.Password = string.Empty;
        BackupFolderBox.Text = ReadSetting(state, "backup_folder_path");
        SmtpHostBox.Text = ReadSetting(state, "smtp_host");
        SmtpPortBox.Value = ReadIntSetting(state, "smtp_port", 587);
        SmtpUserBox.Text = ReadSetting(state, "smtp_user");
        SmtpPasswordBox.Password = string.Empty;
        EmailToBox.Text = ReadSetting(state, "email_to");
        ApplyClosingEmailSettings(state);
        SetScannerPaired(!string.IsNullOrWhiteSpace(_scannerVid) && !string.IsNullOrWhiteSpace(_scannerPid));
        _scannerInput.Configure(_scannerVid, _scannerPid, _scannerSerial);

        var selectedState = StateOptions.FirstOrDefault(s => s.Code == _storeState);
        if (selectedState is not null)
            StateComboBox.SelectedItem = selectedState;
        else
            UpdateBarcodeFormatFromState();

        _imports.Clear();
        foreach (var line in state.Imports)
            _imports.Add(new ImportLine(line.GameId, line.BundleId, line.Ticket, line.Bin, line.Source, line.IsSoldOut));

        _receivedBundles.Clear();
        foreach (var bundle in state.ReceivedBundles)
        {
            _receivedBundles.Add(new ReceivedBundleLine(
                bundle.GameId,
                bundle.BundleId,
                bundle.ReceivedAtUtc.ToLocalTime(),
                bundle.Source));
        }

        _activations.Clear();
        foreach (var activation in state.Activations)
        {
            _activations.Add(new ActivationLine(
                activation.ActivatedAtUtc.ToLocalTime(),
                activation.GameId,
                activation.BundleId,
                activation.Bin,
                activation.Source,
                activation.IntervalId,
                activation.ActorId,
                activation.ActorName));
        }

        _sales.Clear();
        _allSales.Clear();
        _voidedSaleKeys.Clear();
        _voidedSaleIds.Clear();
        foreach (var saleKey in state.VoidedSaleKeys)
            _voidedSaleKeys.Add(saleKey);
        foreach (var saleId in state.VoidedSaleIds)
            _voidedSaleIds.Add(saleId);
        foreach (var line in state.Sales)
        {
            var saleLine = new SaleLine(
                line.SoldAtUtc.ToLocalTime(),
                line.GameId,
                line.Bin,
                line.Ticket,
                line.Quantity,
                line.AmountCents / 100m,
                line.Source,
                line.BundleId,
                line.Id,
                line.IntervalId,
                line.ActorId,
                line.ActorName,
                line.CorrectsSaleId,
                line.MigrationState);
            _allSales.Add(saleLine);
            if (line.IntervalId == _openIntervalId)
                _sales.Add(saleLine);
        }

        _manualGameCatalog.Clear();
        foreach (var game in state.ManualGames)
        {
            if (string.IsNullOrWhiteSpace(game.GameId))
                continue;

            _manualGameCatalog.Add(new GameCatalogRecord(
                game.GameId,
                string.IsNullOrWhiteSpace(game.Name) ? $"Game {game.GameId}" : game.Name,
                game.PriceCents,
                AutomaticBundlePriceCents(game.PriceCents),
                string.IsNullOrWhiteSpace(game.Source) ? "Manual" : game.Source,
                string.IsNullOrWhiteSpace(game.ImageUri) ? "ms-appx:///Assets/SimpleLottoLogo64.png" : game.ImageUri,
                string.IsNullOrWhiteSpace(game.ImageStatus) ? "Image not cached" : game.ImageStatus));
        }

        _closingHistoryRows.Clear();
        foreach (var row in BuildClosingHistoryRows(state.ClosingHistory))
            _closingHistoryRows.Add(row);
        ApplyClosingHistoryPage();

        _auditLogRows.Clear();
        foreach (var audit in state.AuditLog)
            _auditLogRows.Add(AuditLogRow.From(audit));
        ApplyAuditLogPage();

        foreach (var game in _manualGameCatalog)
            ReopenBundlesExtendedByGameSetup(game);

        BuildImportBins(clearImports: false);

        if (_initialImportComplete)
            ShowLoginStage();
        else
            ShowImportStage();

        if (state.PendingClosingReports.Count > 0)
            _ = RetryPendingClosingReportsAsync(state.PendingClosingReports);
    }

    private bool SaveSetupState()
    {
        try
        {
            _store.SaveSetup(new StoreSetup(
                _setupComplete,
                _initialImportComplete,
                _storeState,
                _storeBarcodeLayout ?? string.Empty,
                _storeName,
                _storeStreet,
                _storeCity,
                _configuredBinCount,
                _managerPasswordHash,
                _clerkName,
                _clerkPasswordHash,
                _managerActorId,
                _clerkActorId));
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Unable to save SQLite setup: {ex.Message}";
            return false;
        }
    }

    private bool SaveSetting(string key, string value)
    {
        try
        {
            _store.SaveSetting(key, value);
            TryRecordAudit("settings", "Setting saved", $"Updated {key}");
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Unable to save setting {key}: {ex.Message}";
            return false;
        }
    }

    private void TryRecordAudit(string category, string action, string detail)
    {
        var record = NewAuditRecord(category, action, TruncateAuditDetail(detail));
        try
        {
            _store.InsertAudit(record);
            AddAuditLogRowToUi(record);
        }
        catch (Exception ex)
        {
            // Audit failures should not block the operator workflow.
            AppLog.Error("Audit insert failed.", ex);
        }
    }

    private void AddAuditLogRowToUi(StoredAuditRecord record)
    {
        _auditLogRows.Insert(0, AuditLogRow.From(record));
        while (_auditLogRows.Count > LocalStore.RecentAuditLogLimit)
            _auditLogRows.RemoveAt(_auditLogRows.Count - 1);

        if (SettingsContent.Visibility == Visibility.Visible &&
            ReferenceEquals(SettingsTabs.SelectedItem, AuditSettingsTab))
        {
            ApplyAuditLogPage(resetPage: true);
            _auditLogPageDirty = false;
            return;
        }

        _auditLogPageDirty = true;
    }

    private static string TruncateAuditDetail(string detail)
    {
        var normalized = string.IsNullOrWhiteSpace(detail) ? string.Empty : detail.Trim();
        return normalized.Length <= 500
            ? normalized
            : normalized[..497] + "...";
    }

    private StoredAuditRecord NewAuditRecord(string category, string action, string detail) =>
        new(
            DateTime.UtcNow,
            category,
            action,
            string.IsNullOrWhiteSpace(_activeUserName) ? "system" : _activeUserName,
            detail,
            string.IsNullOrWhiteSpace(_activeActorId) ? "system" : _activeActorId);

    private void SaveSecretSetting(string key, string value)
    {
        try
        {
            _store.SaveSetting(key, ProtectSecret(value));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Unable to save setting {key}: {ex.Message}";
        }
    }

    private void ApplyClosingEmailSettings(PersistedState state)
    {
        SettingsEmailSendCheckBox.IsChecked = ReadBoolSetting(state, EmailSendClosingSettingKey, true);
        SettingsEmailShiftSummaryCheckBox.IsChecked = ReadBoolSetting(state, EmailIncludeShiftSummarySettingKey, true);
        SettingsEmailInventoryCheckBox.IsChecked = ReadBoolSetting(state, EmailIncludeInventorySettingKey, true);
        SettingsEmailSalesDetailCheckBox.IsChecked = ReadBoolSetting(state, EmailIncludeSalesDetailSettingKey, true);
        SettingsEmailCorrectionsCheckBox.IsChecked = ReadBoolSetting(state, EmailIncludeCorrectionsSettingKey, true);
        SettingsEmailAnomaliesCheckBox.IsChecked = ReadBoolSetting(state, EmailIncludeAnomaliesSettingKey, true);
        SettingsEmailPlacementEventsCheckBox.IsChecked = ReadBoolSetting(state, EmailIncludePlacementEventsSettingKey, true);
        SettingsEmailBinAssignmentsCheckBox.IsChecked = ReadBoolSetting(state, EmailIncludeBinAssignmentsSettingKey, true);
        SettingsEmailInitializationCheckBox.IsChecked = ReadBoolSetting(state, EmailIncludeInitializationSettingKey, true);
        SettingsEmailAuditCheckBox.IsChecked = ReadBoolSetting(state, EmailIncludeClosingAuditSettingKey, true);
        SettingsEmailPdfCheckBox.IsChecked = ReadBoolSetting(state, EmailIncludePdfSettingKey, true);
        UpdateSettingsEmailSummary();
    }

    private bool SaveClosingEmailSettingsFromSettings() =>
        SaveClosingEmailSettings(
            SettingsEmailSendCheckBox,
            SettingsEmailShiftSummaryCheckBox,
            SettingsEmailInventoryCheckBox,
            SettingsEmailSalesDetailCheckBox,
            SettingsEmailCorrectionsCheckBox,
            SettingsEmailAnomaliesCheckBox,
            SettingsEmailPlacementEventsCheckBox,
            SettingsEmailBinAssignmentsCheckBox,
            SettingsEmailInitializationCheckBox,
            SettingsEmailAuditCheckBox,
            SettingsEmailPdfCheckBox);

    private bool SaveClosingEmailSettings(
        CheckBox sendEmail,
        CheckBox shiftSummary,
        CheckBox inventory,
        CheckBox salesDetail,
        CheckBox corrections,
        CheckBox anomalies,
        CheckBox placementEvents,
        CheckBox binAssignments,
        CheckBox initialization,
        CheckBox closingAudit,
        CheckBox pdf)
    {
        var saved = SaveSetting(EmailSendClosingSettingKey, BoolSetting(sendEmail.IsChecked == true));
        saved &= SaveSetting(EmailIncludeShiftSummarySettingKey, BoolSetting(shiftSummary.IsChecked == true));
        saved &= SaveSetting(EmailIncludeInventorySettingKey, BoolSetting(inventory.IsChecked == true));
        saved &= SaveSetting(EmailIncludeSalesDetailSettingKey, BoolSetting(salesDetail.IsChecked == true));
        saved &= SaveSetting(EmailIncludeCorrectionsSettingKey, BoolSetting(corrections.IsChecked == true));
        saved &= SaveSetting(EmailIncludeAnomaliesSettingKey, BoolSetting(anomalies.IsChecked == true));
        saved &= SaveSetting(EmailIncludePlacementEventsSettingKey, BoolSetting(placementEvents.IsChecked == true));
        saved &= SaveSetting(EmailIncludeBinAssignmentsSettingKey, BoolSetting(binAssignments.IsChecked == true));
        saved &= SaveSetting(EmailIncludeInitializationSettingKey, BoolSetting(initialization.IsChecked == true));
        saved &= SaveSetting(EmailIncludeClosingAuditSettingKey, BoolSetting(closingAudit.IsChecked == true));
        saved &= SaveSetting(EmailIncludePdfSettingKey, BoolSetting(pdf.IsChecked == true));
        return saved;
    }

    private static string BoolSetting(bool value) => value ? "1" : "0";

    private void SettingsEmailOptionChanged(object sender, RoutedEventArgs e) =>
        UpdateSettingsEmailSummary();

    private void UpdateSettingsEmailSummary()
    {
        SettingsEmailChoicesStatusText.Text = BuildClosingEmailSummaryText(
            SettingsEmailSendCheckBox.IsChecked == true,
            SelectedSettingsEmailReportNames());
    }

    private IReadOnlyList<string> SelectedSettingsEmailReportNames() =>
        SelectedEmailReportNames(
            SettingsEmailShiftSummaryCheckBox,
            SettingsEmailInventoryCheckBox,
            SettingsEmailSalesDetailCheckBox,
            SettingsEmailCorrectionsCheckBox,
            SettingsEmailAnomaliesCheckBox,
            SettingsEmailPlacementEventsCheckBox,
            SettingsEmailBinAssignmentsCheckBox,
            SettingsEmailInitializationCheckBox,
            SettingsEmailAuditCheckBox,
            SettingsEmailPdfCheckBox);

    private static IReadOnlyList<string> SelectedEmailReportNames(
        CheckBox shiftSummary,
        CheckBox inventory,
        CheckBox salesDetail,
        CheckBox corrections,
        CheckBox anomalies,
        CheckBox placementEvents,
        CheckBox binAssignments,
        CheckBox initialization,
        CheckBox closingAudit,
        CheckBox pdf)
    {
        var names = new List<string>();
        AddSelected(names, shiftSummary, "shift_summary.csv");
        AddSelected(names, inventory, "inventory.csv");
        AddSelected(names, salesDetail, "sales_detail.csv");
        AddSelected(names, corrections, "corrections.csv");
        AddSelected(names, anomalies, "anomalies.csv");
        AddSelected(names, placementEvents, "placement_events.csv");
        AddSelected(names, binAssignments, "bin_assignments.csv");
        AddSelected(names, initialization, "initialization.csv");
        AddSelected(names, closingAudit, "closing_audit.csv");
        AddSelected(names, pdf, "closing_report.pdf");
        return names;
    }

    private static void AddSelected(List<string> names, CheckBox checkBox, string name)
    {
        if (checkBox.IsChecked == true)
            names.Add(name);
    }

    private static string BuildClosingEmailSummaryText(bool sendEmail, IReadOnlyList<string> selectedReports)
    {
        if (!sendEmail)
            return "Closing email will not be sent.";

        if (selectedReports.Count == 0)
            return "Closing email is enabled, but no reports are selected.";

        return $"Closing email includes {selectedReports.Count.ToString(CultureInfo.CurrentCulture)} item{(selectedReports.Count == 1 ? string.Empty : "s")}: {string.Join(", ", selectedReports)}.";
    }

    private static string ReadSetting(PersistedState state, string key) =>
        state.Settings.TryGetValue(key, out var value) ? value : string.Empty;

    private static bool ReadBoolSetting(PersistedState state, string key) =>
        ReadSetting(state, key) == "1";

    private static bool ReadBoolSetting(PersistedState state, string key, bool fallback) =>
        state.Settings.TryGetValue(key, out var value)
            ? value == "1"
            : fallback;

    private static int ReadIntSetting(PersistedState state, string key, int fallback) =>
        int.TryParse(ReadSetting(state, key), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static DateTime ReadDateTimeSetting(PersistedState state, string key) =>
        DateTime.TryParse(
            ReadSetting(state, key),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var value)
            ? value.ToUniversalTime()
            : DateTime.MinValue;

    private static string ProtectSecret(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return plaintext;

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var protectedBytes = ProtectedData.Protect(
            bytes,
            optionalEntropy: null,
            scope: DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private async void StartupPrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        switch (_startupStage)
        {
            case StartupStage.Setup:
                CompleteSetupStage();
                break;
            case StartupStage.Import:
                await CompleteImportStageAsync();
                break;
            case StartupStage.Login:
                await CompleteLoginStageAsync();
                break;
        }
    }

    private async void LoginPasswordBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || _startupStage != StartupStage.Login)
            return;

        e.Handled = true;
        await CompleteLoginStageAsync();
    }

    private void LoginUserComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ConfigureLoginPasswordEntry();

    private void StartupBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_startupStage == StartupStage.Import)
        {
            ShowSetupStage();
        }
        else if (_startupStage == StartupStage.Login)
        {
            ShowImportStage();
        }
    }

    private void StateComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateBarcodeFormatFromState();
    }

    private void OnGlobalKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_scannerInput.IsActivelyCapturing)
            return;

        if (_isWorkflowDialogOpen)
            return;

        if (FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox or PasswordBox or AutoSuggestBox or RichEditBox or NumberBox)
            return;

        if (StartupOverlay.Visibility == Visibility.Visible)
        {
            if (_startupStage == StartupStage.Import)
                CaptureGlobalScanKey(e, _startupScanBuffer, ProcessImportScanInput, ImportScanStatusText);
            return;
        }

        CaptureGlobalScanKey(e, _focusedScanBuffer, ProcessFocusedScanInput, DashboardScannerStatusText);
    }

    private void ScannerInput_ScanReceived(string raw)
    {
        if (_useFocusedScannerCapture)
            return;

        try
        {
            if (!TryClassifyScan(raw, out var scan))
            {
                RejectScannerInput(raw, "Barcode is not a recognized SimpleLotto scan.");
                return;
            }

            if (_scannerScanOverride?.Invoke(scan) == true)
                return;

            if (StartupOverlay.Visibility == Visibility.Visible)
            {
                if (_startupStage == StartupStage.Import && scan.Kind is ScanKind.Bin or ScanKind.Ticket)
                    ProcessImportScanSegment(scan.Raw);
                else
                    RejectScannerInput(scan.Raw, "Scanner input is not allowed at this startup stage.");
                return;
            }

            if (_isWorkflowDialogOpen)
            {
                RejectScannerInput(scan.Raw, "No active scanner route for this dialog.");
                return;
            }

            ProcessFocusedScan(scan);
        }
        catch (Exception ex)
        {
            AppLog.Error("Paired scanner input could not be routed.", ex);
            RejectScannerInput(raw, $"Routing failure: {ex.Message}");
        }
    }

    private bool TryClassifyScan(string raw, out ClassifiedScan scan)
    {
        scan = null!;
        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
            return false;

        if (BinCommandBarcode.Match(trimmed) is { Success: true } binMatch &&
            int.TryParse(binMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var binNumber))
        {
            scan = new ClassifiedScan(ScanKind.Bin, trimmed, BinNumber: binNumber);
            return true;
        }

        if (PriceCommandBarcode.Match(trimmed) is { Success: true } priceMatch &&
            long.TryParse(priceMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var priceCents) &&
            priceCents > 0)
        {
            scan = new ClassifiedScan(ScanKind.Price, trimmed, PriceCents: priceCents);
            return true;
        }

        if (!trimmed.All(c => char.IsDigit(c) || c == '-'))
            return false;

        var ticket = TryParseImportTicket(trimmed);
        if (ticket is null)
            return false;

        scan = new ClassifiedScan(ScanKind.Ticket, trimmed, Ticket: ticket);
        return true;
    }

    private void RejectScannerInput(string raw, string reason)
    {
        DashboardScannerStatusText.Text = "Scan error. Scan again.";
        DashboardLastScanText.Text = string.IsNullOrWhiteSpace(raw)
            ? "Last scan failed."
            : $"Last scan failed: {raw}";
        StatusText.Text = reason;
        TryRecordAudit("scanner", "Scan rejected", $"{reason} Raw scan {raw}");
        _ = SpeakAsync("Scan again.");
    }

    private void ScannerInput_CaptureAvailabilityChanged(bool isAvailable)
    {
        RefreshScannerPairingStatus();
        if (isAvailable)
            DashboardScannerStatusText.Text = "Paired scanner ready.";
        else if (IsScannerPaired)
            DashboardScannerStatusText.Text = "Paired scanner not detected. Reconnect it or pair again.";
    }

    private void CaptureGlobalScanKey(
        KeyRoutedEventArgs e,
        StringBuilder buffer,
        Action<string> processScan,
        TextBlock? statusText)
    {
        if (e.Key == VirtualKey.Enter)
        {
            if (buffer.Length == 0)
                return;

            e.Handled = true;
            var raw = buffer.ToString();
            buffer.Clear();
            processScan(raw);
            return;
        }

        if (!TryMapScanKey(e.Key, out var character))
            return;

        e.Handled = true;
        buffer.Append(character);
        if (statusText is not null)
            statusText.Text = "Scanning...";
    }

    private void ObserveFocusedCommandScanKey(
        KeyRoutedEventArgs e,
        StringBuilder buffer,
        Func<ClassifiedScan, bool> routeScan)
    {
        if (e.Key == VirtualKey.Enter)
        {
            if (buffer.Length == 0)
                return;

            var raw = buffer.ToString();
            buffer.Clear();
            if (TryClassifyScan(raw, out var scan) && routeScan(scan))
                e.Handled = true;
            return;
        }

        if (!TryMapScanKey(e.Key, out var character))
            return;

        buffer.Append(character);
    }

    private void ImportBinsGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ImportBin bin)
            AcceptImportBin(bin);
    }

    private void ResolveImportFailureButton_Click(object sender, RoutedEventArgs e)
    {
        ClearImportFailure();
        _startupScanBuffer.Clear();
        StartupStatusText.Text = "Failure resolved. Scan the corrected bin or ticket.";
        ImportScanStatusText.Text = "Scan BIN barcode and ticket barcode in either order.";
        _ = SpeakAsync("Failure resolved.");
    }

    private void CompleteSetupStage()
    {
        if (StateComboBox.SelectedItem is not StateOption state)
        {
            StartupStatusText.Text = "Select the store state. State is required for lottery barcode and game setup.";
            return;
        }

        var storeName = StoreNameBox.Text.Trim();
        var storeStreet = StoreStreetBox.Text.Trim();
        var storeCity = StoreCityBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(storeName) ||
            string.IsNullOrWhiteSpace(storeStreet) ||
            string.IsNullOrWhiteSpace(storeCity))
        {
            StartupStatusText.Text = "Enter store name, street, and city so license feedback can identify this store.";
            return;
        }

        var binCount = CoerceInt(BinCountBox.Value, 90);
        if (binCount < 1)
        {
            StartupStatusText.Text = "Enter at least one bin before initial import.";
            return;
        }

        if (!PinHashService.IsValidPin(ManagerPasswordBox.Password))
        {
            StartupStatusText.Text = "Manager PIN must contain exactly four digits.";
            return;
        }

        var clerkName = ClerkNameBox.Text.Trim();
        var clerkPin = ClerkPasswordBox.Password;
        if (string.IsNullOrWhiteSpace(clerkName) != string.IsNullOrEmpty(clerkPin))
        {
            StartupStatusText.Text = "Enter both a Clerk name and four-digit PIN, or leave both blank.";
            return;
        }

        if (!string.IsNullOrEmpty(clerkPin) && !PinHashService.IsValidPin(clerkPin))
        {
            StartupStatusText.Text = "Clerk PIN must contain exactly four digits.";
            return;
        }

        _storeState = state.Code;
        _storeBarcodeLayout = state.DefaultLayout;
        _storeName = storeName;
        _storeStreet = storeStreet;
        _storeCity = storeCity;
        _configuredBinCount = Math.Min(500, binCount);
        _managerActorId = string.IsNullOrWhiteSpace(_managerActorId) ? $"actor-{Guid.NewGuid():N}" : _managerActorId;
        _clerkActorId = string.IsNullOrWhiteSpace(_clerkActorId) ? $"actor-{Guid.NewGuid():N}" : _clerkActorId;
        _managerPasswordHash = PinHashService.CreateHash(ManagerPasswordBox.Password);
        _clerkName = clerkName;
        _clerkPasswordHash = string.IsNullOrEmpty(clerkPin)
            ? string.Empty
            : PinHashService.CreateHash(clerkPin);
        _setupComplete = true;
        _initialImportComplete = false;
        BuildImportBins();
        if (!SaveSetupState())
        {
            _setupComplete = false;
            StartupStatusText.Text = "First-install setup could not be saved. Resolve the SQLite error and try again.";
            return;
        }

        ShowImportStage();
    }

    private async Task CompleteLoginStageAsync()
    {
        if (_loginInProgress)
            return;

        if (LoginUserComboBox.SelectedItem is not ComboBoxItem userItem)
        {
            StartupStatusText.Text = "Select a user to login.";
            return;
        }

        var user = userItem.Content?.ToString() ?? string.Empty;
        var isManager = string.Equals(user, "Manager", StringComparison.Ordinal);
        var expectedHash = isManager ? _managerPasswordHash : _clerkPasswordHash;
        var credential = LoginPasswordBox.Password;

        _loginInProgress = true;
        StartupPrimaryButton.IsEnabled = false;
        LoginUserComboBox.IsEnabled = false;
        LoginPasswordBox.IsEnabled = false;
        try
        {
            var verification = await Task.Run(() => PinHashService.Verify(credential, expectedHash));
            if (!verification.IsValid)
            {
                StartupStatusText.Text = "Password does not match the selected user.";
                return;
            }

            string? upgradedHash = null;
            if (verification.IsLegacy)
            {
                var selectedPin = await PromptForFourDigitPinAsync(user, credential);
                if (selectedPin is null)
                {
                    StartupStatusText.Text = "Create a new four-digit PIN to finish this required login update.";
                    return;
                }

                upgradedHash = await Task.Run(() => PinHashService.CreateHash(selectedPin));
            }
            else if (verification.NeedsUpgrade)
            {
                upgradedHash = await Task.Run(() => PinHashService.CreateHash(credential));
            }

            if (upgradedHash is not null)
            {
                if (!TrySaveUpgradedPinHash(isManager, upgradedHash))
                {
                    StartupStatusText.Text = "The login was verified, but its required PIN update could not be saved. Try again.";
                    return;
                }

                TryRecordAudit(
                    "auth",
                    verification.IsLegacy ? "Required PIN created" : "PIN security updated",
                    verification.IsLegacy
                        ? $"{user} replaced a legacy login credential with a four-digit PIN"
                        : $"{user} login hash work factor upgraded");
            }

            _activeUserRole = isManager ? UserRole.Manager : UserRole.Clerk;
            _activeUserName = user;
            _activeActorId = isManager ? _managerActorId : _clerkActorId;
            StartupOverlay.Visibility = Visibility.Collapsed;
            StatusText.Text = $"{user} logged in as {_activeUserRole} for {_storeState}.";
            DashboardScannerModeText.Text = "Global scanner";
            DashboardScannerStatusText.Text = "Ready for scanner input.";
            DashboardPairingStatusText.Text = "Background capture: not paired";
            ApplyRoleAccess();
            RefreshOperationalPages();
            TryRecordAudit("auth", "Login", $"{user} logged in as {_activeUserRole}");
            if (_ledgerConflictCount > 0)
            {
                StatusText.Text = _blockingLedgerConflictCount > 0
                    ? $"Ledger migration requires manager review: {_blockingLedgerConflictCount.ToString(CultureInfo.CurrentCulture)} blocking and {(_ledgerConflictCount - _blockingLedgerConflictCount).ToString(CultureInfo.CurrentCulture)} warning conflict(s). Current-interval sales remain explicit."
                    : $"Ledger migration recorded {_ledgerConflictCount.ToString(CultureInfo.CurrentCulture)} historical warning conflict(s) for manager review.";
            }
        }
        finally
        {
            _loginInProgress = false;
            StartupPrimaryButton.IsEnabled = true;
            LoginUserComboBox.IsEnabled = true;
            LoginPasswordBox.IsEnabled = true;
            if (_startupStage == StartupStage.Login && StartupOverlay.Visibility == Visibility.Visible)
                _ = LoginPasswordBox.Focus(FocusState.Programmatic);
        }
    }

    private bool TrySaveUpgradedPinHash(bool isManager, string upgradedHash)
    {
        var settingKey = isManager ? "manager_password_hash" : "clerk_password_hash";
        try
        {
            _store.SaveSetting(settingKey, upgradedHash);
            if (isManager)
                _managerPasswordHash = upgradedHash;
            else
                _clerkPasswordHash = upgradedHash;

            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("PIN hash upgrade could not be saved.", ex);
            return false;
        }
    }

    private async Task<string?> PromptForFourDigitPinAsync(string user, string currentCredential)
    {
        var pinBox = new PasswordBox
        {
            Header = "New 4-digit PIN",
            MaxLength = 4
        };
        var confirmationBox = new PasswordBox
        {
            Header = "Confirm PIN",
            MaxLength = 4
        };
        var validationText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetAutomationId(pinBox, "LegacyMigrationNewPin");
        AutomationProperties.SetAutomationId(confirmationBox, "LegacyMigrationConfirmPin");
        AutomationProperties.SetAutomationId(validationText, "LegacyMigrationValidation");

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = $"{user}'s existing login was verified. Create a different four-digit PIN before continuing.",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(validationText);
        content.Children.Add(pinBox);
        content.Children.Add(confirmationBox);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Password update required",
            Content = content,
            PrimaryButtonText = "Save PIN",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        string? selectedPin = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var newPin = pinBox.Password;
            if (!PinHashService.IsValidPin(newPin))
            {
                args.Cancel = true;
                validationText.Text = "The new PIN must contain exactly four digits.";
                validationText.Visibility = Visibility.Visible;
                pinBox.Password = string.Empty;
                confirmationBox.Password = string.Empty;
                pinBox.Focus(FocusState.Programmatic);
                return;
            }

            if (!string.Equals(newPin, confirmationBox.Password, StringComparison.Ordinal))
            {
                args.Cancel = true;
                validationText.Text = "The PIN and confirmation do not match.";
                validationText.Visibility = Visibility.Visible;
                confirmationBox.Password = string.Empty;
                confirmationBox.Focus(FocusState.Programmatic);
                return;
            }

            if (string.Equals(newPin, currentCredential, StringComparison.Ordinal))
            {
                args.Cancel = true;
                validationText.Text = "Choose a PIN that differs from the current password or PIN.";
                validationText.Visibility = Visibility.Visible;
                pinBox.Password = string.Empty;
                confirmationBox.Password = string.Empty;
                pinBox.Focus(FocusState.Programmatic);
                return;
            }

            selectedPin = newPin;
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? selectedPin : null;
    }

    private void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        TryRecordAudit("auth", "Logout", "User logged out");
        _activeUserRole = UserRole.None;
        _activeUserName = string.Empty;
        _activeActorId = string.Empty;
        LoginPasswordBox.Password = string.Empty;
        StartupOverlay.Visibility = Visibility.Visible;
        ShowLoginStage();
        StatusText.Text = "Logged out.";
        DashboardScannerStatusText.Text = "Login required before scanner input.";
        _focusedScanBuffer.Clear();
        _startupScanBuffer.Clear();
    }

    private void ShowSetupStage()
    {
        _startupStage = StartupStage.Setup;
        StartupSubtitleText.Text = "First install setup";
        SetupPanel.Visibility = Visibility.Visible;
        ImportPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Collapsed;
        StartupBackButton.Visibility = Visibility.Collapsed;
        StartupPrimaryButton.Content = "Continue";
        StartupStatusText.Text = "Select the store state and create the Manager PIN.";
    }

    private void ShowImportStage()
    {
        _startupStage = StartupStage.Import;
        StartupSubtitleText.Text = "Initial import wizard";
        SetupPanel.Visibility = Visibility.Collapsed;
        ImportPanel.Visibility = Visibility.Visible;
        LoginPanel.Visibility = Visibility.Collapsed;
        StartupBackButton.Visibility = Visibility.Visible;
        StartupPrimaryButton.Content = "Continue to Login";
        StartupPrimaryButton.IsEnabled = !_hasImportFailure;
        UpdateImportStatusText("Select a bin and scan its current ticket, or scan bin and ticket in either order.");
        ImportPanel.Focus(FocusState.Programmatic);
    }

    private void ShowLoginStage(bool allowBack = false)
    {
        _startupStage = StartupStage.Login;
        StartupSubtitleText.Text = "Login";
        SetupPanel.Visibility = Visibility.Collapsed;
        ImportPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
        StartupBackButton.Visibility = allowBack ? Visibility.Visible : Visibility.Collapsed;
        StartupPrimaryButton.Content = "Login";
        LoginUserComboBox.Items.Clear();
        LoginUserComboBox.Items.Add(new ComboBoxItem { Content = "Manager" });
        if (!string.IsNullOrWhiteSpace(_clerkName) &&
            !string.IsNullOrWhiteSpace(_clerkPasswordHash))
        {
            LoginUserComboBox.Items.Add(new ComboBoxItem { Content = _clerkName });
        }
        LoginUserComboBox.SelectedIndex = 0;
        LoginPasswordBox.Password = string.Empty;
        ConfigureLoginPasswordEntry();
        _ = LoginPasswordBox.Focus(FocusState.Programmatic);
    }

    private void ConfigureLoginPasswordEntry()
    {
        if (LoginPasswordBox is null || StartupStatusText is null)
            return;

        LoginPasswordBox.Password = string.Empty;
        LoginPasswordBox.Header = "Password";
        StartupStatusText.Text = "Enter the password for the selected user.";
    }

    private async Task CompleteImportStageAsync()
    {
        if (_hasImportFailure)
        {
            StartupStatusText.Text = "Resolve the import failure before continuing to login.";
            _ = SpeakAsync("Resolve import failure.");
            return;
        }

        if (_pendingImportBin is not null || _pendingImportTicket is not null)
        {
            StartupStatusText.Text = "Finish the pending bin and ticket scan, or resolve the pending import before continuing.";
            _ = SpeakAsync("Finish pending import.");
            return;
        }

        var importedGameIds = _imports
            .Where(line => string.Equals(line.Source, "initial_import", StringComparison.OrdinalIgnoreCase))
            .Select(line => line.GameId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var gameId in importedGameIds)
        {
            if (HasCompleteGameSetup(gameId))
                continue;

            var sample = _imports.First(line =>
                string.Equals(line.Source, "initial_import", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(line.GameId, gameId, StringComparison.OrdinalIgnoreCase));
            var ticket = new ImportTicket(sample.GameId, sample.BundleId, sample.Ticket, sample.Ticket);
            StartupStatusText.Text = $"Ticket price is required for game {gameId} before initial import can finish.";
            if (!await ShowActivationGameSetupDialogAsync(
                    sample.Bin,
                    ticket,
                    setupSource: "Initial import") ||
                !HasCompleteGameSetup(gameId))
            {
                StartupStatusText.Text = $"Initial import remains open. Enter and save the ticket price for game {gameId}.";
                TryRecordAudit(
                    "import",
                    "Initial import finalization paused",
                    $"Game {gameId} still requires ticket price setup");
                _ = SpeakAsync("Game setup required.");
                return;
            }
        }

        _initialImportComplete = true;
        if (!SaveSetupState())
        {
            _initialImportComplete = false;
            StartupStatusText.Text = "Initial import could not be finalized because setup was not saved. Try again.";
            TryRecordAudit(
                "import",
                "Initial import finalization failed",
                "SQLite setup state could not be persisted after game configuration");
            return;
        }

        var importedBundles = _imports.Count(line =>
            string.Equals(line.Source, "initial_import", StringComparison.OrdinalIgnoreCase));
        var configuredGames = _imports
            .Where(line => string.Equals(line.Source, "initial_import", StringComparison.OrdinalIgnoreCase))
            .Select(line => line.GameId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        TryRecordAudit(
            "import",
            "Initial import finalized",
            $"{importedBundles.ToString(CultureInfo.InvariantCulture)} bundles and {configuredGames.ToString(CultureInfo.InvariantCulture)} configured games");
        ShowLoginStage(allowBack: true);
    }

    private void PopulateStateComboBox()
    {
        StateComboBox.Items.Clear();
        foreach (var state in StateOptions)
            StateComboBox.Items.Add(state);

        StateComboBox.SelectedItem = StateOptions.FirstOrDefault(s => s.Code == "GA");
        UpdateBarcodeFormatFromState();
    }

    private void UpdateBarcodeFormatFromState()
    {
        if (StateComboBox.SelectedItem is not StateOption state)
        {
            BarcodeFormatBox.Text = string.Empty;
            return;
        }

        BarcodeFormatBox.Text = string.IsNullOrWhiteSpace(state.DefaultLayout)
            ? "Unknown - scanner calibration required"
            : state.DefaultLayout;
    }

    private void BuildImportBins(bool clearImports = true)
    {
        _importBins.Clear();
        for (var i = 1; i <= _configuredBinCount; i++)
            _importBins.Add(new ImportBin(i));

        if (clearImports)
            _imports.Clear();
        else
            RestoreImportBinCounts();

        _pendingImportBin = null;
        _pendingImportTicket = null;
        ClearImportFailure();
        ImportBinCountText.Text = $"{_configuredBinCount} bin{(_configuredBinCount == 1 ? string.Empty : "s")} ready";
        ImportPendingText.Text = "No pending scan.";
        ImportScanStatusText.Text = "Scan BIN barcode and ticket barcode in either order.";
        RefreshOperationalPages();
    }

    private void RestoreImportBinCounts()
    {
        foreach (var bin in _importBins)
        {
            bin.ImportedCount = _imports.Count(line =>
                string.Equals(line.Bin, bin.Number.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase));
        }
    }

    private void ProcessImportScanInput(string raw)
    {
        if (_hasImportFailure)
        {
            StartupStatusText.Text = "Resolve the current import failure before scanning again.";
            _ = SpeakAsync("Resolve import failure.");
            return;
        }

        if (!TryClassifyScan(raw, out var scan) || scan.Kind is ScanKind.Price)
        {
            UpdateImportStatusText("Scan a bin barcode or ticket barcode.");
            ImportScanStatusText.Text = "Scan was not recognized.";
            TryRecordAudit("scanner", "Opening scan rejected", $"Unrecognized scan {raw}");
            _ = SpeakAsync("Scan again.");
            return;
        }

        ProcessImportScanSegment(scan.Raw);
    }

    private void ProcessImportScanSegment(string raw)
    {
        if (TryParseBinNumber(raw, out var binNumber))
        {
            TryRecordAudit("scanner", "Opening scan received", $"Raw scan {raw}");
            var bin = _importBins.FirstOrDefault(b => b.Number == binNumber);
            if (bin is null)
            {
                TryRecordAudit("scanner", "Opening scan rejected", $"Wrong bin {binNumber}");
                FailImport($"Wrong bin {binNumber}. Valid bins are 1 through {_configuredBinCount}.", "Wrong bin.");
                return;
            }

            AcceptImportBin(bin);
            return;
        }

        var ticket = TryParseImportTicket(raw);
        if (ticket is null)
        {
            TryRecordAudit("scanner", "Opening scan rejected", $"Unrecognized scan {raw}");
            FailImport("Scan was not recognized as a configured-state ticket or a BIN barcode.", "Scan again.");
            return;
        }

        if (PhysicalBundleExists(ticket.GameId, ticket.BundleId))
        {
            _ = SpeakAsync("Duplicate");
            return;
        }

        TryRecordAudit("scanner", "Opening scan received", $"Raw scan {raw}");
        AcceptImportTicket(ticket);
    }

    private void ProcessFocusedScanInput(string raw)
    {
        if (!EnsureLicenseAllowsOperation("using scanner input"))
            return;

        if (!TryClassifyScan(raw, out var scan))
        {
            RejectScannerInput(raw, "Barcode is not a recognized SimpleLotto scan.");
            return;
        }

        ProcessFocusedScan(scan);
    }

    private void ProcessFocusedScan(ClassifiedScan scan)
    {
        if (scan.Kind == ScanKind.Price)
        {
            if (string.IsNullOrWhiteSpace(_dashboardPendingBin))
            {
                RejectScannerInput(scan.Raw, "Scan a bin and ticket before setting a price.");
                return;
            }

            _dashboardPendingPriceCents = scan.PriceCents;
            DashboardScannerStatusText.Text = $"Price {MoneyText(scan.PriceCents ?? 0)} captured. Scan ticket.";
            DashboardLastScanText.Text = $"Last scan: {scan.Raw}";
            StatusText.Text = "Price captured for the pending placement.";
            TryRecordAudit("scanner", "Price scan captured", $"Price {MoneyText(scan.PriceCents ?? 0)} pending for bin {_dashboardPendingBin}");
            return;
        }

        ProcessFocusedScanSegment(scan.Raw);
    }

    private void ProcessFocusedScanSegment(string raw)
    {
        ExpireDashboardPendingScanPair();
        TryRecordAudit("scanner", "Scan received", $"Raw scan {raw}");

        if (TryParseBinNumber(raw, out var binNumber))
        {
            if (!IsConfiguredBin(binNumber))
            {
                DashboardScannerStatusText.Text = $"Wrong bin {binNumber}. Scan a valid bin.";
                DashboardLastScanText.Text = $"Last scan failed: BIN-{binNumber}";
                StatusText.Text = $"Wrong bin {binNumber}.";
                TryRecordAudit("scanner", "Scan rejected", $"Wrong bin {binNumber}");
                _ = SpeakAsync("Wrong bin.");
                return;
            }

            _dashboardPendingBin = binNumber.ToString(CultureInfo.InvariantCulture);
            _dashboardPendingBinAtUtc = DateTime.UtcNow;
            TryRecordAudit("bin", "Bin scan captured", $"Bin {_dashboardPendingBin}");
            if (_dashboardPendingTicket is not null)
            {
                PlaceDashboardBundle(_dashboardPendingBin, _dashboardPendingTicket, _dashboardPendingPriceCents);
                return;
            }

            DashboardScannerStatusText.Text = $"Bin {_dashboardPendingBin} captured. Scan ticket.";
            DashboardLastScanText.Text = $"Last scan: BIN-{_dashboardPendingBin}";
            StatusText.Text = $"Bin {_dashboardPendingBin} selected for scanner workflow.";
            return;
        }

        var ticket = TryParseImportTicket(raw);
        if (ticket is null)
        {
            DashboardScannerStatusText.Text = "Scan was not recognized.";
            DashboardLastScanText.Text = $"Last scan failed: {raw}";
            StatusText.Text = "Scanner input was not recognized.";
            TryRecordAudit("scanner", "Scan rejected", $"Unrecognized scan {raw}");
            _ = SpeakAsync("Scan again.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(_dashboardPendingBin))
        {
            PlaceDashboardBundle(_dashboardPendingBin, ticket, _dashboardPendingPriceCents);
            return;
        }

        var activeBundle = FindActiveBundle(ticket);
        if (activeBundle is null)
        {
            var placedBundle = FindPlacedBundle(ticket);
            if (placedBundle?.IsSoldOut == true)
            {
                DashboardScannerStatusText.Text = $"Bundle {ticket.BundleId} is sold out.";
                DashboardLastScanText.Text = $"Game {ticket.GameId} | Bundle {ticket.BundleId} | Sold out | Bin {placedBundle.Bin}";
                StatusText.Text = $"Bundle {ticket.BundleId} in bin {placedBundle.Bin} is sold out.";
                TryRecordAudit("scanner", "Scan rejected", $"Sold-out bundle scanned: game {ticket.GameId}, bundle {ticket.BundleId}, bin {placedBundle.Bin}");
                _ = SpeakAsync("Bundle sold out.");
                return;
            }

            _ = PromptForActivationBinAsync(ticket);
            return;
        }

        _ = ProcessActiveTicketSaleAsync(activeBundle, ticket);
    }

    private async Task ProcessActiveTicketSaleAsync(ImportLine activeBundle, ImportTicket ticket)
    {
        try
        {
            if (!HasCompleteGameSetup(activeBundle.GameId))
            {
                DashboardScannerStatusText.Text = $"Game setup required for game {activeBundle.GameId}.";
                DashboardLastScanText.Text = $"Game {ticket.GameId} | Bundle {ticket.BundleId} | setup incomplete";
                StatusText.Text = $"Enter the ticket price before recording sales for game {activeBundle.GameId}.";
                TryRecordAudit(
                    "sale",
                    "Sale blocked",
                    $"Game {activeBundle.GameId}, bundle {activeBundle.BundleId}, bin {activeBundle.Bin}, scanned ticket {ticket.Ticket}, incomplete game setup");
                var setupSaved = await ShowActivationGameSetupDialogAsync(activeBundle.Bin, ticket);
                if (!setupSaved || !HasCompleteGameSetup(activeBundle.GameId))
                {
                    DashboardScannerStatusText.Text = $"Game setup required for game {activeBundle.GameId}. Sale not recorded.";
                    StatusText.Text = $"Sale not recorded because game {activeBundle.GameId} needs a valid ticket price.";
                    _ = SpeakAsync("Game setup required.");
                    return;
                }
            }

            if (!TryBuildTicketBackfillSale(
                    DateTime.Now,
                    activeBundle,
                    ticket.Ticket,
                    "normal_sale",
                    out var backfill,
                    out var rangeError))
            {
                RejectScannerInput(ticket.Ticket, rangeError);
                return;
            }

            var line = backfill.Sale;
            if (line.Amount <= 0)
            {
                DashboardScannerStatusText.Text = $"Game setup is invalid for game {activeBundle.GameId}. Sale not recorded.";
                DashboardLastScanText.Text = $"Game {ticket.GameId} | Bundle {ticket.BundleId} | setup invalid";
                StatusText.Text = $"Sale not recorded because game {activeBundle.GameId} calculated {line.AmountText}. Verify its ticket price.";
                TryRecordAudit(
                    "sale",
                    "Sale blocked",
                    $"Game {activeBundle.GameId}, bundle {activeBundle.BundleId}, bin {activeBundle.Bin}, sold {line.Ticket}, quantity {line.Quantity.ToString(CultureInfo.InvariantCulture)}, amount {line.AmountText}");
                _ = SpeakAsync("Game setup required.");
                return;
            }

            _sales.Insert(0, line);
            var updatedBundle = activeBundle with
            {
                Ticket = backfill.IsBundleComplete ? ticket.Ticket : backfill.NextTicket,
                IsSoldOut = backfill.IsBundleComplete
            };
            var persistedLine = SaveSaleLineAndUpdateImportTicket(line, updatedBundle);
            if (persistedLine is null)
            {
                _sales.Remove(line);
                return;
            }

            line = persistedLine;

            ReplaceImportLine(updatedBundle);
            SalesListView.SelectedItem = line;
            DashboardScannerStatusText.Text = backfill.IsBundleComplete
                ? $"Bundle {ticket.BundleId} sold out."
                : $"Ticket captured for game {ticket.GameId}.";
            DashboardLastScanText.Text = backfill.IsBundleComplete
                ? $"Game {ticket.GameId} | Bundle {ticket.BundleId} | Sold out | Bin {activeBundle.Bin}"
                : $"Game {ticket.GameId} | Bundle {ticket.BundleId} | Sold {line.Ticket} | Next {backfill.NextTicket} | Bin {activeBundle.Bin}";
            StatusText.Text = backfill.IsBundleComplete
                ? $"Bundle {ticket.BundleId} sold out after ticket {ticket.Ticket}."
                : $"Scanner sale captured for game {ticket.GameId}, {line.Quantity.ToString(CultureInfo.CurrentCulture)} ticket{(line.Quantity == 1 ? string.Empty : "s")}.";
            TryRecordAudit(
                "sale",
                backfill.IsBundleComplete ? "Bundle sold out" : "Ticket sale recorded",
                backfill.IsBundleComplete
                    ? $"Game {ticket.GameId}, bundle {ticket.BundleId}, bin {activeBundle.Bin}, sold {line.Ticket}, quantity {line.Quantity.ToString(CultureInfo.InvariantCulture)}, amount {line.AmountText}; placement retained as sold out"
                    : $"Game {ticket.GameId}, bundle {ticket.BundleId}, bin {activeBundle.Bin}, sold {line.Ticket}, quantity {line.Quantity.ToString(CultureInfo.InvariantCulture)}, amount {line.AmountText}, next {backfill.NextTicket}");
            if (backfill.IsBundleComplete)
                _ = SpeakAsync("Bundle sold out.");
            RefreshTotals();
            RefreshOperationalPages();
        }
        catch (Exception ex)
        {
            AppLog.Error("Scanner sale processing failed.", ex);
            DashboardScannerStatusText.Text = "Scanner sale failed.";
            StatusText.Text = $"Unable to record scanner sale: {ex.Message}";
        }
    }

    private async Task PromptForActivationBinAsync(ImportTicket ticket)
    {
        ClearDashboardPendingScanPair();
        DashboardScannerStatusText.Text = $"Bundle {ticket.BundleId} is not placed. Enter or scan bin.";
        DashboardLastScanText.Text = $"Game {ticket.GameId} | Bundle {ticket.BundleId} | waiting for bin";
        StatusText.Text = "Bundle is not placed. Enter a bin number or scan a bin barcode.";
        _ = SpeakAsync("Enter bin number or scan bin.");

        var selection = await ShowActivationBinDialogAsync(ticket);
        if (selection is null)
        {
            DashboardScannerStatusText.Text = "Bundle activation cancelled.";
            StatusText.Text = "Bundle activation cancelled.";
            return;
        }

        var bin = selection.BinNumber.ToString(CultureInfo.InvariantCulture);
        await ActivateBundleInBinAsync(bin, ticket, updateDashboardStatus: true, selection.PriceCents);
    }

    private async Task<ActivationBinSelection?> ShowActivationBinDialogAsync(ImportTicket ticket)
    {
        RestoreForScannerWorkflowDialog("bundle activation bin selection");

        var binBox = new TextBox
        {
            Header = "Bin number",
            PlaceholderText = "Enter bin number or scan BIN barcode"
        };
        var statusText = new TextBlock
        {
            Text = $"Bundle {ticket.BundleId} is not active. Enter the destination bin or scan its bin barcode.",
            TextWrapping = TextWrapping.Wrap
        };
        int? selectedBin = null;
        long? scannedPriceCents = null;
        var commandScanBuffer = new StringBuilder();

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Select Bin",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Game {ticket.GameId} | Bundle {ticket.BundleId} | Ticket {ticket.Ticket}",
                        TextWrapping = TextWrapping.Wrap
                    },
                    binBox,
                    statusText
                }
            },
            PrimaryButtonText = "Activate Bundle",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        bool TryAcceptBin()
        {
            if (!TryParseBinNumber(binBox.Text, out var parsedBin))
            {
                statusText.Text = "Enter or scan a valid bin barcode.";
                return false;
            }

            if (!IsConfiguredBin(parsedBin))
            {
                statusText.Text = $"Wrong bin {parsedBin.ToString(CultureInfo.CurrentCulture)}. Enter or scan a configured bin.";
                _ = SpeakAsync("Wrong bin.");
                return false;
            }

            selectedBin = parsedBin;
            return true;
        }

        binBox.AddHandler(
            UIElement.KeyDownEvent,
            new KeyEventHandler((_, args) =>
            {
                if (_scannerInput.IsActivelyCapturing)
                    return;

                ObserveFocusedCommandScanKey(args, commandScanBuffer, scan =>
                {
                    if (scan.Kind == ScanKind.Price)
                        binBox.Text = string.Empty;
                    return _scannerScanOverride?.Invoke(scan) == true;
                });
            }),
            handledEventsToo: true);
        binBox.KeyDown += (_, e) =>
        {
            if (e.Handled)
                return;

            if (e.Key != VirtualKey.Enter)
                return;

            e.Handled = true;
            if (TryAcceptBin())
                dialog.Hide();
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (TryAcceptBin())
                return;

            args.Cancel = true;
        };
        dialog.Opened += (_, _) =>
        {
            _ = binBox.Focus(FocusState.Programmatic);
        };

        _isWorkflowDialogOpen = true;
        var previousScannerOverride = _scannerScanOverride;
        _scannerScanOverride = scan =>
        {
            if (scan.Kind == ScanKind.Bin)
            {
                binBox.Text = scan.BinNumber!.Value.ToString(CultureInfo.InvariantCulture);
                if (TryAcceptBin())
                    dialog.Hide();
                return true;
            }

            if (scan.Kind == ScanKind.Price)
            {
                scannedPriceCents = scan.PriceCents;
                statusText.Text = $"Price {MoneyText(scan.PriceCents ?? 0)} captured. Scan bin.";
                TryRecordAudit("scanner", "Price scan captured", $"Price {MoneyText(scan.PriceCents ?? 0)} pending for game {ticket.GameId}");
                return true;
            }

            statusText.Text = "Scan a bin barcode or price label.";
            TryRecordAudit("scanner", "Scan rejected", $"Expected bin or price command: {scan.Raw}");
            _ = SpeakAsync("Scan again.");
            return true;
        };
        try
        {
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary || selectedBin is not null)
                return selectedBin is null ? null : new ActivationBinSelection(selectedBin.Value, scannedPriceCents);

            return null;
        }
        finally
        {
            commandScanBuffer.Clear();
            _scannerScanOverride = previousScannerOverride;
            _isWorkflowDialogOpen = false;
        }
    }

    private async void PlaceDashboardBundle(string bin, ImportTicket ticket, long? scannedPriceCents = null)
    {
        await ActivateBundleInBinAsync(bin, ticket, updateDashboardStatus: true, scannedPriceCents);
    }

    private async Task<bool> ActivateBundleInBinAsync(
        string bin,
        ImportTicket ticket,
        bool updateDashboardStatus,
        long? scannedPriceCents = null)
    {
        if (!EnsureLicenseAllowsOperation("activating bundles"))
            return false;

        if (!int.TryParse(bin, NumberStyles.None, CultureInfo.InvariantCulture, out var binNumber) ||
            !IsConfiguredBin(binNumber))
        {
            if (updateDashboardStatus)
            {
                DashboardScannerStatusText.Text = $"Wrong bin {bin}. Scan a valid bin.";
                DashboardLastScanText.Text = $"Last scan failed: BIN-{bin}";
                ClearDashboardPendingScanPair();
            }

            StatusText.Text = $"Wrong bin {bin}.";
            _ = SpeakAsync("Wrong bin.");
            return false;
        }

        var activeBundle = FindActiveBundle(ticket);
        if (activeBundle is not null)
        {
            if (updateDashboardStatus)
            {
                DashboardScannerStatusText.Text = $"Bundle already active in bin {activeBundle.Bin}.";
                DashboardLastScanText.Text = $"Game {ticket.GameId} | Bundle {ticket.BundleId} | active in bin {activeBundle.Bin}";
                ClearDashboardPendingScanPair();
            }

            StatusText.Text = $"Bundle active in bin {activeBundle.Bin}; move it manually.";
            _ = SpeakAsync($"Bundle active in bin {activeBundle.Bin}, move it manually.");
            return false;
        }

        var placedBundle = FindPlacedBundle(ticket);
        if (placedBundle?.IsSoldOut == true)
        {
            StatusText.Text = $"Bundle {ticket.BundleId} in bin {placedBundle.Bin} is sold out.";
            _ = SpeakAsync("Bundle sold out.");
            return false;
        }

        if (!HasCompleteGameSetup(ticket.GameId))
        {
            var setupSaved = await ShowActivationGameSetupDialogAsync(bin, ticket, scannedPriceCents);
            if (!setupSaved)
            {
                if (updateDashboardStatus)
                {
                    DashboardScannerStatusText.Text = $"Game setup required for game {ticket.GameId}. Bundle not activated.";
                    DashboardLastScanText.Text = $"Game {ticket.GameId} | Bundle {ticket.BundleId} | setup incomplete";
                    ClearDashboardPendingScanPair();
                }

                StatusText.Text = $"Enter the ticket price before activating game {ticket.GameId}.";
                _ = SpeakAsync("Game setup required.");
                return false;
            }
        }

        if (!TryBuildActivationGapFillSale(
                DateTime.Now,
                ticket,
                bin,
                out var activationSale,
                out var configurationError))
        {
            if (updateDashboardStatus)
            {
                DashboardScannerStatusText.Text = $"Game {ticket.GameId} setup is invalid. Bundle not activated.";
                DashboardLastScanText.Text = $"Game {ticket.GameId} | Bundle {ticket.BundleId} | invalid setup";
                ClearDashboardPendingScanPair();
            }

            StatusText.Text = $"Bundle {ticket.BundleId} was not activated: {configurationError}";
            TryRecordAudit(
                "activation",
                "Bundle activation blocked",
                $"Game {ticket.GameId}, bundle {ticket.BundleId}, bin {bin}: {configurationError}");
            _ = SpeakAsync("Game setup required.");
            return false;
        }

        var line = new ImportLine(
            ticket.GameId,
            ticket.BundleId,
            activationSale.IsBundleComplete ? ticket.Ticket : activationSale.NextTicket,
            bin,
            "activation",
            activationSale.IsBundleComplete);
        _imports.Insert(0, line);
        _sales.Insert(0, activationSale.Sale);
        if (!SaveImportLineAndSale(line, activationSale.Sale))
        {
            _imports.Remove(line);
            _sales.Remove(activationSale.Sale);
            return false;
        }

        var receivedBundle = _receivedBundles.FirstOrDefault(received =>
            string.Equals(received.GameId, ticket.GameId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(received.BundleId, ticket.BundleId, StringComparison.OrdinalIgnoreCase));
        if (receivedBundle is not null)
            _receivedBundles.Remove(receivedBundle);
        _activations.Insert(0, new ActivationLine(
            DateTime.Now,
            ticket.GameId,
            ticket.BundleId,
            bin,
            "activation",
            _openIntervalId,
            _activeActorId,
            _activeUserName));

        TryRecordAudit(
            "activation",
            "Bundle activated",
            $"Game {ticket.GameId}, bundle {ticket.BundleId}, scanned ticket {ticket.Ticket}, {(activationSale.IsBundleComplete ? "sold out" : $"next {activationSale.NextTicket}")}, bin {bin}");
        TryRecordAudit(
            "sale",
            "Activation sale recorded",
            $"Game {ticket.GameId}, bundle {ticket.BundleId}, bin {bin}, sold {activationSale.Sale.Ticket}, quantity {activationSale.Sale.Quantity.ToString(CultureInfo.InvariantCulture)}, amount {activationSale.Sale.AmountText}, {(activationSale.IsBundleComplete ? "sold out" : $"next {activationSale.NextTicket}")}");
        TryRecordAudit(
            "bin",
            "Bin placement recorded",
            $"Bin {bin}, game {ticket.GameId}, bundle {ticket.BundleId}, {(activationSale.IsBundleComplete ? "sold out" : $"next ticket {activationSale.NextTicket}")}");
        if (updateDashboardStatus)
        {
            DashboardScannerStatusText.Text = $"Bundle activated in bin {bin}.";
            DashboardLastScanText.Text = $"Game {ticket.GameId} | Bundle {ticket.BundleId} | Sold {activationSale.Sale.Ticket} | Next {activationSale.NextTicket} | Bin {bin}";
            ClearDashboardPendingScanPair();
        }

        SalesListView.SelectedItem = activationSale.Sale;
        StatusText.Text = $"Bundle {ticket.BundleId} activated in bin {bin}. Sold {activationSale.Sale.Quantity.ToString(CultureInfo.CurrentCulture)} ticket{(activationSale.Sale.Quantity == 1 ? string.Empty : "s")}; next ticket {activationSale.NextTicket}.";
        _ = SpeakAsync($"Bundle activated in bin {bin}.");
        RefreshTotals();
        RefreshOperationalPages();
        _ = EnsureGameImageCachedForGameAsync(ticket.GameId);
        return true;
    }

    private void ExpireDashboardPendingScanPair()
    {
        var now = DateTime.UtcNow;
        var timeout = TimeSpan.FromSeconds(_scanPairTimeoutSeconds);
        var expiredBin = _dashboardPendingBinAtUtc is not null && now - _dashboardPendingBinAtUtc.Value > timeout;
        var expiredTicket = _dashboardPendingTicketAtUtc is not null && now - _dashboardPendingTicketAtUtc.Value > timeout;
        if (!expiredBin && !expiredTicket)
            return;

        ClearDashboardPendingScanPair();
        DashboardScannerStatusText.Text = $"Pending bin/bundle scan expired after {_scanPairTimeoutSeconds.ToString(CultureInfo.CurrentCulture)} seconds.";
        DashboardLastScanText.Text = "Last scan pair expired.";
        StatusText.Text = "Scan bin and bundle again within the activation scan timeout.";
    }

    private void ClearDashboardPendingScanPair()
    {
        _dashboardPendingBin = string.Empty;
        _dashboardPendingTicket = null;
        _dashboardPendingPriceCents = null;
        _dashboardPendingBinAtUtc = null;
        _dashboardPendingTicketAtUtc = null;
    }

    private async Task<bool> ShowActivationGameSetupDialogAsync(
        string bin,
        ImportTicket ticket,
        long? scannedPriceCents = null,
        string setupSource = "Activation")
    {
        var existingGame = FindKnownGame(ticket.GameId);
        if (HasCompleteGameSetup(ticket.GameId))
        {
            return true;
        }

        RestoreForScannerWorkflowDialog("game setup");

        existingGame ??= _gameCatalog.FirstOrDefault(g =>
            string.Equals(g.GameId, ticket.GameId, StringComparison.OrdinalIgnoreCase));
        var nameBox = new TextBox
        {
            Header = "Game name",
            Text = existingGame?.Name is { Length: > 0 } existingName ? existingName : $"Game {ticket.GameId}",
            PlaceholderText = "Display name"
        };
        var priceBox = new NumberBox
        {
            Header = "Game price ($)",
            Value = scannedPriceCents is > 0
                ? scannedPriceCents.Value / 100d
                : existingGame?.PriceCents > 0 ? existingGame.PriceCents / 100d : double.NaN,
            Minimum = 1,
            SmallChange = 1,
            LargeChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        var statusText = new TextBlock
        {
            Text = "Enter the ticket price. Bundle total is automatic: $900 for a $50 ticket; otherwise $500.",
            TextWrapping = TextWrapping.Wrap
        };
        var priceScanBuffer = new StringBuilder();
        priceBox.AddHandler(
            UIElement.KeyDownEvent,
            new KeyEventHandler((_, args) =>
            {
                if (_useFocusedScannerCapture || !_scannerInput.IsActivelyCapturing)
                    ObserveFocusedCommandScanKey(args, priceScanBuffer, scan =>
                        _scannerScanOverride?.Invoke(scan) == true);
            }),
            handledEventsToo: true);
        var imageUri = existingGame?.ImageUri ?? string.Empty;
        var image = string.IsNullOrWhiteSpace(imageUri)
            ? null
            : new Image
            {
                Source = new BitmapImage(new Uri(imageUri)),
                Width = 96,
                Height = 64,
                Stretch = Stretch.UniformToFill
            };

        var content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = $"Game {ticket.GameId} | Bundle {ticket.BundleId} | Bin {bin}",
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
        if (image is not null)
            content.Children.Add(image);
        content.Children.Add(nameBox);
        content.Children.Add(priceBox);
        content.Children.Add(statusText);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Game setup required",
            Content = content,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var priceCents = PriceCentsFromNumberBox(priceBox);
            var bundlePriceCents = AutomaticBundlePriceCents(priceCents);
            if (TryValidateGameTicketConfiguration(priceCents, bundlePriceCents, out var configurationError))
                return;

            args.Cancel = true;
            statusText.Text = configurationError;
        };

        _isWorkflowDialogOpen = true;
        var previousScannerOverride = _scannerScanOverride;
        _scannerScanOverride = scan =>
        {
            if (scan.Kind != ScanKind.Price)
            {
                statusText.Text = "Scan a price label, or enter the ticket price.";
                TryRecordAudit("scanner", "Scan rejected", $"Expected price command: {scan.Raw}");
                _ = SpeakAsync("Scan again.");
                return true;
            }

            priceBox.Value = (scan.PriceCents ?? 0) / 100d;
            statusText.Text = $"Price {MoneyText(scan.PriceCents ?? 0)} captured.";
            TryRecordAudit("scanner", "Price scan captured", $"Price {MoneyText(scan.PriceCents ?? 0)} for game {ticket.GameId}");
            return true;
        };
        try
        {
            _ = priceBox.Focus(FocusState.Programmatic);
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return false;
        }
        finally
        {
            priceScanBuffer.Clear();
            _scannerScanOverride = previousScannerOverride;
            _isWorkflowDialogOpen = false;
        }

        var priceCents = PriceCentsFromNumberBox(priceBox);
        var bundlePriceCents = AutomaticBundlePriceCents(priceCents);
        if (!TryValidateGameTicketConfiguration(priceCents, bundlePriceCents, out var configurationError))
        {
            StatusText.Text = $"Game {ticket.GameId} was not saved: {configurationError}";
            return false;
        }

        var name = string.IsNullOrWhiteSpace(nameBox.Text)
            ? $"Game {ticket.GameId}"
            : nameBox.Text.Trim();
        var record = new GameCatalogRecord(
            ticket.GameId,
            name,
            priceCents,
            bundlePriceCents,
            setupSource,
            existingGame?.ImageUri ?? "ms-appx:///Assets/SimpleLottoLogo64.png",
            existingGame?.ImageStatus ?? "Image not uploaded");

        return UpsertManualGameRecord(record);
    }

    private void AcceptImportBin(ImportBin bin)
    {
        if (_hasImportFailure)
        {
            StartupStatusText.Text = "Resolve the current import failure before selecting another bin.";
            _ = SpeakAsync("Resolve import failure.");
            return;
        }

        _pendingImportBin = bin;
        if (_pendingImportTicket is not null)
        {
            CompleteImportPair(bin, _pendingImportTicket);
            return;
        }

        UpdatePendingImportText();
        UpdateImportStatusText($"Bin {bin.Number} selected. Now scan ticket.");
        ImportScanStatusText.Text = $"Bin {bin.Number} captured. Waiting for ticket.";
        _ = SpeakAsync($"Bin {bin.Number}. Now scan ticket.");
    }

    private void AcceptImportTicket(ImportTicket ticket)
    {
        if (_hasImportFailure)
        {
            StartupStatusText.Text = "Resolve the current import failure before scanning another ticket.";
            _ = SpeakAsync("Resolve import failure.");
            return;
        }

        if (PhysicalBundleExists(ticket.GameId, ticket.BundleId))
        {
            _pendingImportTicket = null;
            _ = SpeakAsync("Duplicate");
            return;
        }

        _pendingImportTicket = ticket;
        if (_pendingImportBin is not null)
        {
            CompleteImportPair(_pendingImportBin, ticket);
            return;
        }

        UpdatePendingImportText();
        UpdateImportStatusText($"Ticket scanned for game {ticket.GameId}, bundle {ticket.BundleId}. Now scan or select bin.");
        ImportScanStatusText.Text = $"Ticket captured. Waiting for bin.";
        _ = SpeakAsync("Ticket scanned. Now scan bin.");
    }

    private void CompleteImportPair(ImportBin bin, ImportTicket ticket)
    {
        if (PhysicalBundleExists(ticket.GameId, ticket.BundleId))
        {
            _pendingImportTicket = null;
            UpdatePendingImportText();
            _ = SpeakAsync("Duplicate");
            return;
        }

        var line = new ImportLine(
            ticket.GameId,
            ticket.BundleId,
            ticket.Ticket,
            bin.Number.ToString(CultureInfo.InvariantCulture),
            "initial_import");
        _imports.Insert(0, line);
        if (!SaveImportLine(line))
        {
            _imports.Remove(line);
            return;
        }

        TryRecordAudit(
            "import",
            "Opening placement recorded",
            $"Bin {bin.Number.ToString(CultureInfo.InvariantCulture)}, game {ticket.GameId}, bundle {ticket.BundleId}, ticket {ticket.Ticket}");
        TryRecordAudit(
            "bin",
            "Bin placement recorded",
            $"Bin {bin.Number.ToString(CultureInfo.InvariantCulture)}, game {ticket.GameId}, bundle {ticket.BundleId}, ticket {ticket.Ticket}");
        bin.ImportedCount++;

        _pendingImportBin = null;
        _pendingImportTicket = null;
        ClearImportFailure();
        UpdatePendingImportText();
        UpdateImportStatusText($"Success. Imported game {ticket.GameId}, bundle {ticket.BundleId}, ticket {ticket.Ticket} in bin {bin.Number}.");
        ImportScanStatusText.Text = $"Imported bin {bin.Number}: game {ticket.GameId}, bundle {ticket.BundleId}, ticket {ticket.Ticket}.";
        _ = SpeakAsync($"Success. Bin {bin.Number} imported.");
        RefreshOperationalPages();
        _ = EnsureGameImageCachedForGameAsync(ticket.GameId);
    }

    private void FailImport(string message, string spoken)
    {
        _hasImportFailure = true;
        StartupPrimaryButton.IsEnabled = false;
        ImportFailureText.Text = message;
        ResolveImportFailureButton.Visibility = Visibility.Visible;
        StartupStatusText.Text = message;
        ImportScanStatusText.Text = message;
        _ = SpeakAsync(spoken);
    }

    private void ClearImportFailure()
    {
        _hasImportFailure = false;
        StartupPrimaryButton.IsEnabled = true;
        ImportFailureText.Text = string.Empty;
        ResolveImportFailureButton.Visibility = Visibility.Collapsed;
    }

    private void UpdatePendingImportText()
    {
        var binText = _pendingImportBin is null
            ? "No bin selected"
            : $"Bin {_pendingImportBin.Number} selected";
        var ticketText = _pendingImportTicket is null
            ? "no ticket scanned"
            : $"ticket {_pendingImportTicket.Ticket} for game {_pendingImportTicket.GameId}, bundle {_pendingImportTicket.BundleId}";
        ImportPendingText.Text = $"{binText}; {ticketText}.";
    }

    private void UpdateImportStatusText(string message)
    {
        StartupStatusText.Text = message;
        if (_imports.Count == 0)
            ImportInstructionText.Text = $"{_configuredBinCount} bins are ready. Click a bin to hear the scan prompt, or scan BIN and ticket barcodes in either order.";
        else
            ImportInstructionText.Text = $"{_imports.Count} import{(_imports.Count == 1 ? string.Empty : "s")} recorded. Continue when all physical bins are imported; any new Game ID pricing will be required before login.";
    }

    private IEnumerable<string> SplitImportScanInput(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            yield break;

        var pieces = raw.Split(
            new[] { ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (pieces.Length > 1)
        {
            foreach (var piece in pieces)
            {
                foreach (var nested in SplitImportScanInput(piece))
                    yield return nested;
            }

            yield break;
        }

        var compact = CompactScanStream(raw);
        if (compact.Length == 0)
            yield break;

        var segmented = SegmentCompactImportStream(compact);
        if (segmented.Count == 0)
        {
            yield return raw.Trim();
            yield break;
        }

        foreach (var segment in segmented)
            yield return segment;
    }

    private IReadOnlyList<string> SegmentCompactImportStream(string stream)
    {
        var layouts = CandidateBarcodeLayouts().ToArray();
        var memo = new Dictionary<int, List<string>?>();
        var segments = SegmentCompactImportStreamFrom(stream, 0, layouts, memo);
        return segments is null ? Array.Empty<string>() : segments;
    }

    private List<string>? SegmentCompactImportStreamFrom(
        string stream,
        int offset,
        IReadOnlyList<BarcodeLayout> layouts,
        Dictionary<int, List<string>?> memo)
    {
        if (offset == stream.Length)
            return new List<string>();
        if (memo.TryGetValue(offset, out var cached))
            return cached;

        foreach (var candidate in CandidateImportSegments(stream, offset, layouts))
        {
            var rest = SegmentCompactImportStreamFrom(
                stream,
                offset + candidate.Length,
                layouts,
                memo);
            if (rest is null)
                continue;

            var result = new List<string>(rest.Count + 1) { candidate };
            result.AddRange(rest);
            memo[offset] = result;
            return result;
        }

        memo[offset] = null;
        return null;
    }

    private IEnumerable<string> CandidateImportSegments(
        string stream,
        int offset,
        IReadOnlyList<BarcodeLayout> layouts)
    {
        const string binPrefix = "BIN-";
        if (StartsWithAt(stream, offset, binPrefix))
        {
            var digitStart = offset + binPrefix.Length;
            var maxDigits = Math.Min(4, stream.Length - digitStart);
            for (var digits = maxDigits; digits >= 1; digits--)
            {
                if (AllDigits(stream, digitStart, digits))
                    yield return stream.Substring(offset, binPrefix.Length + digits);
            }
        }

        foreach (var layout in layouts)
        {
            foreach (var length in layout.CandidateLengths)
            {
                if (offset + length > stream.Length)
                    continue;

                var segment = stream.Substring(offset, length);
                if (AllDigits(segment, 0, segment.Length) &&
                    layout.TryParse(segment) is not null)
                {
                    yield return segment;
                }
            }
        }
    }

    private static bool TryParseBinNumber(string raw, out int binNumber)
    {
        binNumber = 0;
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("BIN-", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(trimmed[4..], NumberStyles.None, CultureInfo.InvariantCulture, out binNumber);

        var digits = DigitsOnly(trimmed);
        return digits.Length is > 0 and <= 4 &&
               int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out binNumber);
    }

    private bool IsConfiguredBin(int binNumber) =>
        binNumber >= 1 && binNumber <= _configuredBinCount;

    private ImportTicket? TryParseImportTicket(string raw)
    {
        var digits = DigitsOnly(raw);
        if (digits.Length == 0)
            return null;

        foreach (var layout in CandidateBarcodeLayouts())
        {
            var hit = layout.TryParse(digits);
            if (hit is null)
                continue;

            return new ImportTicket(hit.Value.Game, hit.Value.Pack, hit.Value.Ticket, raw);
        }

        return null;
    }

    private IEnumerable<BarcodeLayout> CandidateBarcodeLayouts()
    {
        if (!string.IsNullOrWhiteSpace(_storeBarcodeLayout))
        {
            var selected = BarcodeLayouts.FirstOrDefault(l => l.Name == _storeBarcodeLayout);
            if (selected is not null)
            {
                yield return selected;
                yield break;
            }
        }

        foreach (var layout in BarcodeLayouts)
            yield return layout;
    }

    private static bool TryMapScanKey(VirtualKey key, out char c)
    {
        c = '\0';
        if (key is >= VirtualKey.A and <= VirtualKey.Z)
        {
            c = (char)('A' + (int)key - (int)VirtualKey.A);
            return true;
        }

        if (key is >= VirtualKey.Number0 and <= VirtualKey.Number9)
        {
            c = (char)('0' + (int)key - (int)VirtualKey.Number0);
            return true;
        }

        if (key is >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9)
        {
            c = (char)('0' + (int)key - (int)VirtualKey.NumberPad0);
            return true;
        }

        if (key == VirtualKey.Subtract || (int)key == 189)
        {
            c = '-';
            return true;
        }

        return false;
    }

    private static string CompactScanStream(string raw)
    {
        Span<char> buffer = stackalloc char[raw.Length];
        var count = 0;
        foreach (var c in raw)
        {
            if (char.IsWhiteSpace(c))
                continue;

            buffer[count++] = char.ToUpperInvariant(c);
        }

        return new string(buffer[..count]);
    }

    private static bool StartsWithAt(string value, int offset, string prefix) =>
        offset + prefix.Length <= value.Length &&
        string.Compare(value, offset, prefix, 0, prefix.Length, StringComparison.OrdinalIgnoreCase) == 0;

    private static bool AllDigits(string value, int offset, int length)
    {
        for (var i = offset; i < offset + length; i++)
        {
            if (value[i] is < '0' or > '9')
                return false;
        }

        return true;
    }

    private async Task SpeakAsync(string text)
    {
        if (!await _speechGate.WaitAsync(0))
            return;

        try
        {
            await PlaySpeechAsync(text, volume: 1.0, timeout: TimeSpan.FromSeconds(4));
        }
        catch
        {
            StatusText.Text = text;
        }
        finally
        {
            _speechGate.Release();
        }
    }

    private async Task WarmAudioEngineAsync()
    {
        await _speechGate.WaitAsync();
        try
        {
            await PlaySpeechAsync("Ready", volume: 0.0, timeout: TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Audio prompts are optional; scanner status text remains authoritative.
        }
        finally
        {
            _speechGate.Release();
        }
    }

    private async Task PlaySpeechAsync(string text, double volume, TimeSpan timeout)
    {
        var stream = await _speechSynthesizer.SynthesizeTextToStreamAsync(text);
        var done = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Ended(MediaPlayer sender, object args) => done.TrySetResult(null);
        void Failed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => done.TrySetResult(null);

        _speechPlayer.MediaEnded += Ended;
        _speechPlayer.MediaFailed += Failed;
        try
        {
            _speechPlayer.Volume = volume;
            _speechPlayer.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
            _speechPlayer.Play();
            await Task.WhenAny(done.Task, Task.Delay(timeout));
        }
        finally
        {
            _speechPlayer.MediaEnded -= Ended;
            _speechPlayer.MediaFailed -= Failed;
            _speechPlayer.Source = null;
            _speechPlayer.Volume = 1.0;
            stream.Dispose();
        }
    }

    private void MainWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowExit)
            return;

        if (!IsScannerPaired)
        {
            _allowExit = true;
            return;
        }

        args.Cancel = true;
        if (!EnsureTrayIcon())
        {
            StatusText.Text = "Tray icon could not be created. SimpleLotto remains open.";
            return;
        }

        HideToTray();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _rdisplay.DisplaysChanged -= Rdisplay_DisplaysChanged;
        DisposeLifetimeResources();
    }

    private void Rdisplay_DisplaysChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            RefreshRegisteredDisplayCards();
            RefreshSettingsSummary();
        });
    }

    private bool IsScannerPaired => _isScannerPaired;

    private void SetScannerPaired(bool isPaired)
    {
        _isScannerPaired = isPaired;
        DashboardPairingStatusText.Text = isPaired
            ? "Background capture: scanner paired"
            : "Background capture: not paired";
    }

    private bool EnsureTrayIcon()
    {
        if (_trayIconVisible)
            return true;

        SetWindowSubclass(_hwnd, _subclassProc, TraySubclassId, UIntPtr.Zero);
        _trayIconHandle = LoadTrayIcon();
        var data = CreateTrayData(TrayFlags.Message | TrayFlags.Icon | TrayFlags.Tip);
        _trayIconVisible = Shell_NotifyIcon(TrayMessages.Add, ref data);
        return _trayIconVisible;
    }

    private IntPtr LoadTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "SimpleLotto.ico");
        if (File.Exists(iconPath))
        {
            var icon = LoadImage(IntPtr.Zero, iconPath, ImageIcon, 16, 16, LoadFromFile);
            if (icon != IntPtr.Zero)
            {
                _trayIconLoadedFromFile = true;
                return icon;
            }
        }

        return LoadIcon(IntPtr.Zero, new IntPtr(32512));
    }

    private void HideToTray()
    {
        ShowWindow(_hwnd, 0);
        StatusText.Text = "SimpleLotto is running in the tray.";
        ShowTrayBalloon("SimpleLotto", "Scanner, display, and speech services are still running.");
    }

    private void RestoreFromTray()
    {
        ShowWindow(_hwnd, 5);
        SetForegroundWindow(_hwnd);
        StatusText.Text = "SimpleLotto restored.";
    }

    private void RestoreForScannerWorkflowDialog(string workflow)
    {
        ShowWindow(_hwnd, ShowWindowRestore);
        var foregrounded = SetForegroundWindow(_hwnd);
        AppLog.Info(foregrounded
            ? $"Restored SimpleLotto for scanner workflow: {workflow}."
            : $"Restored SimpleLotto for scanner workflow but Windows did not grant foreground focus: {workflow}.");
    }

    private async void RequestExitFromTray()
    {
        if (_sales.Count > 0)
        {
            RestoreFromTray();
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Exit SimpleLotto?",
                Content = "There is activity in the current open close interval. Exit only if scanner and display service downtime is intended.",
                PrimaryButtonText = "Exit",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return;
        }

        _allowExit = true;
        RemoveTrayIcon();
        Close();
    }

    private void DisposeLifetimeResources()
    {
        RemoveTrayIcon();
        _scannerInput.ScanReceived -= ScannerInput_ScanReceived;
        _scannerInput.CaptureAvailabilityChanged -= ScannerInput_CaptureAvailabilityChanged;
        _scannerInput.Dispose();
        _speechPlayer.Dispose();
        _speechSynthesizer.Dispose();
        _speechGate.Dispose();
        _updates.Dispose();
    }

    private void RemoveTrayIcon()
    {
        if (_trayIconVisible)
        {
            var data = CreateTrayData(0);
            Shell_NotifyIcon(TrayMessages.Delete, ref data);
            _trayIconVisible = false;
        }

        RemoveWindowSubclass(_hwnd, _subclassProc, TraySubclassId);
        if (_trayIconLoadedFromFile && _trayIconHandle != IntPtr.Zero)
            DestroyIcon(_trayIconHandle);
        _trayIconHandle = IntPtr.Zero;
        _trayIconLoadedFromFile = false;
    }

    private void ShowTrayBalloon(string title, string message)
    {
        if (!_trayIconVisible)
            return;

        var data = CreateTrayData(TrayFlags.Info);
        data.InfoTitle = title;
        data.Info = message;
        data.InfoFlags = 1;
        Shell_NotifyIcon(TrayMessages.Modify, ref data);
    }

    private NotifyIconData CreateTrayData(uint flags)
    {
        return new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            WindowHandle = _hwnd,
            Id = TrayIconId,
            Flags = flags,
            CallbackMessage = TrayCallbackMessage,
            IconHandle = _trayIconHandle,
            Tip = "SimpleLotto",
            TimeoutOrVersion = 0
        };
    }

    private IntPtr TraySubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr refData)
    {
        if (message == TrayCallbackMessage)
        {
            var trayMessage = unchecked((uint)lParam.ToInt64());
            if (trayMessage == WindowMessages.LeftButtonDoubleClick)
            {
                RestoreFromTray();
                return IntPtr.Zero;
            }

            if (trayMessage == WindowMessages.RightButtonUp)
            {
                ShowTrayMenu();
                return IntPtr.Zero;
            }
        }

        return DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private void ShowTrayMenu()
    {
        if (!GetCursorPos(out var point))
            return;

        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;

        try
        {
            AppendMenu(menu, MenuFlags.String, RestoreCommandId, "Restore");
            AppendMenu(menu, MenuFlags.String, ExitCommandId, "Exit");
            SetForegroundWindow(_hwnd);
            var command = TrackPopupMenu(
                menu,
                MenuFlags.ReturnCommand | MenuFlags.RightButton,
                point.X,
                point.Y,
                0,
                _hwnd,
                IntPtr.Zero);

            if (command == RestoreCommandId.ToUInt32())
                RestoreFromTray();
            else if (command == ExitCommandId.ToUInt32())
                RequestExitFromTray();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void NavToggleButton_Click(object sender, RoutedEventArgs e)
    {
        SetNavCollapsed(!_isNavCollapsed);
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width < 1080 && !_isNavCollapsed)
            SetNavCollapsed(true);

        RecalculateInventoryPageSizes();
    }

    private void InventoryPagedList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RecalculateInventoryPageSizes();
    }

    private void PagedList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RecalculateInventoryPageSizes();
    }

    private void SetNavCollapsed(bool collapsed)
    {
        _isNavCollapsed = collapsed;
        NavColumn.Width = new GridLength(_isNavCollapsed ? 64 : 190);
        NavToggleIcon.Glyph = _isNavCollapsed ? "\uE76C" : "\uE76B";
        ToolTipService.SetToolTip(NavToggleButton, _isNavCollapsed ? "Expand navigation" : "Collapse navigation");
        var labelVisibility = _isNavCollapsed ? Visibility.Collapsed : Visibility.Visible;
        DashboardNavLabel.Visibility = labelVisibility;
        BinsNavLabel.Visibility = labelVisibility;
        InventoryNavLabel.Visibility = labelVisibility;
        ClosingNavLabel.Visibility = labelVisibility;
        SettingsNavLabel.Visibility = labelVisibility;
    }

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string section })
            return;

        ShowSection(section);
    }

    private void ShowSection(string section)
    {
        if (section == "Dashboard")
        {
            DashboardContent.Visibility = Visibility.Visible;
            SectionContent.Visibility = Visibility.Collapsed;
            BinsContent.Visibility = Visibility.Collapsed;
            InventoryContent.Visibility = Visibility.Collapsed;
            ClosingContent.Visibility = Visibility.Collapsed;
            SettingsContent.Visibility = Visibility.Collapsed;
            SetSelectedNav(section);
            StatusText.Text = "Dashboard";
            return;
        }

        RefreshOperationalPages();
        DashboardContent.Visibility = Visibility.Collapsed;
        SectionContent.Visibility = Visibility.Visible;
        BinsContent.Visibility = section == "Bins" ? Visibility.Visible : Visibility.Collapsed;
        InventoryContent.Visibility = section == "Inventory" ? Visibility.Visible : Visibility.Collapsed;
        ClosingContent.Visibility = section == "Closing" ? Visibility.Visible : Visibility.Collapsed;
        SettingsContent.Visibility = section == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        if (section == "Settings" && ReferenceEquals(SettingsTabs.SelectedItem, AuditSettingsTab))
        {
            ApplyAuditLogPage(resetPage: _auditLogPageDirty);
            _auditLogPageDirty = false;
        }
        SetSelectedNav(section);
        StatusText.Text = section;
    }

    private void SetSelectedNav(string section)
    {
        DashboardNavLabel.FontWeight = section == "Dashboard" ? FontWeights.SemiBold : FontWeights.Normal;
        BinsNavLabel.FontWeight = section == "Bins" ? FontWeights.SemiBold : FontWeights.Normal;
        InventoryNavLabel.FontWeight = section == "Inventory" ? FontWeights.SemiBold : FontWeights.Normal;
        ClosingNavLabel.FontWeight = section == "Closing" ? FontWeights.SemiBold : FontWeights.Normal;
        SettingsNavLabel.FontWeight = section == "Settings" ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void VoidSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLicenseAllowsOperation("voiding sales"))
            return;

        if (SalesListView.SelectedItem is not SaleLine sale)
        {
            StatusText.Text = "Select a sale to void.";
            return;
        }

        if (string.Equals(sale.Source, "undo", StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "A correction cannot be voided. Use the manager correction workflow for a new adjustment.";
            return;
        }

        var saleKey = SaleIdentity(sale);
        if ((sale.Id > 0 && _voidedSaleIds.Contains(sale.Id)) || _voidedSaleKeys.Contains(saleKey))
        {
            StatusText.Text = "This sale was already voided.";
            return;
        }

        var correction = new SaleLine(
            DateTime.Now,
            sale.GameId,
            sale.Bin,
            sale.Ticket,
            -sale.Quantity,
            -sale.Amount,
            "undo",
            sale.BundleId);
        var persistedCorrection = SaveVoid(sale, correction, saleKey);
        if (persistedCorrection is null)
            return;

        _sales.Insert(0, persistedCorrection);

        TryRecordAudit("correction", "Sale voided", $"Game {sale.GameId}, ticket {sale.Ticket}, bin {sale.Bin}");
        StatusText.Text = $"Voided game {sale.GameId} sale for {sale.AmountText}.";
        RefreshTotals();
        RefreshBinCards();
    }

    private async void CloseShiftButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLicenseAllowsOperation("closing shifts"))
            return;

        ShowSection("Closing");
        await StartClosingScanWorkflowAsync();
    }

    private async void ScanNewInventoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLicenseAllowsOperation("receiving inventory"))
            return;

        ShowSection("Inventory");
        InventoryTabs.SelectedItem = ReceivingTab;
        await StartReceivingScanWorkflowAsync();
    }

    private bool SaveImportLine(ImportLine line)
    {
        try
        {
            _store.InsertImport(new StoredImportLine(line.GameId, line.BundleId, line.Ticket, line.Bin, line.Source, line.IsSoldOut));
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Unable to save placement to SQLite: {ex.Message}";
            return false;
        }
    }

    private bool SaveImportLineAndSale(ImportLine importLine, SaleLine saleLine)
    {
        try
        {
            var inserted = _store.InsertImportAndSale(
                new StoredImportLine(
                    importLine.GameId,
                    importLine.BundleId,
                    importLine.Ticket,
                    importLine.Bin,
                    importLine.Source,
                    importLine.IsSoldOut),
                ToStoredSaleLine(saleLine));
            var persisted = FromStoredSaleLine(inserted);
            ReplaceSaleLine(_sales, saleLine, persisted);
            _allSales.Insert(0, persisted);
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Unable to save activation sale to SQLite: {ex.Message}";
            return false;
        }
    }

    private SaleLine? SaveVoid(SaleLine original, SaleLine correction, string saleKey)
    {
        try
        {
            var inserted = _store.InsertVoid(ToStoredSaleLine(original), ToStoredSaleLine(correction), saleKey);
            var persisted = FromStoredSaleLine(inserted);
            _allSales.Insert(0, persisted);
            _voidedSaleKeys.Add(saleKey);
            _voidedSaleIds.Add(original.Id);
            return persisted;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message.Contains("already been voided", StringComparison.OrdinalIgnoreCase)
                ? "This sale was already voided."
                : $"Unable to void sale in SQLite: {ex.Message}";
            return null;
        }
    }

    private SaleLine? SaveSaleLineAndUpdateImportTicket(SaleLine line, ImportLine updatedBundle)
    {
        try
        {
            var inserted = _store.InsertSaleAndUpdateImportTicket(
                ToStoredSaleLine(line),
                new StoredImportLine(
                    updatedBundle.GameId,
                    updatedBundle.BundleId,
                    updatedBundle.Ticket,
                    updatedBundle.Bin,
                    updatedBundle.Source,
                    updatedBundle.IsSoldOut));
            var persisted = FromStoredSaleLine(inserted);
            ReplaceSaleLine(_sales, line, persisted);
            _allSales.Insert(0, persisted);
            return persisted;
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("already recorded", StringComparison.OrdinalIgnoreCase))
            {
                DashboardScannerStatusText.Text = "Scan error. Ticket already recorded.";
                StatusText.Text = "Ticket already recorded. Scan again.";
                TryRecordAudit("sale", "Sale rejected", ex.Message);
                _ = SpeakAsync("Scan again.");
            }
            else
            {
                StatusText.Text = $"Unable to save sale and ticket state to SQLite: {ex.Message}";
            }
            return null;
        }
    }

    private bool SaveManualGame(GameCatalogRecord game)
    {
        try
        {
            _store.UpsertManualGame(new StoredGameRecord(
                game.GameId,
                game.Name,
                game.PriceCents,
                game.BundlePriceCents,
                _globalFirstTicketSerial,
                game.Source,
                game.ImageUri,
                game.ImageStatus));
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Unable to save game to SQLite: {ex.Message}";
            return false;
        }
    }

    private bool UpsertManualGameRecord(GameCatalogRecord game)
    {
        game = game with
        {
            BundlePriceCents = AutomaticBundlePriceCents(game.PriceCents)
        };
        if (!SaveManualGame(game))
            return false;

        var existing = _manualGameCatalog.FindIndex(g =>
            string.Equals(g.GameId, game.GameId, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
            _manualGameCatalog[existing] = game;
        else
            _manualGameCatalog.Add(game);

        ReopenBundlesExtendedByGameSetup(game);

        RefreshGameCatalog();
        SelectGame(game.GameId);
        SyncRdisplayTiles();
        TryRecordAudit(
            "inventory",
            "Game setup saved",
            $"Game {game.GameId}, name {game.Name}, ticket price {MoneyText(game.PriceCents)}, automatic bundle total {MoneyText(game.BundlePriceCents)}, global first ticket {(_globalFirstTicketSerial == 1 ? "001" : "000")}, source {game.Source}, image status {game.ImageStatus}");
        return true;
    }

    private void SelectGame(string gameId)
    {
        var filtered = FilteredGameCatalog();
        var index = filtered.FindIndex(g =>
            string.Equals(g.GameId, gameId, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            GameSearchBox.Text = gameId;
            filtered = FilteredGameCatalog();
            index = filtered.FindIndex(g =>
                string.Equals(g.GameId, gameId, StringComparison.OrdinalIgnoreCase));
        }

        if (index < 0)
            return;

        _gameCatalogPage = (index / Math.Max(1, _gameCatalogPageSize)) + 1;
        ApplyGameCatalogPage();
        var match = _pagedGameCatalog.FirstOrDefault(g =>
            string.Equals(g.GameId, gameId, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            GameCatalogListView.SelectedItem = match;
    }

    private StoredSaleLine ToStoredSaleLine(SaleLine line) =>
        new(
            line.SoldAt.ToUniversalTime(),
            line.GameId,
            line.Bin,
            line.Ticket,
            line.Quantity,
            (long)Math.Round(line.Amount * 100m, MidpointRounding.AwayFromZero),
            line.Source,
            line.BundleId,
            line.Id,
            line.IntervalId > 0 ? line.IntervalId : _openIntervalId,
            string.IsNullOrWhiteSpace(line.ActorId) ? _activeActorId : line.ActorId,
            string.IsNullOrWhiteSpace(line.ActorName) ? _activeUserName : line.ActorName,
            line.CorrectsSaleId,
            line.MigrationState);

    private static SaleLine FromStoredSaleLine(StoredSaleLine line) =>
        new(
            line.SoldAtUtc.ToLocalTime(),
            line.GameId,
            line.Bin,
            line.Ticket,
            line.Quantity,
            line.AmountCents / 100m,
            line.Source,
            line.BundleId,
            line.Id,
            line.IntervalId,
            line.ActorId,
            line.ActorName,
            line.CorrectsSaleId,
            line.MigrationState);

    private static void ReplaceSaleLine(
        ObservableCollection<SaleLine> rows,
        SaleLine original,
        SaleLine replacement)
    {
        var index = rows.IndexOf(original);
        if (index >= 0)
            rows[index] = replacement;
    }

    private static string SaleIdentity(SaleLine line) =>
        line.Id > 0
            ? $"sale:{line.Id.ToString(CultureInfo.InvariantCulture)}"
            : string.Join("|",
            line.SoldAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            line.GameId,
            line.Bin,
            line.Ticket,
            line.Quantity.ToString(CultureInfo.InvariantCulture),
            ((long)Math.Round(line.Amount * 100m, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture),
            line.Source);

    private static string SaleSourceLabel(string source) =>
        source switch
        {
            "normal_sale" => "Sale",
            "undo" => "Correction",
            "closing_gap_fill_sold" => "Closing gap-fill",
            "activation_gap_fill" => "Activation gap-fill",
            _ when string.IsNullOrWhiteSpace(source) => "Sale",
            _ => source
        };

    private void RefreshTotals()
    {
        var salesCount = _sales.Count;
        var ticketCount = _sales.Sum(s => s.Quantity);
        var revenue = CurrentShiftSalesAmount();

        SalesSubtitleText.Text = salesCount == 0
            ? "No entries yet"
            : $"{salesCount} sale entr{(salesCount == 1 ? "y" : "ies")}";
        RevenueText.Text = revenue.ToString("C", CultureInfo.CurrentCulture);
        TicketsText.Text = ticketCount.ToString(CultureInfo.CurrentCulture);
        GameMixText.Text = BuildGameMixText();
        RefreshBinsShiftSalesMetric(revenue);
        RefreshClosingMetricCards();
    }

    private decimal CurrentShiftSalesAmount() =>
        _sales.Sum(s => s.Amount);

    private void RefreshBinsShiftSalesMetric(decimal? revenue = null)
    {
        BinsShiftSalesText.Text = (revenue ?? CurrentShiftSalesAmount()).ToString("C", CultureInfo.CurrentCulture);
    }

    private void RefreshClosingMetricCards()
    {
        if (_selectedClosingReport is { } selected)
        {
            ClosingSalesText.Text = selected.SalesText;
            ClosingTicketsText.Text = selected.TicketText;
            ClosingEvidenceText.Text = selected.BinText;
            ClosingActivatedText.Text = selected.ActivatedBundles.ToString(CultureInfo.CurrentCulture);
            ClosingExpectedCashText.Text = selected.ExpectedCashText;
            return;
        }

        ClosingSalesText.Text = _sales.Sum(s => s.Amount).ToString("C", CultureInfo.CurrentCulture);
        ClosingTicketsText.Text = _sales.Sum(s => s.Quantity).ToString(CultureInfo.CurrentCulture);
        ClosingActivatedText.Text = CurrentShiftActivationCount().ToString(CultureInfo.CurrentCulture);
        ClosingExpectedCashText.Text = MoneyText(CurrentClosingExpectedCashCents());
    }

    private int CurrentShiftActivationCount() =>
        _activations.Count(activation => activation.IntervalId == _openIntervalId);

    private long CurrentClosingExpectedCashCents()
    {
        var instantTicketSalesCents = (long)Math.Round(_sales.Sum(s => s.Amount) * 100m, MidpointRounding.AwayFromZero);
        instantTicketSalesCents += _closingScanSales.Sum(s =>
            (long)Math.Round(s.Sale.Amount * 100m, MidpointRounding.AwayFromZero));
        if (_closingScanCaptured &&
            _closingScanIssues.Count == 0 &&
            _closingUnmatchedTickets.Count == 0 &&
            TryBuildClosingSoldOutChanges(
                ClosingSoldOutBundles(),
                DateTime.UtcNow,
                out var projectedSales,
                out _,
                out _))
        {
            instantTicketSalesCents += projectedSales
                .Sum(sale => (long)Math.Round(sale.Amount * 100m, MidpointRounding.AwayFromZero));
        }

        TryReadMoneyCentsOrZero(ClosingOnlineSaleBox, out var onlineSaleCents);
        TryReadMoneyCentsOrZero(ClosingOnlineCashoutBox, out var onlineCashoutCents);
        TryReadMoneyCentsOrZero(ClosingInstantCashoutBox, out var instantCashoutCents);
        return instantTicketSalesCents + onlineSaleCents - instantCashoutCents - onlineCashoutCents;
    }

    private void RefreshOperationalPages()
    {
        RefreshBinCards();
        RefreshInventoryRecords();
        RefreshGameCatalog();
        SyncRdisplayTiles();
        RefreshClosingBins();
        RefreshSettingsSummary();
        RefreshClosingActionState();
    }

    private void RefreshBinCards()
    {
        _binCards.Clear();
        var grouped = _imports
            .GroupBy(i => i.Bin)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var salesByGame = _sales
            .GroupBy(s => s.GameId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(s => s.Quantity), StringComparer.OrdinalIgnoreCase);
        var maxGameSales = salesByGame.Count == 0 ? 0 : salesByGame.Values.Max();
        var highThreshold = maxGameSales == 0 ? int.MaxValue : Math.Max(1, (int)Math.Ceiling(maxGameSales * 0.66));
        var mediumThreshold = maxGameSales == 0 ? int.MaxValue : Math.Max(1, (int)Math.Ceiling(maxGameSales * 0.33));

        for (var i = 1; i <= _configuredBinCount; i++)
        {
            var bin = i.ToString(CultureInfo.InvariantCulture);
            grouped.TryGetValue(bin, out var bundles);
            var current = bundles?.FirstOrDefault();
            var gameSales = current is not null && salesByGame.TryGetValue(current.GameId, out var count)
                ? count
                : 0;
            _binCards.Add(BinCard.From(
                i,
                current,
                bundles?.Count ?? 0,
                current is null ? string.Empty : GameDisplayName(current.GameId),
                ActivityForGameSales(gameSales, mediumThreshold, highThreshold)));
        }

        var activeBins = _binCards.Count(b => b.BundleCount > 0);
        BinsTotalText.Text = _configuredBinCount.ToString(CultureInfo.CurrentCulture);
        BinsActiveText.Text = activeBins.ToString(CultureInfo.CurrentCulture);
        BinsBundleText.Text = _imports.Count.ToString(CultureInfo.CurrentCulture);
        RefreshBinsShiftSalesMetric();

        if (_selectedBinBundles.Count == 0)
            BinDetailText.Text = "Bin Details (0 bundles)";
    }

    private void RefreshInventoryRecords()
    {
        _receivingRecords.Clear();
        _inventoryRecords.Clear();
        foreach (var bundle in _receivedBundles)
        {
            _receivingRecords.Add(new InventoryRecord(
                bundle.ReceivedAt.ToString("g", CultureInfo.CurrentCulture),
                bundle.GameId,
                bundle.BundleId,
                string.Empty,
                string.Empty,
                "Unopened"));
        }

        foreach (var line in _imports)
            _inventoryRecords.Add(new InventoryRecord(
                PlacementSourceLabel(line.Source),
                line.GameId,
                line.BundleId,
                line.Ticket,
                line.Bin,
                $"Active in bin {line.Bin}"));

        ApplyReceivingPage();
        ApplyInventoryPage();
        RefreshInventoryMetricCards();
    }

    private void RefreshInventoryMetricCards()
    {
        InventoryReceivingCountText.Text = _receivingRecords.Count.ToString(CultureInfo.CurrentCulture);
        InventoryBundleCountText.Text = _inventoryRecords.Count.ToString(CultureInfo.CurrentCulture);
    }

    private void RefreshGameCatalog()
    {
        _gameCatalog.Clear();
        var byGame = new Dictionary<string, GameCatalogRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var manual in _manualGameCatalog)
            byGame[manual.GameId] = manual;

        foreach (var import in _imports)
        {
            if (byGame.ContainsKey(import.GameId))
                continue;

            byGame[import.GameId] = GameCatalogRecord.FromImport(import.GameId);
        }

        foreach (var received in _receivedBundles)
        {
            if (!byGame.ContainsKey(received.GameId))
                byGame[received.GameId] = GameCatalogRecord.FromImport(received.GameId) with { Source = "Receiving" };
        }

        foreach (var game in byGame.Values.OrderBy(g => g.GameId, StringComparer.OrdinalIgnoreCase))
            _gameCatalog.Add(game);

        ApplyGameCatalogPage();
    }

    private void ApplyGameCatalogPage(bool resetPage = false)
    {
        if (resetPage)
            _gameCatalogPage = 1;

        var filtered = FilteredGameCatalog();
        var totalPages = TotalPages(filtered.Count, _gameCatalogPageSize);
        _gameCatalogPage = Math.Clamp(_gameCatalogPage, 1, totalPages);

        _pagedGameCatalog.Clear();
        foreach (var game in filtered
                     .Skip((_gameCatalogPage - 1) * _gameCatalogPageSize)
                     .Take(_gameCatalogPageSize))
        {
            _pagedGameCatalog.Add(game);
        }

        InventoryGameCountText.Text = $"{filtered.Count.ToString(CultureInfo.CurrentCulture)} of {_gameCatalog.Count.ToString(CultureInfo.CurrentCulture)} game{(_gameCatalog.Count == 1 ? string.Empty : "s")} defined";
        GamePageStatusText.Text = $"Page {_gameCatalogPage.ToString(CultureInfo.CurrentCulture)} of {totalPages.ToString(CultureInfo.CurrentCulture)}";
        GamePreviousPageButton.IsEnabled = _gameCatalogPage > 1;
        GameNextPageButton.IsEnabled = _gameCatalogPage < totalPages;
    }

    private List<GameCatalogRecord> FilteredGameCatalog()
    {
        var search = GameSearchBox.Text.Trim();
        return _gameCatalog
            .Where(g => string.IsNullOrWhiteSpace(search) ||
                        g.GameId.StartsWith(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(g => g.GameId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void ApplyReceivingPage(bool resetPage = false)
    {
        if (resetPage)
            _receivingPage = 1;

        var filtered = FilteredReceivingRecords();
        var totalPages = TotalPages(filtered.Count, _receivingPageSize);
        _receivingPage = Math.Clamp(_receivingPage, 1, totalPages);

        _pagedReceivingRecords.Clear();
        foreach (var record in filtered
                     .Skip((_receivingPage - 1) * _receivingPageSize)
                     .Take(_receivingPageSize))
        {
            _pagedReceivingRecords.Add(record);
        }

        ReceivingCountText.Text = $"{filtered.Count.ToString(CultureInfo.CurrentCulture)} of {_receivingRecords.Count.ToString(CultureInfo.CurrentCulture)} received bundle{(_receivingRecords.Count == 1 ? string.Empty : "s")}";
        ReceivingPageStatusText.Text = $"Page {_receivingPage.ToString(CultureInfo.CurrentCulture)} of {totalPages.ToString(CultureInfo.CurrentCulture)}";
        ReceivingPreviousPageButton.IsEnabled = _receivingPage > 1;
        ReceivingNextPageButton.IsEnabled = _receivingPage < totalPages;
    }

    private void ApplyInventoryPage(bool resetPage = false)
    {
        if (resetPage)
            _inventoryPage = 1;

        var filtered = FilteredInventoryRecords();
        var totalPages = TotalPages(filtered.Count, _inventoryPageSize);
        _inventoryPage = Math.Clamp(_inventoryPage, 1, totalPages);

        _pagedInventoryRecords.Clear();
        foreach (var record in filtered
                     .Skip((_inventoryPage - 1) * _inventoryPageSize)
                     .Take(_inventoryPageSize))
        {
            _pagedInventoryRecords.Add(record);
        }

        OpenBundleCountText.Text = $"{filtered.Count.ToString(CultureInfo.CurrentCulture)} of {_inventoryRecords.Count.ToString(CultureInfo.CurrentCulture)} open bundle{(_inventoryRecords.Count == 1 ? string.Empty : "s")}";
        OpenBundlePageStatusText.Text = $"Page {_inventoryPage.ToString(CultureInfo.CurrentCulture)} of {totalPages.ToString(CultureInfo.CurrentCulture)}";
        OpenBundlePreviousPageButton.IsEnabled = _inventoryPage > 1;
        OpenBundleNextPageButton.IsEnabled = _inventoryPage < totalPages;
    }

    private void ApplyClosingHistoryPage(bool resetPage = false)
    {
        if (resetPage)
            _closingHistoryPage = 1;

        var selected = ClosingHistoryListView.SelectedItem as ClosingHistoryRow;
        var filtered = FilteredClosingHistoryRows();
        var totalPages = TotalPages(filtered.Count, _closingHistoryPageSize);
        _closingHistoryPage = Math.Clamp(_closingHistoryPage, 1, totalPages);

        _pagedClosingHistoryRows.Clear();
        foreach (var row in filtered
                     .Skip((_closingHistoryPage - 1) * _closingHistoryPageSize)
                     .Take(_closingHistoryPageSize))
        {
            _pagedClosingHistoryRows.Add(row);
        }

        ClosingHistoryCountText.Text = $"{filtered.Count.ToString(CultureInfo.CurrentCulture)} of {_closingHistoryRows.Count.ToString(CultureInfo.CurrentCulture)} closing{(_closingHistoryRows.Count == 1 ? string.Empty : "s")}";
        ClosingHistoryPageStatusText.Text = $"Page {_closingHistoryPage.ToString(CultureInfo.CurrentCulture)} of {totalPages.ToString(CultureInfo.CurrentCulture)}";
        ClosingHistoryPreviousPageButton.IsEnabled = _closingHistoryPage > 1;
        ClosingHistoryNextPageButton.IsEnabled = _closingHistoryPage < totalPages;
        if (selected is not null && _pagedClosingHistoryRows.Contains(selected))
            ClosingHistoryListView.SelectedItem = selected;
        else if (selected is not null)
            ClearClosingReport();
    }

    private void ApplyAuditLogPage(bool resetPage = false)
    {
        if (resetPage)
            _auditLogPage = 1;

        var filtered = FilteredAuditLogRows();
        var totalPages = TotalPages(filtered.Count, _auditLogPageSize);
        _auditLogPage = Math.Clamp(_auditLogPage, 1, totalPages);

        _pagedAuditLogRows.Clear();
        foreach (var row in filtered
                     .Skip((_auditLogPage - 1) * _auditLogPageSize)
                     .Take(_auditLogPageSize))
        {
            _pagedAuditLogRows.Add(row);
        }

        var recentWindowText = _auditLogRows.Count == LocalStore.RecentAuditLogLimit
            ? $"latest {LocalStore.RecentAuditLogLimit.ToString(CultureInfo.CurrentCulture)}"
            : $"{_auditLogRows.Count.ToString(CultureInfo.CurrentCulture)} recent";
        AuditLogCountText.Text = $"{filtered.Count.ToString(CultureInfo.CurrentCulture)} matching of {recentWindowText} action{(_auditLogRows.Count == 1 ? string.Empty : "s")}";
        AuditLogPageStatusText.Text = $"Page {_auditLogPage.ToString(CultureInfo.CurrentCulture)} of {totalPages.ToString(CultureInfo.CurrentCulture)}";
        AuditLogPreviousPageButton.IsEnabled = _auditLogPage > 1;
        AuditLogNextPageButton.IsEnabled = _auditLogPage < totalPages;
    }

    private List<InventoryRecord> FilteredReceivingRecords()
    {
        var search = ReceivingSearchBox.Text.Trim();
        return _receivingRecords
            .Where(r => string.IsNullOrWhiteSpace(search) ||
                        r.GameId.StartsWith(search, StringComparison.OrdinalIgnoreCase) ||
                        r.BundleId.StartsWith(search, StringComparison.OrdinalIgnoreCase) ||
                        r.Ticket.StartsWith(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.GameId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.BundleId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<InventoryRecord> FilteredInventoryRecords() =>
        FilterOpenInventoryRecords(_inventoryRecords, OpenBundleSearchBox.Text.Trim());

    private static List<InventoryRecord> FilterOpenInventoryRecords(IEnumerable<InventoryRecord> records, string search) =>
        records
            .Where(r => string.IsNullOrWhiteSpace(search) ||
                        r.GameId.StartsWith(search, StringComparison.OrdinalIgnoreCase) ||
                        r.BundleId.StartsWith(search, StringComparison.OrdinalIgnoreCase) ||
                        r.Bin.StartsWith(search, StringComparison.OrdinalIgnoreCase) ||
                        r.Ticket.StartsWith(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.GameId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.BundleId, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private List<ClosingHistoryRow> FilteredClosingHistoryRows()
    {
        var search = ClosingHistorySearchBox.Text.Trim();
        return _closingHistoryRows
            .Where(r => string.IsNullOrWhiteSpace(search) ||
                        r.MatchesSearch(search) ||
                        r.ClosedText.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        r.ShiftText.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        r.ReportFolder.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        r.SalesText.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        r.ExpectedCashText.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        r.ManualTotalsText.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        r.TicketText.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        r.BinText.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        r.ReconciliationText.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.ClosedAt)
            .ToList();
    }

    private List<AuditLogRow> FilteredAuditLogRows()
    {
        var search = AuditLogSearchBox.Text.Trim();
        return _auditLogRows
            .Where(r => string.IsNullOrWhiteSpace(search) ||
                        r.TimeText.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        r.Category.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        r.Action.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        r.Actor.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        r.Detail.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.OccurredAt)
            .ToList();
    }

    private static int TotalPages(int count, int pageSize) =>
        Math.Max(1, (int)Math.Ceiling(count / (double)Math.Max(1, pageSize)));

    private void RecalculateInventoryPageSizes()
    {
        if (ReceivingListView is null ||
            InventoryListView is null ||
            GameCatalogListView is null ||
            ClosingHistoryListView is null ||
            AuditLogListView is null)
        {
            return;
        }

        var nextReceiving = PageSizeForList(ReceivingListView, 44, _receivingPageSize);
        var nextInventory = PageSizeForList(InventoryListView, 44, _inventoryPageSize);
        var nextGames = PageSizeForList(GameCatalogListView, 58, _gameCatalogPageSize);
        var nextClosings = PageSizeForList(ClosingHistoryListView, 44, _closingHistoryPageSize);
        var nextAudit = PageSizeForList(AuditLogListView, 44, _auditLogPageSize);
        if (nextReceiving == _receivingPageSize &&
            nextInventory == _inventoryPageSize &&
            nextGames == _gameCatalogPageSize &&
            nextClosings == _closingHistoryPageSize &&
            nextAudit == _auditLogPageSize)
        {
            return;
        }

        _receivingPageSize = nextReceiving;
        _inventoryPageSize = nextInventory;
        _gameCatalogPageSize = nextGames;
        _closingHistoryPageSize = nextClosings;
        _auditLogPageSize = nextAudit;
        ApplyReceivingPage();
        ApplyInventoryPage();
        ApplyGameCatalogPage();
        ApplyClosingHistoryPage();
        ApplyAuditLogPage();
    }

    private static int PageSizeForList(ListView listView, double rowHeight, int fallback)
    {
        if (listView.ActualHeight <= rowHeight)
            return Math.Max(1, fallback);

        return Math.Max(1, (int)Math.Floor((listView.ActualHeight - 4) / rowHeight));
    }

    private void GameSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyGameCatalogPage(resetPage: true);
    }

    private void ReceivingSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyReceivingPage(resetPage: true);
    }

    private void OpenBundleSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyInventoryPage(resetPage: true);
    }

    private void ReceivingListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RemoveReceivedBundleButton.IsEnabled =
            ReceivingListView.SelectedItem is InventoryRecord && IsLicenseAvailableForOperation();
    }

    private void InventoryListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RemoveActiveBundleButton.IsEnabled =
            InventoryListView.SelectedItem is InventoryRecord && IsLicenseAvailableForOperation();
    }

    private async void RemoveReceivedBundleButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLicenseAllowsOperation("removing received inventory") ||
            ReceivingListView.SelectedItem is not InventoryRecord selected)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Remove received bundle?",
            Content = $"Remove game {selected.GameId}, bundle {selected.BundleId} from unopened receiving inventory? This does not delete sales or activation history.",
            PrimaryButtonText = "Remove Bundle",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        _isWorkflowDialogOpen = true;
        ContentDialogResult result;
        try
        {
            result = await dialog.ShowAsync();
        }
        finally
        {
            _isWorkflowDialogOpen = false;
        }

        if (result != ContentDialogResult.Primary)
            return;

        try
        {
            _store.DeleteReceivedBundle(selected.GameId, selected.BundleId);
            var bundle = _receivedBundles.FirstOrDefault(item =>
                string.Equals(item.GameId, selected.GameId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.BundleId, selected.BundleId, StringComparison.OrdinalIgnoreCase));
            if (bundle is not null)
                _receivedBundles.Remove(bundle);
            TryRecordAudit(
                "inventory",
                "Received bundle removed",
                $"Game {selected.GameId}, bundle {selected.BundleId}; sales and activation history unchanged");
            StatusText.Text = $"Removed received bundle {selected.BundleId}.";
            RefreshOperationalPages();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Unable to remove received bundle: {ex.Message}";
        }
    }

    private async void RemoveActiveBundleButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLicenseAllowsOperation("removing active inventory") ||
            InventoryListView.SelectedItem is not InventoryRecord selected)
        {
            return;
        }

        var activeBundle = _imports.FirstOrDefault(item =>
            string.Equals(item.GameId, selected.GameId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.BundleId, selected.BundleId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Bin, selected.Bin, StringComparison.OrdinalIgnoreCase));
        if (activeBundle is null)
        {
            StatusText.Text = "The selected active bundle no longer exists.";
            RefreshOperationalPages();
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Remove active bundle?",
            Content = $"Remove game {activeBundle.GameId}, bundle {activeBundle.BundleId} from bin {activeBundle.Bin}? Recorded sales and activation history will remain unchanged.",
            PrimaryButtonText = "Remove Bundle",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        _isWorkflowDialogOpen = true;
        ContentDialogResult result;
        try
        {
            result = await dialog.ShowAsync();
        }
        finally
        {
            _isWorkflowDialogOpen = false;
        }

        if (result != ContentDialogResult.Primary)
            return;

        try
        {
            _store.DeleteImport(activeBundle.GameId, activeBundle.BundleId, activeBundle.Bin);
            _imports.Remove(activeBundle);
            TryRecordAudit(
                "inventory",
                "Active bundle removed",
                $"Game {activeBundle.GameId}, bundle {activeBundle.BundleId}, bin {activeBundle.Bin}; sales and activation history unchanged");
            StatusText.Text = $"Removed bundle {activeBundle.BundleId} from bin {activeBundle.Bin}.";
            RefreshOperationalPages();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Unable to remove active bundle: {ex.Message}";
        }
    }

    private void ClosingHistorySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyClosingHistoryPage(resetPage: true);
    }

    private void AuditLogSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyAuditLogPage(resetPage: true);
    }

    private void SettingsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(SettingsTabs.SelectedItem, AuditSettingsTab))
            return;

        ApplyAuditLogPage(resetPage: _auditLogPageDirty);
        _auditLogPageDirty = false;
    }

    private void ClosingManualTotalBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_isWindowInitialized)
            return;

        RefreshTotals();
    }

    private void ReceivingPreviousPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_receivingPage <= 1)
            return;

        _receivingPage--;
        ApplyReceivingPage();
    }

    private void ReceivingNextPageButton_Click(object sender, RoutedEventArgs e)
    {
        _receivingPage++;
        ApplyReceivingPage();
    }

    private void OpenBundlePreviousPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_inventoryPage <= 1)
            return;

        _inventoryPage--;
        ApplyInventoryPage();
    }

    private void OpenBundleNextPageButton_Click(object sender, RoutedEventArgs e)
    {
        _inventoryPage++;
        ApplyInventoryPage();
    }

    private void GamePreviousPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_gameCatalogPage <= 1)
            return;

        _gameCatalogPage--;
        ApplyGameCatalogPage();
    }

    private void GameNextPageButton_Click(object sender, RoutedEventArgs e)
    {
        _gameCatalogPage++;
        ApplyGameCatalogPage();
    }

    private void ClosingHistoryPreviousPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_closingHistoryPage <= 1)
            return;

        _closingHistoryPage--;
        ApplyClosingHistoryPage();
    }

    private void ClosingHistoryNextPageButton_Click(object sender, RoutedEventArgs e)
    {
        _closingHistoryPage++;
        ApplyClosingHistoryPage();
    }

    private void ClosingHistoryListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ClosingHistoryListView.SelectedItem is ClosingHistoryRow row)
        {
            ShowClosingReport(row);
            return;
        }

        ClearClosingReport();
    }

    private void ClosingTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isWindowInitialized)
            return;

        if (ClosingTabs.SelectedItem is TabViewItem selectedClosingTab &&
            selectedClosingTab == ClosingScanEvidenceTab)
            ExitClosingReportContext();
    }

    private void ExitClosingReportContext()
    {
        if (ClosingHistoryListView.SelectedItem is not null)
        {
            ClosingHistoryListView.SelectedItem = null;
            return;
        }

        ClearClosingReport();
    }

    private async void OpenClosingReportButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = sender is Button { Tag: string taggedFolder } && !string.IsNullOrWhiteSpace(taggedFolder)
            ? taggedFolder
            : (ClosingHistoryListView.SelectedItem as ClosingHistoryRow)?.ReportFolder;

        if (string.IsNullOrWhiteSpace(folder))
        {
            StatusText.Text = "No report folder is available for this closing.";
            return;
        }

        if (!Directory.Exists(folder))
        {
            StatusText.Text = $"Report folder was not found: {folder}";
            return;
        }

        try
        {
            var storageFolder = await StorageFolder.GetFolderFromPathAsync(folder);
            var launched = await Launcher.LaunchFolderAsync(storageFolder);
            StatusText.Text = launched
                ? $"Opened report folder: {folder}"
                : $"Unable to open report folder: {folder}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Unable to open report folder: {ex.Message}";
        }
    }

    private void ShowClosingReport(ClosingHistoryRow row)
    {
        _selectedClosingReport = row;
        RefreshClosingMetricCards();
        ClosingReportSummaryText.Text =
            $"Start: {row.IntervalStartText}{Environment.NewLine}" +
            $"End: {row.ClosedText}";
        ClosingReportCashText.Text =
            $"Online sale: {row.OnlineSaleText}{Environment.NewLine}" +
            $"Online cashout: {row.OnlineCashoutText}{Environment.NewLine}" +
            $"Instant cashout: {row.InstantCashoutText}";
        ClosingReportInventoryText.Text =
            $"{row.ReconciliationText}{Environment.NewLine}" +
            $"Scanned bins: {row.ScannedBins.ToString(CultureInfo.CurrentCulture)} of {row.ActiveBins.ToString(CultureInfo.CurrentCulture)} active";
        ClosingReportStatusText.Text = $"Report folder: {row.ReportFolderText}";
        OpenSelectedClosingReportButton.IsEnabled = row.HasReportFolder;
    }

    private void ClearClosingReport()
    {
        _selectedClosingReport = null;
        RefreshClosingMetricCards();
        ClosingReportSummaryText.Text = "Select a closing to view report details.";
        ClosingReportCashText.Text = "Online sale: $0.00";
        ClosingReportInventoryText.Text = "0 closed, 0 updated, 0 resolved";
        ClosingReportStatusText.Text = "Select a closing to see its report folder.";
        OpenSelectedClosingReportButton.IsEnabled = false;
    }

    private void AuditLogPreviousPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_auditLogPage <= 1)
            return;

        _auditLogPage--;
        ApplyAuditLogPage();
    }

    private void AuditLogNextPageButton_Click(object sender, RoutedEventArgs e)
    {
        _auditLogPage++;
        ApplyAuditLogPage();
    }

    private void SyncRdisplayTiles()
    {
        var gamesById = _gameCatalog.ToDictionary(
            g => g.GameId,
            g => g,
            StringComparer.OrdinalIgnoreCase);

        var tiles = _imports
            .GroupBy(i => i.Bin, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Bin = int.TryParse(g.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0,
                Current = g.First()
            })
            .Where(x => x.Bin > 0)
            .Select(x => new
            {
                x.Bin,
                x.Current,
                Game = gamesById.TryGetValue(x.Current.GameId, out var game) ? game : null
            })
            .Select(x =>
            {
                var remaining = TicketsRemainingForDisplay(x.Current, x.Game);
                return new RdisplayTileState(
                    x.Bin,
                    x.Current.GameId,
                    x.Game?.Name ?? $"Game {x.Current.GameId}",
                    x.Current.Ticket,
                    PriceCentsForDisplay(x.Game?.PriceCents ?? 0),
                    remaining,
                    x.Current.IsSoldOut);
            });

        _rdisplay.UpdateTiles(tiles);
    }

    private void RefreshClosingBins()
    {
        _closingBinCards.Clear();
        var grouped = _imports
            .GroupBy(i => i.Bin)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var activeBinCount = 0;

        for (var i = 1; i <= _configuredBinCount; i++)
        {
            var bin = i.ToString(CultureInfo.InvariantCulture);
            grouped.TryGetValue(bin, out var bundles);
            var current = bundles?.FirstOrDefault();
            if (current is not null && !current.IsSoldOut)
                activeBinCount++;

            _closingBinCards.Add(ClosingBinCard.From(
                i,
                current,
                scanned: _closingScannedBins.Contains(i)));
        }

        if (_selectedClosingReport is null)
            ClosingEvidenceText.Text = $"{_closingScannedBins.Count.ToString(CultureInfo.CurrentCulture)} / {activeBinCount.ToString(CultureInfo.CurrentCulture)}";
        ClosingBinDetailText.Text = "Select a bin to view expected game, bundle, and ticket.";
    }

    private void ClosingBinsGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ClosingBinCard bin)
            return;

        ClosingBinDetailText.Text = bin.Detail;
    }

    private void RefreshSettingsSummary()
    {
        ApplyRoleAccess();
        SettingsStoreText.Text = string.IsNullOrWhiteSpace(_storeName)
            ? "Store setup not completed."
            : $"{_storeName}{Environment.NewLine}{_storeStreet}, {_storeCity}";
        SettingsStateText.Text = string.IsNullOrWhiteSpace(_storeState)
            ? "State: not selected"
            : $"State: {_storeState}";
        SettingsBarcodeText.Text = string.IsNullOrWhiteSpace(_storeBarcodeLayout)
            ? "Barcode format: not selected"
            : $"Barcode format: {_storeBarcodeLayout}";
        RefreshPinSettings();
        RefreshLicenseRegistrationStatus();
        RefreshVersionInformation();
        SettingsScannerText.Text = $"Scanner: WindowsPOS HID pairing model; activation scan timeout {_scanPairTimeoutSeconds.ToString(CultureInfo.CurrentCulture)} seconds";
        ScanPairTimeoutBox.Value = _scanPairTimeoutSeconds;
        DisplayBurnInCheckBox.IsChecked = _displayBurnInEnabled;
        DisplayBurnInIntervalBox.Value = _displayBurnInIntervalMinutes;
        RefreshScannerPairingStatus();
        RefreshRegisteredDisplayCards();
        var registered = _registeredDisplayCards.Count;
        DisplayStatusText.Text = registered == 0
            ? $"Rdisplay API listening on port {RdisplayService.ApiPort}. No display registered."
            : $"Rdisplay API listening on port {RdisplayService.ApiPort}. {registered} display{(registered == 1 ? string.Empty : "s")} registered.";
    }

    private void RefreshPinSettings()
    {
        UserSettingsTab.Header = IsManager ? "Users" : "PIN";
        PinSettingsTitleText.Text = IsManager ? "Users and PINs" : "My PIN";
        PinSettingsDescriptionText.Text = IsManager
            ? "Change your Manager PIN or reset the optional Clerk login. Every PIN contains exactly four digits."
            : "Change your Clerk PIN. Every PIN contains exactly four digits.";
        OwnPinTitleText.Text = IsManager ? "Manager PIN" : $"{_activeUserName} PIN";
        OwnPinInstructionText.Text = "Confirm your current PIN before replacing it.";
        CurrentUserPinBox.Password = string.Empty;
        NewUserPinBox.Password = string.Empty;
        ConfirmUserPinBox.Password = string.Empty;
        OwnPinStatusText.Text = "Enter your current and new PINs.";

        ClerkResetPanel.Visibility = IsManager ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(OwnPinPanel, IsManager ? 1 : 2);

        ClerkNameSettingsBox.Text = _clerkName;
        NewClerkPinSettingsBox.Password = string.Empty;
        ConfirmClerkPinSettingsBox.Password = string.Empty;
        ClerkAccountStatusText.Text = string.IsNullOrWhiteSpace(_clerkName) ||
            string.IsNullOrWhiteSpace(_clerkPasswordHash)
                ? "No Clerk login is configured."
                : $"Clerk login is configured for {_clerkName}.";
        ClerkResetStatusText.Text = "Enter a Clerk name and matching four-digit PIN.";
    }

    private async void ChangeOwnPinButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeUserRole == UserRole.None)
            return;

        var changingRole = _activeUserRole;
        var changingUser = _activeUserName;
        var currentPin = CurrentUserPinBox.Password;
        var newPin = NewUserPinBox.Password;
        var confirmation = ConfirmUserPinBox.Password;
        if (!PinHashService.IsValidPin(currentPin))
        {
            OwnPinStatusText.Text = "Enter your current four-digit PIN.";
            return;
        }

        if (!PinHashService.IsValidPin(newPin))
        {
            OwnPinStatusText.Text = "Your new PIN must contain exactly four digits.";
            return;
        }

        if (!string.Equals(newPin, confirmation, StringComparison.Ordinal))
        {
            OwnPinStatusText.Text = "Your new PIN and confirmation do not match.";
            return;
        }

        if (string.Equals(currentPin, newPin, StringComparison.Ordinal))
        {
            OwnPinStatusText.Text = "Choose a new PIN that differs from your current PIN.";
            return;
        }

        var expectedHash = changingRole == UserRole.Manager ? _managerPasswordHash : _clerkPasswordHash;
        ChangeOwnPinButton.IsEnabled = false;
        OwnPinStatusText.Text = "Verifying your current PIN...";
        try
        {
            var verification = await Task.Run(() => PinHashService.Verify(currentPin, expectedHash));
            if (!verification.IsValid)
            {
                OwnPinStatusText.Text = "Your current PIN is incorrect.";
                return;
            }

            var newHash = await Task.Run(() => PinHashService.CreateHash(newPin));
            if (_activeUserRole != changingRole ||
                !string.Equals(_activeUserName, changingUser, StringComparison.Ordinal))
            {
                OwnPinStatusText.Text = "The active user changed. Sign in again before changing this PIN.";
                return;
            }

            if (!TrySaveOwnPinHash(changingRole, newHash))
            {
                OwnPinStatusText.Text = "Your new PIN could not be saved. Try again.";
                return;
            }

            CurrentUserPinBox.Password = string.Empty;
            NewUserPinBox.Password = string.Empty;
            ConfirmUserPinBox.Password = string.Empty;
            OwnPinStatusText.Text = "Your PIN changed successfully.";
            TryRecordAudit("auth", "User PIN changed", $"{changingUser} changed their own {changingRole} PIN");
        }
        finally
        {
            ChangeOwnPinButton.IsEnabled = true;
        }
    }

    private async void ResetClerkPinButton_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireManagerAccess("Clerk PIN settings"))
            return;

        var clerkName = ClerkNameSettingsBox.Text.Trim();
        var newPin = NewClerkPinSettingsBox.Password;
        var confirmation = ConfirmClerkPinSettingsBox.Password;
        if (string.IsNullOrWhiteSpace(clerkName))
        {
            ClerkResetStatusText.Text = "Enter a Clerk name.";
            return;
        }

        if (!PinHashService.IsValidPin(newPin))
        {
            ClerkResetStatusText.Text = "The Clerk PIN must contain exactly four digits.";
            return;
        }

        if (!string.Equals(newPin, confirmation, StringComparison.Ordinal))
        {
            ClerkResetStatusText.Text = "The Clerk PIN and confirmation do not match.";
            return;
        }

        ResetClerkPinButton.IsEnabled = false;
        ClerkResetStatusText.Text = "Saving the Clerk login...";
        try
        {
            var newHash = await Task.Run(() => PinHashService.CreateHash(newPin));
            if (!IsManager)
            {
                ClerkResetStatusText.Text = "Manager login changed. Sign in again before resetting the Clerk PIN.";
                return;
            }

            if (!TrySaveClerkCredentials(clerkName, newHash))
            {
                ClerkResetStatusText.Text = "The Clerk login could not be saved. Try again.";
                return;
            }

            NewClerkPinSettingsBox.Password = string.Empty;
            ConfirmClerkPinSettingsBox.Password = string.Empty;
            ClerkAccountStatusText.Text = $"Clerk login is configured for {_clerkName}.";
            ClerkResetStatusText.Text = "Clerk login and PIN reset successfully.";
            TryRecordAudit("auth", "Clerk PIN reset", $"Manager reset the Clerk login PIN for {_clerkName}");
        }
        finally
        {
            ResetClerkPinButton.IsEnabled = true;
        }
    }

    private bool TrySaveOwnPinHash(UserRole role, string pinHash)
    {
        var settingKey = role == UserRole.Manager ? "manager_password_hash" : "clerk_password_hash";
        try
        {
            _store.SaveSetting(settingKey, pinHash);
            if (role == UserRole.Manager)
                _managerPasswordHash = pinHash;
            else
                _clerkPasswordHash = pinHash;

            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"{role} PIN could not be saved.", ex);
            return false;
        }
    }

    private bool TrySaveClerkCredentials(string clerkName, string clerkPinHash)
    {
        try
        {
            _store.SaveClerkCredentials(clerkName, clerkPinHash);
            _clerkName = clerkName;
            _clerkPasswordHash = clerkPinHash;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Clerk login could not be saved.", ex);
            return false;
        }
    }

    private void RefreshLicenseRegistrationStatus()
    {
        ApplyLicenseStatus(_license.CheckLicense());
    }

    private void ApplyLicenseStatus(LicenseStatus status)
    {
        LicenseRegistrationText.Text = string.IsNullOrWhiteSpace(status.RegistrationId)
            ? "Device registration ID unavailable."
            : $"Registration ID: {status.RegistrationId}";
        LicenseLastCheckedText.Text = status.LastCheck is null
            ? "License last checked: never"
            : $"License last checked: {status.LastCheck.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}";
        LicenseStatusText.Text = BuildLicenseStatusText(status);
        RefreshLicenseBanner(status);

        var available = !IsLicenseLocked(status);
        CloseShiftButton.IsEnabled = available;
        StartClosingScanButton.IsEnabled = available;
        AddBundleToBinButton.IsEnabled = available && _selectedBinNumber is not null;
        MoveSelectedBundleButton.IsEnabled = available && BinBundlesListView.SelectedItem is BundleDetailLine;
        _rdisplay.UpdateLicenseStatus(available ? status.Status : "expired");
    }

    private static string BuildLicenseStatusText(LicenseStatus status)
    {
        var builder = new StringBuilder()
            .Append("Status: ")
            .Append(status.Status)
            .Append(". ")
            .Append(status.Message);

        if (!string.IsNullOrWhiteSpace(status.SubscriptionExpiresAt))
        {
            builder
                .Append(" Subscription expires ")
                .Append(status.SubscriptionExpiresAt);
            if (status.SubscriptionDaysRemaining is { } days)
                builder.Append(" (").Append(days.ToString(CultureInfo.CurrentCulture)).Append(" days).");
            else
                builder.Append('.');
        }

        if (status.LastAuthorized is not null)
        {
            builder
                .Append(" Last authorized ")
                .Append(status.LastAuthorized.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))
                .Append('.');
        }

        return builder.ToString();
    }

    private bool EnsureLicenseAllowsOperation(string operation)
    {
        var status = _license.CheckLicense();
        ApplyLicenseStatus(status);
        if (!IsLicenseLocked(status))
            return true;

        var message = $"License expired. SimpleLotto is locked. Open Settings > Store and check license before {operation}.";
        StatusText.Text = message;
        DashboardScannerStatusText.Text = message;
        ClosingStatusText.Text = message;
        return false;
    }

    private bool IsLicenseAvailableForOperation() =>
        !IsLicenseLocked(_license.CheckLicense());

    private static bool IsLicenseLocked(LicenseStatus status) =>
        string.Equals(status.Status, "expired", StringComparison.OrdinalIgnoreCase) ||
        status.SubscriptionDaysRemaining < 0;

    private void RefreshLicenseBanner(LicenseStatus status)
    {
        var message = BuildLicenseBannerMessage(status);
        if (string.IsNullOrWhiteSpace(message))
        {
            LicenseBanner.Visibility = Visibility.Collapsed;
            return;
        }

        var locked = IsLicenseLocked(status);
        LicenseBanner.Visibility = Visibility.Visible;
        LicenseBannerText.Text = message;
        LicenseBanner.Background = new SolidColorBrush(locked
            ? Color.FromArgb(0xFF, 0xFD, 0xE7, 0xE9)
            : Color.FromArgb(0xFF, 0xFF, 0xF4, 0xCE));
        LicenseBanner.BorderBrush = new SolidColorBrush(locked
            ? Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C)
            : Color.FromArgb(0xFF, 0xE0, 0xA1, 0x00));
        var foreground = new SolidColorBrush(locked
            ? Color.FromArgb(0xFF, 0x5C, 0x1B, 0x14)
            : Color.FromArgb(0xFF, 0x3B, 0x2F, 0x00));
        LicenseBannerText.Foreground = foreground;
        LicenseBannerIcon.Foreground = foreground;
    }

    private static string BuildLicenseBannerMessage(LicenseStatus status)
    {
        if (IsLicenseLocked(status))
            return "License expired. SimpleLotto is locked until registration is renewed.";

        if (string.Equals(status.Status, "grace", StringComparison.OrdinalIgnoreCase))
        {
            var days = status.DaysRemaining.ToString(CultureInfo.CurrentCulture);
            return $"License renewal is in the 7-day grace period. {days} day{(status.DaysRemaining == 1 ? string.Empty : "s")} remaining before lock.";
        }

        if (string.Equals(status.Status, "pending", StringComparison.OrdinalIgnoreCase) &&
            status.LastCheck is not null &&
            status.LastAuthorized is null)
        {
            var days = status.DaysRemaining.ToString(CultureInfo.CurrentCulture);
            return $"License check is pending. The 7-day grace period has started; {days} day{(status.DaysRemaining == 1 ? string.Empty : "s")} remaining before lock.";
        }

        if (status.SubscriptionDaysRemaining is { } subscriptionDays &&
            subscriptionDays >= 0 &&
            subscriptionDays <= LicenseExpiryWarningDays)
        {
            return subscriptionDays == 0
                ? "License expires today. Renew registration to avoid lock."
                : $"License expires in {subscriptionDays.ToString(CultureInfo.CurrentCulture)} day{(subscriptionDays == 1 ? string.Empty : "s")}. Renew registration to avoid lock.";
        }

        return string.Empty;
    }

    private LicenseStoreInfo CurrentLicenseStoreInfo() =>
        new(
            _storeName,
            _storeStreet,
            _storeCity,
            _storeState,
            string.Empty);

    private async void CheckUpgradeButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpgradeButton.IsEnabled = false;
        SettingsUpgradeStatusText.Text = "Checking for upgrade...";
        try
        {
            var state = await _updates.CheckAndDownloadAsync();
            RefreshUpgradeStatus(state);
        }
        finally
        {
            CheckUpgradeButton.IsEnabled = true;
        }
    }

    private void QueueScheduledUpgradeCheck()
    {
        if (_automaticUpgradeCheckRunning || !_setupComplete)
            return;

        var today = DateTime.Today;
        if (!IsScheduledAutomaticUpgradeCheckDate(today))
            return;

        var todayKey = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (string.Equals(_lastAutomaticUpgradeCheckDate, todayKey, StringComparison.Ordinal))
            return;

        _automaticUpgradeCheckRunning = true;
        _ = Task.Run(async () => await RunScheduledUpgradeCheckAsync(todayKey));
    }

    private async Task RunScheduledUpgradeCheckAsync(string todayKey)
    {
        try
        {
            AppLog.Info($"Automatic upgrade check started for scheduled date {todayKey}.");
            var state = await _updates.CheckAndDownloadAsync();
            _lastAutomaticUpgradeCheckDate = todayKey;
            SaveAutomaticUpgradeCheckDate(todayKey);
            DispatcherQueue.TryEnqueue(() => RefreshUpgradeStatus(state));
            AppLog.Info($"Automatic upgrade check finished with status {state.Status}.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Automatic upgrade check failed.", ex);
        }
        finally
        {
            _automaticUpgradeCheckRunning = false;
        }
    }

    private void SaveAutomaticUpgradeCheckDate(string value)
    {
        try
        {
            _store.SaveSetting(AutomaticUpgradeLastCheckDateSettingKey, value);
        }
        catch (Exception ex)
        {
            AppLog.Error("Unable to save automatic upgrade check date.", ex);
        }
    }

    private static bool IsScheduledAutomaticUpgradeCheckDate(DateTime date)
    {
        var weekOfMonth = ((date.Day - 1) / 7) + 1;
        return weekOfMonth switch
        {
            1 => date.DayOfWeek == DayOfWeek.Monday,
            2 => date.DayOfWeek == DayOfWeek.Tuesday,
            3 => date.DayOfWeek == DayOfWeek.Wednesday,
            4 => date.DayOfWeek == DayOfWeek.Thursday,
            _ => false
        };
    }

    private void ApplyUpgradeButton_Click(object sender, RoutedEventArgs e)
    {
        var result = _updates.LaunchDownloadedInstaller();
        SettingsUpgradeStatusText.Text = result.IsSuccess
            ? "Upgrade installer launched. The installer will close SimpleLotto when it applies the upgrade."
            : $"Upgrade could not be launched: {result.ErrorMessage}";
    }

    private async void TriggerRdisplayUpgradeButton_Click(object sender, RoutedEventArgs e)
    {
        TriggerRdisplayUpgradeButton.IsEnabled = false;
        SettingsRdisplayUpgradeStatusText.Text = "Requesting Rdisplay upgrades...";
        try
        {
            var result = await _rdisplay.TriggerUpgradeForAllRegisteredAsync();
            SettingsRdisplayUpgradeStatusText.Text =
                $"Rdisplay upgrade requested for {result.Requested.ToString(CultureInfo.CurrentCulture)} of {result.RegisteredDisplays.ToString(CultureInfo.CurrentCulture)} registered display{(result.RegisteredDisplays == 1 ? string.Empty : "s")}. Failed: {result.Failed.ToString(CultureInfo.CurrentCulture)}.";
        }
        finally
        {
            TriggerRdisplayUpgradeButton.IsEnabled = true;
        }
    }

    private void RefreshVersionInformation()
    {
        var appAssembly = typeof(App).Assembly;
        var windowsAppSdkAssembly = typeof(Application).Assembly;

        SettingsAppVersionText.Text = GetInformationalVersion(appAssembly);
        SettingsBuildVersionText.Text = appAssembly.GetName().Version?.ToString() ?? "Unavailable";
        SettingsSchemaVersionText.Text = string.IsNullOrWhiteSpace(_databaseSchemaVersion)
            ? "Not initialized"
            : _databaseSchemaVersion;
        SettingsWindowsAppSdkVersionText.Text = windowsAppSdkAssembly.GetName().Version?.ToString() ?? "Unavailable";
        SettingsDatabasePathText.Text = _store.DatabasePath;
        SettingsLogPathText.Text = AppLog.LogDirectory;
        RefreshUpgradeStatus(_updates.Current);
    }

    private void RefreshUpgradeStatus(AppUpdateState state)
    {
        SettingsUpgradeStatusText.Text = state.Error is { Length: > 0 }
            ? $"{state.Message} {state.Error}"
            : state.Package is null
                ? state.Message
                : $"{state.Message} Latest: {state.Package.Version}. Installed: {state.InstalledVersion}.";
        ApplyUpgradeButton.IsEnabled = state.Status == AppUpdateStatus.Downloaded &&
            !string.IsNullOrWhiteSpace(state.InstallerPath);
    }

    private static string GetInformationalVersion(Assembly assembly)
    {
        var version = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return string.IsNullOrWhiteSpace(version)
            ? assembly.GetName().Version?.ToString() ?? "Unavailable"
            : version;
    }

    private void RefreshRegisteredDisplayCards()
    {
        _registeredDisplayCards.Clear();
        foreach (var display in _rdisplay.Displays
                     .Where(d => d.IsRegistered)
                     .OrderBy(d => d.ScreenOrder)
                     .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            _registeredDisplayCards.Add(RegisteredDisplayCard.From(display));
        }

        RegisteredDisplaysEmptyText.Visibility = _registeredDisplayCards.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateRegisteredDisplayActionState();
    }

    private void RegisteredDisplaysListView_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateRegisteredDisplayActionState();

    private void UpdateRegisteredDisplayActionState()
    {
        var hasSelected = RegisteredDisplaysListView.SelectedItem is RegisteredDisplayCard;
        RefreshSelectedDisplayButton.IsEnabled = hasSelected;
        DeregisterSelectedDisplayButton.IsEnabled = hasSelected;
    }

    private void RefreshScannerPairingStatus()
    {
        var paired = !string.IsNullOrWhiteSpace(_scannerVid) && !string.IsNullOrWhiteSpace(_scannerPid);
        SetScannerPaired(paired);
        if (!paired)
        {
            ScannerPairingStatusText.Text = "No scanner paired";
            ScannerPairingDetailText.Text = "Scans only register while the app has focus.";
            UnpairScannerButton.IsEnabled = false;
            return;
        }

        ScannerPairingStatusText.Text = _scannerInput.IsActivelyCapturing
            ? "Scanner paired and listening"
            : "Scanner paired, but not detected";
        ScannerPairingDetailText.Text = string.IsNullOrWhiteSpace(_scannerSerial)
            ? $"VID {_scannerVid} / PID {_scannerPid} / no serial"
            : $"VID {_scannerVid} / PID {_scannerPid} / SN {_scannerSerial}";
        if (!_scannerInput.IsActivelyCapturing)
            ScannerPairingDetailText.Text += " / reconnect the scanner or pair it again";
        UnpairScannerButton.IsEnabled = true;
    }

    private bool IsManager => _activeUserRole == UserRole.Manager;

    private void OrderInventoryTabs()
    {
        InventoryTabs.TabItems.Remove(BundleRecordsTab);
        InventoryTabs.TabItems.Add(BundleRecordsTab);
        InventoryTabs.SelectedItem = ReceivingTab;
    }

    private void ApplyRoleAccess()
    {
        var managerVisibility = IsManager ? Visibility.Visible : Visibility.Collapsed;
        StoreSettingsTab.Visibility = managerVisibility;
        UserSettingsTab.Visibility = _activeUserRole == UserRole.None
            ? Visibility.Collapsed
            : Visibility.Visible;
        BackupSettingsTab.Visibility = managerVisibility;
        EmailSettingsTab.Visibility = managerVisibility;
        AuditSettingsTab.Visibility = managerVisibility;
        GameSettingsTab.Visibility = managerVisibility;

        SettingsSubtitleText.Text = IsManager
            ? "Store, users, scanner, display, and game setup controls for the current installation."
            : "PIN, scanner, and display settings available for Clerk access.";

        if (!IsManager)
        {
            if (SettingsTabs.SelectedItem is not TabViewItem selectedSettingsTab ||
                selectedSettingsTab != ScannerDisplaySettingsTab &&
                selectedSettingsTab != UserSettingsTab)
            {
                SettingsTabs.SelectedItem = ScannerDisplaySettingsTab;
            }

            if (InventoryTabs.SelectedItem is TabViewItem selectedInventoryTab &&
                selectedInventoryTab == GameSettingsTab)
            {
                InventoryTabs.SelectedItem = ReceivingTab;
            }
        }
    }

    private bool RequireManagerAccess(string area)
    {
        if (IsManager)
            return true;

        var userText = string.IsNullOrWhiteSpace(_activeUserName)
            ? "current user"
            : _activeUserName;
        var message = $"Manager access is required for {area}. {userText} is logged in as {_activeUserRole}.";
        StatusText.Text = message;
        GameCatalogStatusText.Text = message;
        BackupStatusText.Text = message;
        EmailSettingsStatusText.Text = message;
        return false;
    }

    private void BinsGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not BinCard card)
            return;

        ShowBinDetail(card);
    }

    private void BinTile_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BinCard card)
            return;

        e.Handled = true;
        BinsGridView.SelectedItem = null;
        ShowBinDetail(card);
    }

    private void BinBundlesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var hasSelection = BinBundlesListView.SelectedItem is BundleDetailLine;
        MoveSelectedBundleButton.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        MoveSelectedBundleButton.IsEnabled = hasSelection && IsLicenseAvailableForOperation();
    }

    private async void MoveSelectedBundleButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLicenseAllowsOperation("moving bundles"))
            return;

        if (BinBundlesListView.SelectedItem is not BundleDetailLine selected)
        {
            StatusText.Text = "Select a bundle in Bin Details before moving it.";
            return;
        }

        var bundleIndex = -1;
        for (var index = 0; index < _imports.Count; index++)
        {
            var candidate = _imports[index];
            if (string.Equals(candidate.GameId, selected.GameId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.BundleId, selected.BundleId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Bin, selected.Bin, StringComparison.OrdinalIgnoreCase))
            {
                bundleIndex = index;
                break;
            }
        }

        if (bundleIndex < 0)
        {
            StatusText.Text = "The selected bundle is no longer assigned to this bin. Refresh and try again.";
            return;
        }

        var newBinBox = new NumberBox
        {
            Header = "New bin number",
            Minimum = 1,
            Maximum = _configuredBinCount,
            SmallChange = 1,
            LargeChange = 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        var validationText = new TextBlock
        {
            Text = $"Move game {selected.GameId}, bundle {selected.BundleId}, from bin {selected.Bin}.",
            TextWrapping = TextWrapping.Wrap
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Move Bundle",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    validationText,
                    newBinBox
                }
            },
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        var selectedNewBin = 0;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var value = newBinBox.Value;
            if (double.IsNaN(value) ||
                value < 1 ||
                value > _configuredBinCount ||
                value != Math.Truncate(value))
            {
                args.Cancel = true;
                validationText.Text = $"Enter a whole bin number from 1 to {_configuredBinCount.ToString(CultureInfo.CurrentCulture)}.";
                return;
            }

            selectedNewBin = (int)value;
            if (string.Equals(
                    selectedNewBin.ToString(CultureInfo.InvariantCulture),
                    selected.Bin,
                    StringComparison.OrdinalIgnoreCase))
            {
                args.Cancel = true;
                validationText.Text = $"Bundle {selected.BundleId} is already in bin {selected.Bin}. Enter a different bin.";
            }
        };

        _isWorkflowDialogOpen = true;
        try
        {
            _ = newBinBox.Focus(FocusState.Programmatic);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;
        }
        finally
        {
            _isWorkflowDialogOpen = false;
        }

        var oldBin = selected.Bin;
        var newBin = selectedNewBin.ToString(CultureInfo.InvariantCulture);
        try
        {
            _store.MoveImportBundle(selected.GameId, selected.BundleId, oldBin, newBin);
            _imports[bundleIndex] = _imports[bundleIndex] with { Bin = newBin };
            TryRecordAudit(
                "inventory",
                "Bundle moved",
                $"Game {selected.GameId}, bundle {selected.BundleId}, bin {oldBin} to bin {newBin}, ticket {selected.Ticket}, sold out {selected.IsSoldOut}");
            RefreshOperationalPages();
            ShowBinDetail(selectedNewBin);
            StatusText.Text = $"Moved bundle {selected.BundleId} from bin {oldBin} to bin {newBin}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Unable to move bundle: {ex.Message}";
        }
    }

    private async void AddBundleToBinButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLicenseAllowsOperation("adding bundles"))
            return;

        if (_selectedBinNumber is not { } binNumber)
        {
            StatusText.Text = "Select a bin before adding a bundle.";
            return;
        }

        _ = SpeakAsync("Scan bundle.");
        var barcodeBox = new TextBox
        {
            Header = "Bundle or ticket barcode",
            PlaceholderText = "Scan bundle/ticket barcode"
        };
        var statusText = new TextBlock
        {
            Text = $"Scan or enter the bundle/ticket barcode for bin {binNumber.ToString(CultureInfo.CurrentCulture)}.",
            TextWrapping = TextWrapping.Wrap
        };
        ImportTicket? parsedTicket = null;

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"Add Bundle to Bin {binNumber.ToString(CultureInfo.CurrentCulture)}",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    barcodeBox,
                    statusText
                }
            },
            PrimaryButtonText = "Activate Bundle",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            parsedTicket = TryParseImportTicket(barcodeBox.Text.Trim());
            if (parsedTicket is not null)
                return;

            args.Cancel = true;
            statusText.Text = "Scan or enter a valid configured-state ticket barcode before activating.";
        };

        _isWorkflowDialogOpen = true;
        try
        {
            _ = barcodeBox.Focus(FocusState.Programmatic);
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary || parsedTicket is null)
                return;
        }
        finally
        {
            _isWorkflowDialogOpen = false;
        }

        var bin = binNumber.ToString(CultureInfo.InvariantCulture);
        var activated = await ActivateBundleInBinAsync(bin, parsedTicket, updateDashboardStatus: false);
        if (activated)
            ShowBinDetail(binNumber);
    }

    private void ShowBinDetail(BinCard card)
    {
        ShowBinDetail(card.Number);
    }

    private void ShowBinDetail(int binNumber)
    {
        BinBundlesListView.SelectedItem = null;
        MoveSelectedBundleButton.Visibility = Visibility.Collapsed;
        MoveSelectedBundleButton.IsEnabled = false;
        _selectedBinBundles.Clear();
        var lines = _imports
            .Where(i => string.Equals(i.Bin, binNumber.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase))
            .ToList();

        _selectedBinNumber = binNumber;
        AddBundleToBinButton.IsEnabled = IsLicenseAvailableForOperation();
        var bundleLabel = lines.Count == 1 ? "bundle" : "bundles";
        BinDetailText.Text = $"Bin {binNumber.ToString(CultureInfo.CurrentCulture)} Details ({lines.Count.ToString(CultureInfo.CurrentCulture)} {bundleLabel})";

        for (var i = 0; i < lines.Count; i++)
            _selectedBinBundles.Add(BundleDetailLine.From(lines[i], GameNameForDetail(lines[i].GameId), i == 0));
    }

    private void BinDetailSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        const double minWidth = 240;
        const double maxWidth = 520;
        var currentWidth = BinDetailColumn.ActualWidth > 0
            ? BinDetailColumn.ActualWidth
            : BinDetailColumn.Width.Value;
        var nextWidth = Math.Clamp(currentWidth - e.HorizontalChange, minWidth, maxWidth);
        BinDetailColumn.Width = new GridLength(nextWidth);
    }

    private ImportLine? FindActiveBundle(ImportTicket ticket) =>
        _imports.FirstOrDefault(i =>
            string.Equals(i.GameId, ticket.GameId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(i.BundleId, ticket.BundleId, StringComparison.OrdinalIgnoreCase) &&
            !i.IsSoldOut);

    private ImportLine? FindPlacedBundle(ImportTicket ticket) =>
        _imports.FirstOrDefault(i =>
            string.Equals(i.GameId, ticket.GameId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(i.BundleId, ticket.BundleId, StringComparison.OrdinalIgnoreCase));

    private bool PhysicalBundleExists(string gameId, string bundleId) =>
        _imports.Any(bundle =>
            string.Equals(bundle.GameId, gameId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(bundle.BundleId, bundleId, StringComparison.OrdinalIgnoreCase)) ||
        _receivedBundles.Any(bundle =>
            string.Equals(bundle.GameId, gameId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(bundle.BundleId, bundleId, StringComparison.OrdinalIgnoreCase));

    private async Task StartReceivingScanWorkflowAsync()
    {
        if (_isWorkflowDialogOpen)
            return;

        var stagedRows = new ObservableCollection<ReceivingScanRow>();
        var stagedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dialogScanBuffer = new StringBuilder();
        var finishing = false;
        var statusText = new TextBlock
        {
            Text = "Ready. Scan any ticket barcode from each delivered bundle.",
            Style = (Style)Application.Current.Resources["SlCaptionTextStyle"],
            TextWrapping = TextWrapping.Wrap
        };
        var totalText = new TextBlock
        {
            Text = "0",
            Style = (Style)Application.Current.Resources["SlMetricTextStyle"],
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
        var scanList = new ListView
        {
            ItemsSource = stagedRows,
            DisplayMemberPath = "ScannedText",
            SelectionMode = ListViewSelectionMode.None,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        void AcceptScan(string raw)
        {
            if (finishing)
            {
                statusText.Text = "Scan error: inventory update is in progress.";
                TryRecordAudit("scanner", "Receiving scan rejected", $"Received while finalizing: {raw}");
                _ = SpeakAsync("Scan again.");
                return;
            }

            foreach (var segment in SplitImportScanInput(raw))
            {
                var ticket = TryParseImportTicket(segment);
                if (ticket is null)
                {
                    statusText.Text = "Scan error: barcode was not recognized for the configured state.";
                    TryRecordAudit("scanner", "Receiving scan rejected", $"Unrecognized scan {segment}");
                    _ = SpeakAsync("Scan error.");
                    continue;
                }

                var key = BundleKey(ticket);
                var duplicate = stagedKeys.Contains(key) || PhysicalBundleExists(ticket.GameId, ticket.BundleId);
                if (duplicate)
                {
                    _ = SpeakAsync("Duplicate");
                    continue;
                }

                stagedKeys.Add(key);
                stagedRows.Insert(0, new ReceivingScanRow(ticket.GameId, ticket.BundleId));
                totalText.Text = stagedRows.Count.ToString(CultureInfo.CurrentCulture);
                statusText.Text = $"Game {ticket.GameId}, bundle {ticket.BundleId} added.";
                TryRecordAudit(
                    "scanner",
                    "Receiving scan captured",
                    $"Game {ticket.GameId}, bundle {ticket.BundleId}; staged for receiving");
            }
        }

        var content = new Grid
        {
            RowSpacing = 12,
            ColumnSpacing = 12,
            IsTabStop = true
        };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.AddHandler(
            UIElement.KeyDownEvent,
            new KeyEventHandler((_, args) =>
                CaptureGlobalScanKey(args, dialogScanBuffer, AcceptScan, statusText)),
            handledEventsToo: true);

        var promptText = new TextBlock
        {
            Text = "Receiving captures Game ID + Bundle ID only. Ticket serial is ignored.",
            Style = (Style)Application.Current.Resources["SlSectionTitleTextStyle"],
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumnSpan(promptText, 2);
        content.Children.Add(promptText);

        var listGrid = new Grid { RowSpacing = 8 };
        listGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        listGrid.Children.Add(new TextBlock
        {
            Text = "Bundles scanned",
            Style = (Style)Application.Current.Resources["SlSectionTitleTextStyle"]
        });
        Grid.SetRow(scanList, 1);
        listGrid.Children.Add(scanList);
        var listPanel = new Border
        {
            Style = (Style)Application.Current.Resources["SlPanelBorderStyle"],
            Child = listGrid
        };
        Grid.SetRow(listPanel, 1);
        content.Children.Add(listPanel);

        var totalPanel = new Border
        {
            Style = (Style)Application.Current.Resources["SlPanelBorderStyle"],
            Child = new StackPanel
            {
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    totalText,
                    new TextBlock
                    {
                        Text = "bundles added to inventory",
                        Style = (Style)Application.Current.Resources["SlCaptionTextStyle"],
                        HorizontalAlignment = HorizontalAlignment.Center,
                        TextAlignment = TextAlignment.Center,
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            }
        };
        Grid.SetRow(totalPanel, 1);
        Grid.SetColumn(totalPanel, 1);
        content.Children.Add(totalPanel);

        var cancelButton = new Button
        {
            Content = "Cancel Receiving",
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 128
        };
        var footer = new Grid { ColumnSpacing = 12 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(statusText);
        Grid.SetColumn(cancelButton, 1);
        footer.Children.Add(cancelButton);
        Grid.SetRow(footer, 2);
        Grid.SetColumnSpan(footer, 2);
        content.Children.Add(footer);

        void ApplyResponsiveLayout(double width)
        {
            var stacked = width > 0 && width < 640;
            if (stacked)
            {
                if (content.RowDefinitions.Count == 3)
                    content.RowDefinitions.Insert(2, new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                content.RowDefinitions[1].Height = GridLength.Auto;
                content.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);
                Grid.SetRow(totalPanel, 1);
                Grid.SetColumn(totalPanel, 0);
                Grid.SetColumnSpan(totalPanel, 2);
                Grid.SetRow(listPanel, 2);
                Grid.SetColumnSpan(listPanel, 2);
                Grid.SetRow(footer, 3);
            }
            else
            {
                while (content.RowDefinitions.Count > 3)
                    content.RowDefinitions.RemoveAt(2);
                content.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
                Grid.SetRow(listPanel, 1);
                Grid.SetColumnSpan(listPanel, 1);
                Grid.SetRow(totalPanel, 1);
                Grid.SetColumn(totalPanel, 1);
                Grid.SetColumnSpan(totalPanel, 1);
                Grid.SetRow(footer, 2);
            }
        }

        content.SizeChanged += (_, args) => ApplyResponsiveLayout(args.NewSize.Width);

        void ApplyOverlaySize()
        {
            var width = ClosingScanOverlay.ActualWidth > 0 ? ClosingScanOverlay.ActualWidth : RootGrid.ActualWidth;
            var height = ClosingScanOverlay.ActualHeight > 0 ? ClosingScanOverlay.ActualHeight : RootGrid.ActualHeight;
            ClosingScanOverlayPanel.Width = Math.Max(320, Math.Min(1120, width - 32));
            ClosingScanOverlayPanel.Height = Math.Max(280, Math.Min(760, height - 32));
            ApplyResponsiveLayout(ClosingScanOverlayPanel.Width - 32);
        }

        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler finishHandler = async (_, _) =>
        {
            if (finishing)
                return;

            finishing = true;
            ClosingScanOverlayCloseButton.IsEnabled = false;
            try
            {
                if (stagedRows.Count == 0)
                {
                    TryRecordAudit("inventory", "Receiving scan finalized", "0 bundles received");
                    completed.TrySetResult(false);
                    return;
                }

                foreach (var gameId in stagedRows
                             .Select(row => row.GameId)
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (HasCompleteGameSetup(gameId))
                        continue;

                    var sample = stagedRows.First(row =>
                        string.Equals(row.GameId, gameId, StringComparison.OrdinalIgnoreCase));
                    if (!await ShowReceivingGameSetupDialogAsync(sample))
                    {
                        statusText.Text = $"Ticket price is required for game {gameId}. Receiving remains open.";
                        TryRecordAudit(
                            "inventory",
                            "Receiving finalization paused",
                            $"Game {gameId} still requires ticket price setup; no bundles committed");
                        _ = content.Focus(FocusState.Programmatic);
                        return;
                    }
                }

                var receivedAtUtc = DateTime.UtcNow;
                var stored = stagedRows
                    .Select(row => new StoredReceivedBundle(row.GameId, row.BundleId, receivedAtUtc))
                    .ToList();
                _store.InsertReceivedBundles(stored);
                foreach (var bundle in stored)
                {
                    _receivedBundles.Insert(0, new ReceivedBundleLine(
                        bundle.GameId,
                        bundle.BundleId,
                        bundle.ReceivedAtUtc.ToLocalTime(),
                        bundle.Source));
                    TryRecordAudit(
                        "inventory",
                        "Bundle received",
                        $"Game {bundle.GameId}, bundle {bundle.BundleId}, source receiving");
                }

                TryRecordAudit(
                    "inventory",
                    "Receiving scan finalized",
                    $"{stored.Count.ToString(CultureInfo.InvariantCulture)} bundles committed to unopened inventory");

                completed.TrySetResult(true);
            }
            catch (Exception ex)
            {
                AppLog.Error("Inventory receiving could not be finalized.", ex);
                statusText.Text = $"Unable to save received inventory: {ex.Message}";
                TryRecordAudit("inventory", "Receiving finalization failed", ex.Message);
            }
            finally
            {
                finishing = false;
                ClosingScanOverlayCloseButton.IsEnabled = true;
            }
        };
        RoutedEventHandler cancelHandler = async (_, _) =>
        {
            if (finishing)
                return;

            if (stagedRows.Count == 0)
            {
                TryRecordAudit("inventory", "Receiving scan cancelled", "0 staged bundles");
                completed.TrySetResult(false);
                return;
            }

            var confirm = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Discard receiving scans?",
                Content = $"Discard {stagedRows.Count.ToString(CultureInfo.CurrentCulture)} scanned bundle{(stagedRows.Count == 1 ? string.Empty : "s")}?",
                PrimaryButtonText = "Discard",
                CloseButtonText = "Keep Scanning",
                DefaultButton = ContentDialogButton.Close
            };
            if (await confirm.ShowAsync() == ContentDialogResult.Primary)
            {
                TryRecordAudit(
                    "inventory",
                    "Receiving scan cancelled",
                    $"Discarded {stagedRows.Count.ToString(CultureInfo.InvariantCulture)} staged bundle scans");
                completed.TrySetResult(false);
            }
            else
                _ = content.Focus(FocusState.Programmatic);
        };
        SizeChangedEventHandler overlaySizeChanged = (_, _) => ApplyOverlaySize();

        ClosingScanOverlayCloseButton.Click += finishHandler;
        cancelButton.Click += cancelHandler;
        ClosingScanOverlay.SizeChanged += overlaySizeChanged;
        _isWorkflowDialogOpen = true;
        var previousFocusedScannerCapture = _useFocusedScannerCapture;
        _useFocusedScannerCapture = true;
        try
        {
            TryRecordAudit("inventory", "Receiving scan started", "Focused receiving session opened");
            ClosingScanOverlayTitleText.Text = "Receive New Inventory";
            ClosingScanOverlayCloseButton.Content = "Update Inventory";
            ClosingScanOverlayContent.Children.Clear();
            ClosingScanOverlayContent.Children.Add(content);
            ClosingScanOverlay.Visibility = Visibility.Visible;
            ApplyOverlaySize();
            _ = content.Focus(FocusState.Programmatic);
            var saved = await completed.Task;
            if (saved)
            {
                StatusText.Text = $"Received {stagedRows.Count.ToString(CultureInfo.CurrentCulture)} new bundle{(stagedRows.Count == 1 ? string.Empty : "s")}.";
                _ = SpeakAsync("Receiving complete");
                RefreshOperationalPages();
                foreach (var gameId in stagedRows.Select(row => row.GameId).Distinct(StringComparer.OrdinalIgnoreCase))
                    _ = EnsureGameImageCachedForGameAsync(gameId);
            }
        }
        finally
        {
            ClosingScanOverlayCloseButton.Click -= finishHandler;
            cancelButton.Click -= cancelHandler;
            ClosingScanOverlay.SizeChanged -= overlaySizeChanged;
            ClosingScanOverlay.Visibility = Visibility.Collapsed;
            ClosingScanOverlayContent.Children.Clear();
            ClosingScanOverlayCloseButton.Content = "Close Scanning";
            ClosingScanOverlayCloseButton.IsEnabled = true;
            dialogScanBuffer.Clear();
            _useFocusedScannerCapture = previousFocusedScannerCapture;
            _isWorkflowDialogOpen = false;
        }
    }

    private async Task<bool> ShowReceivingGameSetupDialogAsync(ReceivingScanRow bundle)
    {
        var existingGame = FindKnownGame(bundle.GameId);
        var nameBox = new TextBox
        {
            Header = "Game name",
            Text = existingGame?.Name is { Length: > 0 } existingName ? existingName : $"Game {bundle.GameId}",
            PlaceholderText = "Display name"
        };
        var priceBox = new NumberBox
        {
            Header = "Game price ($)",
            Value = existingGame?.PriceCents > 0 ? existingGame.PriceCents / 100d : double.NaN,
            Minimum = 1,
            SmallChange = 1,
            LargeChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        var statusText = new TextBlock
        {
            Text = "Enter the ticket price. Bundle total is automatic: $900 for a $50 ticket; otherwise $500.",
            TextWrapping = TextWrapping.Wrap
        };
        var priceScanBuffer = new StringBuilder();
        priceBox.AddHandler(
            UIElement.KeyDownEvent,
            new KeyEventHandler((_, args) =>
            {
                if (_useFocusedScannerCapture || !_scannerInput.IsActivelyCapturing)
                    ObserveFocusedCommandScanKey(args, priceScanBuffer, scan =>
                        _scannerScanOverride?.Invoke(scan) == true);
            }),
            handledEventsToo: true);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Game setup required",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Game {bundle.GameId} | Bundle {bundle.BundleId}",
                        TextWrapping = TextWrapping.Wrap
                    },
                    nameBox,
                    priceBox,
                    statusText
                }
            },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var priceCents = PriceCentsFromNumberBox(priceBox);
            var bundlePriceCents = AutomaticBundlePriceCents(priceCents);
            if (TryValidateGameTicketConfiguration(priceCents, bundlePriceCents, out var configurationError))
                return;

            args.Cancel = true;
            statusText.Text = configurationError;
        };
        dialog.Opened += (_, _) => _ = priceBox.Focus(FocusState.Programmatic);

        var previousScannerOverride = _scannerScanOverride;
        _scannerScanOverride = scan =>
        {
            if (scan.Kind != ScanKind.Price)
            {
                statusText.Text = "Scan a price label, or enter the ticket price.";
                TryRecordAudit("scanner", "Receiving price scan rejected", $"Expected price command: {scan.Raw}");
                _ = SpeakAsync("Scan again.");
                return true;
            }

            priceBox.Value = (scan.PriceCents ?? 0) / 100d;
            statusText.Text = $"Price {MoneyText(scan.PriceCents ?? 0)} captured.";
            TryRecordAudit("scanner", "Receiving price scan captured", $"Price {MoneyText(scan.PriceCents ?? 0)} for game {bundle.GameId}");
            return true;
        };
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return false;
        }
        finally
        {
            priceScanBuffer.Clear();
            _scannerScanOverride = previousScannerOverride;
        }

        var priceCents = PriceCentsFromNumberBox(priceBox);
        var bundlePriceCents = AutomaticBundlePriceCents(priceCents);
        if (!TryValidateGameTicketConfiguration(priceCents, bundlePriceCents, out var configurationError))
        {
            StatusText.Text = $"Game {bundle.GameId} was not saved: {configurationError}";
            return false;
        }

        return UpsertManualGameRecord(new GameCatalogRecord(
            bundle.GameId,
            string.IsNullOrWhiteSpace(nameBox.Text) ? $"Game {bundle.GameId}" : nameBox.Text.Trim(),
            priceCents,
            bundlePriceCents,
            "Receiving",
            existingGame?.ImageUri ?? DefaultGameImageUri,
            existingGame?.ImageStatus ?? "Image not uploaded"));
    }

    private async void StartClosingScanButton_Click(object sender, RoutedEventArgs e)
    {
        await StartClosingScanWorkflowAsync();
    }

    private async Task StartClosingScanWorkflowAsync()
    {
        if (!EnsureLicenseAllowsOperation("starting closing scans"))
            return;

        if (_isWorkflowDialogOpen)
            return;

        ExitClosingReportContext();
        ClosingTabs.SelectedItem = ClosingScanEvidenceTab;
        var isResuming = _closingScanRows.Count > 0 ||
            _closingScannedBins.Count > 0 ||
            _closingScanIssues.Count > 0 ||
            _closingUnmatchedTickets.Count > 0;
        RefreshClosingActionState();
        RefreshClosingBins();
        ClosingStatusText.Text = isResuming
            ? $"Closing scan resumed with {_closingScanRows.Count.ToString(CultureInfo.CurrentCulture)} existing scan row{(_closingScanRows.Count == 1 ? string.Empty : "s")}."
            : "Closing scan started. Scan the current ticket from each physical bin.";
        AppLog.Info(isResuming ? "Closing scan overlay resuming." : "Closing scan overlay starting.");
        TryRecordAudit(
            "closing",
            isResuming ? "Closing scan resumed" : "Closing scan started",
            isResuming
                ? $"Rows {_closingScanRows.Count.ToString(CultureInfo.InvariantCulture)}, scanned bins {_closingScannedBins.Count.ToString(CultureInfo.InvariantCulture)}, issues {_closingScanIssues.Count.ToString(CultureInfo.InvariantCulture)}, unmatched {_closingUnmatchedTickets.Count.ToString(CultureInfo.InvariantCulture)}"
                : $"{ActiveClosingBinCount().ToString(CultureInfo.InvariantCulture)} active bins expected");
        _ = SpeakAsync(isResuming ? "Continue scanning." : "Start scanning.");

        var dialogScanBuffer = new StringBuilder();
        var scanPromptText = new TextBlock
        {
            Text = "Ready for ticket scans",
            Style = (Style)Application.Current.Resources["SlSectionTitleTextStyle"],
            TextWrapping = TextWrapping.Wrap
        };
        var totalText = new TextBlock
        {
            Text = _closingScanRows.Count.ToString(CultureInfo.CurrentCulture),
            Style = (Style)Application.Current.Resources["SlMetricTextStyle"],
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
        var statusText = new TextBlock
        {
            Text = "Ready for closing scan.",
            Style = (Style)Application.Current.Resources["SlCaptionTextStyle"],
            TextWrapping = TextWrapping.Wrap
        };
        var scanList = new ListView
        {
            ItemsSource = _closingScanRows,
            DisplayMemberPath = "DisplayText",
            SelectionMode = ListViewSelectionMode.Single,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var discardSelectedScanButton = new Button
        {
            Content = "Discard Selected Error",
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 172,
            IsEnabled = false
        };

        void RefreshDialogTotals()
        {
            totalText.Text = _closingScanRows.Count.ToString(CultureInfo.CurrentCulture);
            if (_selectedClosingReport is null)
                ClosingEvidenceText.Text = $"{_closingScannedBins.Count.ToString(CultureInfo.CurrentCulture)} / {ActiveClosingBinCount().ToString(CultureInfo.CurrentCulture)}";
        }

        scanList.SelectionChanged += (_, _) =>
        {
            discardSelectedScanButton.IsEnabled =
                scanList.SelectedItem is ClosingScanRow { CanDiscard: true };
        };
        discardSelectedScanButton.Click += (_, _) =>
        {
            if (scanList.SelectedItem is not ClosingScanRow { CanDiscard: true } selectedRow)
                return;

            DiscardClosingScanError(selectedRow);
            scanList.SelectedItem = null;
            statusText.Text = "Selected rejected scan was discarded. Existing valid scans were kept.";
            RefreshDialogTotals();
            RefreshClosingBins();
            RefreshClosingActionState();
        };

        void AcceptDialogScan(string raw)
        {
            try
            {
                raw = raw.Trim();
                if (string.IsNullOrWhiteSpace(raw))
                    return;

                AppLog.Info($"Closing scan received: {raw}");
                _closingScanCaptured = true;
                var processed = false;
                foreach (var segment in SplitImportScanInput(raw))
                {
                    processed = true;
                    AppLog.Info($"Closing scan segment: {segment}");
                    ProcessClosingScanSegment(segment, statusText);
                }

                if (!processed)
                    statusText.Text = "Scan was empty.";

                RefreshDialogTotals();
                RefreshClosingBins();
                RefreshClosingActionState();
            }
            catch (Exception ex)
            {
                AppLog.Error("Closing scan failed to process barcode.", ex);
                TryRecordAudit("closing", "Closing scan processing failed", ex.Message);
                statusText.Text = $"Closing scan failed to process the last barcode: {ex.Message}";
                ClosingStatusText.Text = "Closing scan failed to process the last barcode. Re-scan or close scanning and restart.";
            }
        }

        var rootSize = Content.XamlRoot?.Size ?? new Windows.Foundation.Size(0, 0);
        var content = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsTabStop = true,
            RowSpacing = 12
        };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Grid.SetColumnSpan(scanPromptText, 2);
        content.Children.Add(scanPromptText);
        content.AddHandler(
            UIElement.KeyDownEvent,
            new KeyEventHandler((_, args) =>
                CaptureGlobalScanKey(args, dialogScanBuffer, AcceptDialogScan, statusText)),
            handledEventsToo: true);

        var totalView = new Viewbox
        {
            Stretch = Stretch.Uniform,
            MaxHeight = 88,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = totalText
        };
        Grid.SetRow(totalView, 0);

        var totalLabel = new TextBlock
        {
            Text = "total scanned",
            Style = (Style)Application.Current.Resources["SlCaptionTextStyle"],
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.WrapWholeWords
        };
        Grid.SetRow(totalLabel, 1);

        var totalGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto }
            },
            Children =
            {
                totalView,
                totalLabel
            }
        };

        var totalPanel = new Border
        {
            Style = (Style)Application.Current.Resources["SlPanelBorderStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = totalGrid
        };
        content.Children.Add(totalPanel);

        var leftGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            RowSpacing = 8
        };
        leftGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        leftGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        leftGrid.Children.Add(new TextBlock
        {
            Text = "Scanned number",
            Style = (Style)Application.Current.Resources["SlSectionTitleTextStyle"]
        });
        Grid.SetRow(scanList, 1);
        leftGrid.Children.Add(scanList);

        var leftPanel = new Border
        {
            Style = (Style)Application.Current.Resources["SlPanelBorderStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = leftGrid
        };
        content.Children.Add(leftPanel);

        var cancelButton = new Button
        {
            Content = "Cancel Closing Scan",
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 148
        };
        statusText.MaxLines = 2;
        statusText.TextTrimming = TextTrimming.CharacterEllipsis;
        var footerButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        footerButtons.Children.Add(discardSelectedScanButton);
        footerButtons.Children.Add(cancelButton);
        var footer = new Grid { RowSpacing = 8 };
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        footer.Children.Add(statusText);
        Grid.SetRow(footerButtons, 1);
        footer.Children.Add(footerButtons);
        content.Children.Add(footer);

        void ApplyResponsiveDialogLayout(double width)
        {
            var stacked = width > 0 && width < 560;
            var verticalFooterButtons = width > 0 && width < 400;
            footerButtons.Orientation = verticalFooterButtons
                ? Orientation.Vertical
                : Orientation.Horizontal;
            footerButtons.HorizontalAlignment = verticalFooterButtons
                ? HorizontalAlignment.Stretch
                : HorizontalAlignment.Right;
            discardSelectedScanButton.HorizontalAlignment = verticalFooterButtons
                ? HorizontalAlignment.Stretch
                : HorizontalAlignment.Right;
            cancelButton.HorizontalAlignment = verticalFooterButtons
                ? HorizontalAlignment.Stretch
                : HorizontalAlignment.Right;
            content.ColumnDefinitions[0].Width = new GridLength(2, GridUnitType.Star);
            content.ColumnDefinitions[1].Width = stacked
                ? new GridLength(0)
                : new GridLength(1, GridUnitType.Star);
            content.RowDefinitions[1].Height = stacked
                ? GridLength.Auto
                : new GridLength(1, GridUnitType.Star);
            content.RowDefinitions[2].Height = stacked
                ? new GridLength(1, GridUnitType.Star)
                : GridLength.Auto;
            content.RowDefinitions[3].Height = stacked
                ? GridLength.Auto
                : new GridLength(0);

            Grid.SetRow(leftPanel, stacked ? 2 : 1);
            Grid.SetColumn(leftPanel, 0);
            Grid.SetColumnSpan(leftPanel, stacked ? 2 : 1);

            Grid.SetRow(totalPanel, stacked ? 1 : 1);
            Grid.SetColumn(totalPanel, stacked ? 0 : 1);
            Grid.SetColumnSpan(totalPanel, stacked ? 2 : 1);

            Grid.SetRow(footer, stacked ? 3 : 2);
            Grid.SetColumn(footer, 0);
            Grid.SetColumnSpan(footer, 2);
        }

        ApplyResponsiveDialogLayout(rootSize.Width);
        content.SizeChanged += (_, args) => ApplyResponsiveDialogLayout(args.NewSize.Width);

        void ApplyDialogSize(Windows.Foundation.Size size)
        {
            var panelWidth = size.Width > 0
                ? Math.Max(320, Math.Min(1180, size.Width - 32))
                : 1120;
            var panelHeight = size.Height > 0
                ? Math.Max(280, Math.Min(760, size.Height - 32))
                : 680;
            ClosingScanOverlayPanel.Width = panelWidth;
            ClosingScanOverlayPanel.Height = panelHeight;
            ApplyResponsiveDialogLayout(panelWidth - 32);
        }

        ApplyDialogSize(rootSize);
        void ApplyOverlaySizeFromActual()
        {
            var width = ClosingScanOverlay.ActualWidth > 0
                ? ClosingScanOverlay.ActualWidth
                : RootGrid.ActualWidth;
            var height = ClosingScanOverlay.ActualHeight > 0
                ? ClosingScanOverlay.ActualHeight
                : RootGrid.ActualHeight;
            ApplyDialogSize(new Windows.Foundation.Size(width, height));
        }

        SizeChangedEventHandler overlaySizeChanged = (_, _) => ApplyOverlaySizeFromActual();
        var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler closeHandler = (_, _) => closed.TrySetResult(false);
        RoutedEventHandler cancelHandler = async (_, _) =>
        {
            if (_closingScanRows.Count == 0 &&
                _closingScannedBins.Count == 0 &&
                _closingScanIssues.Count == 0 &&
                _closingUnmatchedTickets.Count == 0)
            {
                closed.TrySetResult(true);
                return;
            }

            var confirm = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Discard closing scans?",
                Content = $"Discard {_closingScanRows.Count.ToString(CultureInfo.CurrentCulture)} temporary closing scan{(_closingScanRows.Count == 1 ? string.Empty : "s")}? No sales, inventory, or shift data will be changed.",
                PrimaryButtonText = "Discard",
                CloseButtonText = "Keep Scanning",
                DefaultButton = ContentDialogButton.Close
            };
            if (await confirm.ShowAsync() == ContentDialogResult.Primary)
                closed.TrySetResult(true);
            else
                _ = content.Focus(FocusState.Programmatic);
        };
        ClosingScanOverlayCloseButton.Click += closeHandler;
        cancelButton.Click += cancelHandler;
        ClosingScanOverlay.SizeChanged += overlaySizeChanged;

        _isWorkflowDialogOpen = true;
        var previousFocusedScannerCapture = _useFocusedScannerCapture;
        _useFocusedScannerCapture = true;
        try
        {
            AppLog.Info("Closing scan overlay opened.");
            ClosingScanOverlayTitleText.Text = "Closing Scan";
            ClosingScanOverlayContent.Children.Clear();
            ClosingScanOverlayContent.Children.Add(content);
            ClosingScanOverlay.Visibility = Visibility.Visible;
            ApplyOverlaySizeFromActual();
            _ = content.Focus(FocusState.Programmatic);
            var discarded = await closed.Task;
            var rowCount = _closingScanRows.Count;
            var scannedBinCount = _closingScannedBins.Count;
            var issueCount = _closingScanIssues.Count;
            var unmatchedCount = _closingUnmatchedTickets.Count;

            if (discarded)
            {
                TryRecordAudit(
                    "closing",
                    "Closing scan cancelled",
                    $"Discarded rows {rowCount.ToString(CultureInfo.InvariantCulture)}, scanned bins {scannedBinCount.ToString(CultureInfo.InvariantCulture)}, issues {issueCount.ToString(CultureInfo.InvariantCulture)}, unmatched {unmatchedCount.ToString(CultureInfo.InvariantCulture)}; no shift data changed");
                ResetClosingScanState();
                ClosingStatusText.Text = "Closing scan cancelled. Temporary scan evidence was discarded; no shift data changed.";
            }
            else
            {
                AppLog.Info($"Closing scan overlay closed. rows={rowCount.ToString(CultureInfo.InvariantCulture)}; scannedBins={scannedBinCount.ToString(CultureInfo.InvariantCulture)}; issues={issueCount.ToString(CultureInfo.InvariantCulture)}; unmatched={unmatchedCount.ToString(CultureInfo.InvariantCulture)}.");
                TryRecordAudit(
                    "closing",
                    "Closing scan closed",
                    $"Rows {rowCount.ToString(CultureInfo.InvariantCulture)}, scanned bins {scannedBinCount.ToString(CultureInfo.InvariantCulture)}, issues {issueCount.ToString(CultureInfo.InvariantCulture)}, unmatched {unmatchedCount.ToString(CultureInfo.InvariantCulture)}");
                _closingScanCaptured = true;
                ClosingStatusText.Text = $"{scannedBinCount.ToString(CultureInfo.CurrentCulture)} bin{(scannedBinCount == 1 ? string.Empty : "s")} scanned. Unscanned active bins remain marked.";
            }
        }
        finally
        {
            ClosingScanOverlayCloseButton.Click -= closeHandler;
            cancelButton.Click -= cancelHandler;
            ClosingScanOverlay.SizeChanged -= overlaySizeChanged;
            ClosingScanOverlay.Visibility = Visibility.Collapsed;
            ClosingScanOverlayContent.Children.Clear();
            dialogScanBuffer.Clear();
            _useFocusedScannerCapture = previousFocusedScannerCapture;
            _isWorkflowDialogOpen = false;
            RefreshClosingBins();
            RefreshClosingActionState();
        }
    }

    private void ResetClosingScanState()
    {
        _closingScannedBins.Clear();
        _closingScannedBundleKeys.Clear();
        _closingCurrentPlacements.Clear();
        _closingUnmatchedTickets.Clear();
        _closingResolvedPlacements.Clear();
        _closingScanRows.Clear();
        _closingScanIssues.Clear();
        _closingScanSales.Clear();
        _closingScanCaptured = false;
    }

    private int ActiveClosingBinCount() =>
        _imports
            .Where(i => !i.IsSoldOut)
            .Select(i => i.Bin)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(bin => int.TryParse(bin, NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
                          IsConfiguredBin(number));

    private void ProcessClosingScanSegment(string raw, TextBlock statusText)
    {
        var ticket = TryParseImportTicket(raw);
        if (ticket is not null)
        {
            var activeBundle = FindActiveBundle(ticket);
            if (activeBundle is null && FindPlacedBundle(ticket)?.IsSoldOut == true)
            {
                _closingScanRows.Insert(0, new ClosingScanRow(raw, "Sold out"));
                TryRecordAudit("closing", "Closing scan rejected", $"Sold-out bundle scanned: game {ticket.GameId}, bundle {ticket.BundleId}");
                statusText.Text = "Scan error: this bundle is already sold out.";
                _ = SpeakAsync("Bundle sold out.");
                return;
            }

            if (activeBundle is null ||
                !int.TryParse(activeBundle.Bin, NumberStyles.None, CultureInfo.InvariantCulture, out var binNumber) ||
                !IsConfiguredBin(binNumber))
            {
                if (!_closingUnmatchedTickets.Any(t =>
                    string.Equals(t.GameId, ticket.GameId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(t.BundleId, ticket.BundleId, StringComparison.OrdinalIgnoreCase)))
                {
                    _closingUnmatchedTickets.Add(ticket);
                }

                _closingScanRows.Insert(0, new ClosingScanRow(raw, "No active bin"));
                TryRecordAudit(
                    "closing",
                    "Closing scan unmatched",
                    $"Game {ticket.GameId}, bundle {ticket.BundleId}, ticket {ticket.Ticket}; no active bin");
                statusText.Text = $"No active bin matched scan {raw}.";
                return;
            }

            var closingBundle = _closingCurrentPlacements.FirstOrDefault(placement =>
                string.Equals(placement.GameId, activeBundle.GameId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(placement.BundleId, activeBundle.BundleId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(placement.Bin, activeBundle.Bin, StringComparison.OrdinalIgnoreCase)) ?? activeBundle;
            if (closingBundle.IsSoldOut)
            {
                _closingScanRows.Insert(0, new ClosingScanRow(raw, "Sold out"));
                TryRecordAudit("closing", "Closing scan rejected", $"Sold-out bundle scanned: game {ticket.GameId}, bundle {ticket.BundleId}");
                statusText.Text = "Scan error: this bundle is already sold out.";
                _ = SpeakAsync("Bundle sold out.");
                return;
            }

            if (!TryBuildTicketBackfillSale(
                    DateTime.Now,
                    closingBundle,
                    ticket.Ticket,
                    "closing_gap_fill_sold",
                    out var backfill,
                    out var rangeError))
            {
                _closingScanRows.Insert(0, new ClosingScanRow(raw, "Outside bundle range"));
                AddClosingScanIssue(raw, "Ticket outside bundle range", rangeError);
                TryRecordAudit("closing", "Closing scan rejected", rangeError);
                statusText.Text = "Scan error: ticket is outside the configured bundle range.";
                _ = SpeakAsync("Scan again.");
                return;
            }

            _closingScannedBins.Add(binNumber);
            _closingScannedBundleKeys.Add(BundleKey(activeBundle));
            ClearClosingScanErrorsForBundle(ticket);
            UpsertClosingScanSale(BundleKey(activeBundle), backfill.Sale);
            ReplaceClosingCurrentPlacement(activeBundle with
            {
                Ticket = backfill.IsBundleComplete ? ticket.Ticket : backfill.NextTicket,
                IsSoldOut = backfill.IsBundleComplete
            });
            _closingScanRows.Insert(0, new ClosingScanRow(
                $"Bin {binNumber.ToString(CultureInfo.CurrentCulture)} | {backfill.Sale.Ticket}",
                "Scanned")
            {
                Raw = raw
            });
            TryRecordAudit(
                "closing",
                "Closing scan captured",
                backfill.IsBundleComplete
                    ? $"Game {ticket.GameId}, bundle {ticket.BundleId}, bin {binNumber.ToString(CultureInfo.InvariantCulture)}, scanned final ticket {ticket.Ticket}, reconciled range {backfill.Sale.Ticket}, quantity {backfill.Sale.Quantity.ToString(CultureInfo.InvariantCulture)}, amount {backfill.Sale.AmountText}; bundle completed"
                    : $"Game {ticket.GameId}, bundle {ticket.BundleId}, bin {binNumber.ToString(CultureInfo.InvariantCulture)}, scanned ticket {ticket.Ticket}, reconciled range {backfill.Sale.Ticket}, quantity {backfill.Sale.Quantity.ToString(CultureInfo.InvariantCulture)}, amount {backfill.Sale.AmountText}, next {backfill.NextTicket}");
            statusText.Text = backfill.IsBundleComplete
                ? $"Bin {binNumber.ToString(CultureInfo.CurrentCulture)} scanned. Bundle is sold out."
                : $"Bin {binNumber.ToString(CultureInfo.CurrentCulture)} scanned. {backfill.Sale.Quantity.ToString(CultureInfo.CurrentCulture)} ticket{(backfill.Sale.Quantity == 1 ? string.Empty : "s")} captured.";
            return;
        }

        if (TryParseBinNumber(raw, out var directBin) && IsConfiguredBin(directBin))
        {
            _closingScanRows.Insert(0, new ClosingScanRow(raw, "Ignored bin scan"));
            TryRecordAudit("closing", "Closing bin scan ignored", $"Bin {directBin.ToString(CultureInfo.InvariantCulture)}; closing accepts ticket barcodes only");
            statusText.Text = "Closing scan accepts ticket barcodes only. Bin scan ignored.";
            _ = SpeakAsync("Ticket only.");
            return;
        }

        _closingScanRows.Insert(0, new ClosingScanRow(raw, "Unrecognized"));
        AddClosingScanIssue(
            raw,
            "Unrecognized scan",
            $"Scan {raw} was not recognized as a ticket barcode. Re-scan the ticket or resolve before finalizing.");
        TryRecordAudit("closing", "Closing scan rejected", $"Unrecognized scan {raw}");
        statusText.Text = $"Scan was not recognized: {raw}";
        _ = SpeakAsync("Scan again.");
    }

    private void AddClosingScanIssue(string raw, string title, string detail) =>
        _closingScanIssues.Add(new ClosingScanIssue(raw, title, detail));

    private void ClearClosingScanErrorsForBundle(ImportTicket ticket)
    {
        foreach (var row in _closingScanRows
                     .Where(row => row.CanDiscard && ClosingRowMatchesBundle(row, ticket))
                     .ToList())
        {
            _closingScanRows.Remove(row);
        }

        _closingScanIssues.RemoveAll(issue =>
            TryParseImportTicket(issue.Raw) is { } issueTicket &&
            SamePhysicalBundle(issueTicket, ticket));
        _closingUnmatchedTickets.RemoveAll(unmatched => SamePhysicalBundle(unmatched, ticket));
    }

    private void DiscardClosingScanError(ClosingScanRow row)
    {
        _closingScanRows.Remove(row);
        _closingScanIssues.RemoveAll(issue =>
            string.Equals(issue.Raw, row.Raw, StringComparison.OrdinalIgnoreCase));

        if (TryParseImportTicket(row.Raw) is { } ticket &&
            !_closingScanRows.Any(candidate =>
                candidate.CanDiscard && ClosingRowMatchesBundle(candidate, ticket)))
        {
            _closingUnmatchedTickets.RemoveAll(unmatched => SamePhysicalBundle(unmatched, ticket));
        }

        if (_closingScanRows.Count == 0 &&
            _closingScannedBins.Count == 0 &&
            _closingUnmatchedTickets.Count == 0 &&
            _closingScanIssues.Count == 0)
        {
            _closingScanCaptured = false;
        }

        TryRecordAudit(
            "closing",
            "Closing scan error discarded",
            $"Raw {row.Raw}, status {row.Status}; valid closing evidence retained");
    }

    private bool ClosingRowMatchesBundle(ClosingScanRow row, ImportTicket ticket) =>
        TryParseImportTicket(row.Raw) is { } rowTicket && SamePhysicalBundle(rowTicket, ticket);

    private static bool SamePhysicalBundle(ImportTicket left, ImportTicket right) =>
        string.Equals(left.GameId, right.GameId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.BundleId, right.BundleId, StringComparison.OrdinalIgnoreCase);

    private void UpsertClosingScanSale(string bundleKey, SaleLine sale)
    {
        _closingScanSales.RemoveAll(s => string.Equals(s.BundleKey, bundleKey, StringComparison.OrdinalIgnoreCase));
        _closingScanSales.Add(new ClosingScanSale(bundleKey, sale));
    }

    private void ReplaceClosingCurrentPlacement(ImportLine placement)
    {
        _closingCurrentPlacements.RemoveAll(i =>
            string.Equals(i.GameId, placement.GameId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(i.BundleId, placement.BundleId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(i.Bin, placement.Bin, StringComparison.OrdinalIgnoreCase));
        _closingCurrentPlacements.Add(placement);
    }

    private void RefreshClosingActionState()
    {
        RefreshTotals();
        var licenseAvailable = IsLicenseAvailableForOperation();
        StartClosingScanButton.Content = _closingScanRows.Count > 0 ||
            _closingScannedBins.Count > 0 ||
            _closingScanIssues.Count > 0 ||
            _closingUnmatchedTickets.Count > 0
                ? "Continue Closing Scan"
                : "Start Closing Scan";
        ResolveClosingIssuesButton.IsEnabled = licenseAvailable && _closingUnmatchedTickets.Count > 0;
        FinalizeClosingButton.IsEnabled = licenseAvailable &&
            _closingScanCaptured &&
            _closingScanIssues.Count == 0 &&
            _closingUnmatchedTickets.Count == 0;

        if (!licenseAvailable)
        {
            ClosingExceptionText.Text = "License expired. Check license registration before continuing closing.";
            return;
        }

        if (_closingScanIssues.Count > 0)
        {
            ClosingExceptionText.Text = "One or more rejected scans remain. Continue Closing Scan, then rescan the correct ticket or discard the selected error.";
            return;
        }

        if (_closingUnmatchedTickets.Count > 0)
        {
            var count = _closingUnmatchedTickets.Count.ToString(CultureInfo.CurrentCulture);
            ClosingExceptionText.Text = $"{count} scanned ticket{(_closingUnmatchedTickets.Count == 1 ? string.Empty : "s")} did not match an active bin. Assign the unmatched scan before finalizing.";
            return;
        }

        if (!_closingScanCaptured)
        {
            ClosingExceptionText.Text = "Matched scan evidence auto reconciles. Unscanned active bundles close out when the shift is finalized.";
            return;
        }

        var closedOutCount = ClosingSoldOutBundles().Count;
        ClosingExceptionText.Text = closedOutCount == 0
            ? "All scanned ticket evidence matched active bins. Closing state is ready to finalize."
            : $"{closedOutCount.ToString(CultureInfo.CurrentCulture)} unscanned active bundle{(closedOutCount == 1 ? string.Empty : "s")} will be auto closed out as closing gap-fill sold when finalized.";
    }

    private List<ImportLine> ClosingSoldOutBundles() =>
        _imports
            .Where(i => !i.IsSoldOut && !_closingScannedBundleKeys.Contains(BundleKey(i)))
            .GroupBy(i => BundleKey(i), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

    private async void ResolveClosingIssuesButton_Click(object sender, RoutedEventArgs e)
    {
        while (_closingUnmatchedTickets.Count > 0)
        {
            var ticket = _closingUnmatchedTickets[0];
            var binBox = new TextBox
            {
                Header = "Closing bin",
                PlaceholderText = "Enter bin number"
            };
            var statusText = new TextBlock
            {
                Text = "Choose the physical bin where this scanned bundle belongs.",
                TextWrapping = TextWrapping.Wrap
            };
            int? selectedBin = null;

            var content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Game {ticket.GameId} | Bundle {ticket.BundleId} | Ticket {ticket.Ticket}",
                        TextWrapping = TextWrapping.Wrap
                    },
                    binBox,
                    statusText
                }
            };

            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Resolve Closing Scan",
                Content = content,
                PrimaryButtonText = "Use This Bin",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };

            bool TryAcceptBin()
            {
                if (!TryParseBinNumber(binBox.Text, out var parsedBin))
                {
                    statusText.Text = "Enter or scan a valid bin barcode.";
                    return false;
                }

                if (!IsConfiguredBin(parsedBin))
                {
                    statusText.Text = $"Wrong bin {parsedBin.ToString(CultureInfo.CurrentCulture)}. Enter a configured bin.";
                    _ = SpeakAsync("Wrong bin.");
                    return false;
                }

                var binText = parsedBin.ToString(CultureInfo.InvariantCulture);
                var alreadyResolved = _closingResolvedPlacements.FirstOrDefault(i =>
                    string.Equals(i.Bin, binText, StringComparison.OrdinalIgnoreCase));
                if (alreadyResolved is not null)
                {
                    statusText.Text = $"Bin {binText} is already resolved to game {alreadyResolved.GameId}, bundle {alreadyResolved.BundleId}. Choose a different bin.";
                    return false;
                }

                var scannedExisting = _imports.FirstOrDefault(i =>
                    string.Equals(i.Bin, binText, StringComparison.OrdinalIgnoreCase) &&
                    _closingScannedBundleKeys.Contains(BundleKey(i)));
                if (scannedExisting is not null)
                {
                    statusText.Text = $"Bin {binText} already has scanned evidence for game {scannedExisting.GameId}, bundle {scannedExisting.BundleId}. Choose a different bin.";
                    return false;
                }

                var existing = _imports.FirstOrDefault(i =>
                    string.Equals(i.Bin, binText, StringComparison.OrdinalIgnoreCase) &&
                    !_closingScannedBundleKeys.Contains(BundleKey(i)));
                if (existing is not null)
                {
                    statusText.Text = $"Bin {binText} currently has game {existing.GameId}, bundle {existing.BundleId}, ticket {existing.Ticket}. If not scanned elsewhere, it will be closed out as closing gap-fill sold.";
                }

                selectedBin = parsedBin;
                return true;
            }

            binBox.KeyDown += (_, args) =>
            {
                if (args.Key != VirtualKey.Enter)
                    return;

                args.Handled = true;
                if (TryAcceptBin())
                    dialog.Hide();
            };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (TryAcceptBin())
                    return;

                args.Cancel = true;
            };
            dialog.Opened += (_, _) =>
            {
                _ = binBox.Focus(FocusState.Programmatic);
            };

            _isWorkflowDialogOpen = true;
            ContentDialogResult result;
            try
            {
                result = await dialog.ShowAsync();
            }
            finally
            {
                _isWorkflowDialogOpen = false;
            }

            if (result != ContentDialogResult.Primary && selectedBin is null)
            {
                ClosingStatusText.Text = "Unmatched scan assignment cancelled.";
                TryRecordAudit(
                    "closing",
                    "Closing reconciliation cancelled",
                    $"Game {ticket.GameId}, bundle {ticket.BundleId}, ticket {ticket.Ticket} remains unmatched");
                break;
            }

            if (selectedBin is null)
                continue;

            var bin = selectedBin.Value.ToString(CultureInfo.InvariantCulture);
            var resolved = new ImportLine(ticket.GameId, ticket.BundleId, ticket.Ticket, bin, "closing_reconciliation");
            _closingResolvedPlacements.RemoveAll(i =>
                string.Equals(i.GameId, resolved.GameId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(i.BundleId, resolved.BundleId, StringComparison.OrdinalIgnoreCase));
            _closingResolvedPlacements.Add(resolved);
            _closingScannedBins.Add(selectedBin.Value);
            _closingScannedBundleKeys.Add(BundleKey(ticket));
            _closingUnmatchedTickets.RemoveAt(0);
            ClosingStatusText.Text = $"Resolved game {ticket.GameId}, bundle {ticket.BundleId} to bin {bin}.";
            TryRecordAudit(
                "closing",
                "Closing bundle reconciled",
                $"Game {ticket.GameId}, bundle {ticket.BundleId}, ticket {ticket.Ticket}, assigned bin {bin}");
        }

        RefreshClosingBins();
        RefreshClosingActionState();
    }

    private async void FinalizeClosingButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLicenseAllowsOperation("finalizing closing"))
            return;

        if (!_closingScanCaptured)
        {
            ClosingStatusText.Text = "Run closing scan before finalizing.";
            return;
        }

        if (_closingScanIssues.Count > 0)
        {
            ClosingStatusText.Text = "Continue Closing Scan and rescan the correct ticket, or discard each rejected scan before finalizing.";
            return;
        }

        if (_closingUnmatchedTickets.Count > 0)
        {
            ClosingStatusText.Text = "Resolve unmatched scanned tickets before finalizing.";
            return;
        }

        if (!TryReadMoneyCents(ClosingOnlineSaleBox, out var onlineSaleCents) ||
            !TryReadMoneyCents(ClosingOnlineCashoutBox, out var onlineCashoutCents) ||
            !TryReadMoneyCents(ClosingInstantCashoutBox, out var instantCashoutCents))
        {
            ClosingStatusText.Text = "Manual totals must be valid dollar amounts.";
            StatusText.Text = "Manual closing totals must be valid dollar amounts.";
            return;
        }

        var unscannedSoldOutBundles = ClosingSoldOutBundles();
        var closedAtUtc = DateTime.UtcNow;
        if (!TryBuildClosingSoldOutChanges(
                unscannedSoldOutBundles,
                closedAtUtc,
                out var soldOutSales,
                out var soldOutPlacements,
                out var configurationError))
        {
            ClosingStatusText.Text = $"Closing blocked: {configurationError}";
            StatusText.Text = "Shift was not closed because game setup is invalid.";
            TryRecordAudit("closing", "Closing finalization blocked", configurationError);
            _ = SpeakAsync("Game setup required.");
            return;
        }

        var currentBundlesForClosing = _closingCurrentPlacements
            .Concat(soldOutPlacements)
            .GroupBy(BundleKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
        var generatedSales = _closingScanSales
            .Select(s => s.Sale)
            .Concat(soldOutSales)
            .ToList();
        var instantTicketSalesCents = (long)Math.Round(
            (_sales.Sum(s => s.Amount) + generatedSales.Sum(s => s.Amount)) * 100m,
            MidpointRounding.AwayFromZero);
        var expectedCashCents = instantTicketSalesCents + onlineSaleCents - instantCashoutCents - onlineCashoutCents;
        var activatedBundles = CurrentShiftActivationCount();
        var selectedEmailAttachments = SelectedSettingsEmailReportNames();
        var closingEmailSummary = BuildClosingEmailSummaryText(
            SettingsEmailSendCheckBox.IsChecked == true,
            selectedEmailAttachments);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Finalize shift closing?",
            Content = $"{_closingScannedBins.Count.ToString(CultureInfo.CurrentCulture)} bin{(_closingScannedBins.Count == 1 ? string.Empty : "s")} scanned. {unscannedSoldOutBundles.Count.ToString(CultureInfo.CurrentCulture)} unscanned active bundle{(unscannedSoldOutBundles.Count == 1 ? string.Empty : "s")} will be marked sold out with a closing gap-fill sale. Sold-out bundles remain assigned and grey in their bins/Rdisplay.{Environment.NewLine}Bundles activated this shift: {activatedBundles.ToString(CultureInfo.CurrentCulture)}{Environment.NewLine}{Environment.NewLine}Instant ticket sales: {MoneyText(instantTicketSalesCents)}{Environment.NewLine}Online sale: {MoneyText(onlineSaleCents)}{Environment.NewLine}Instant cashout: {MoneyText(instantCashoutCents)}{Environment.NewLine}Online cashout: {MoneyText(onlineCashoutCents)}{Environment.NewLine}Expected cash: {MoneyText(expectedCashCents)}{Environment.NewLine}{Environment.NewLine}{closingEmailSummary}",
            PrimaryButtonText = "Finalize Closing",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            ClosingStatusText.Text = "Closing finalization cancelled.";
            return;
        }

        var intervalStartUtc = _lastCloseUtc;
        var reportTarget = BuildClosingReportTarget(closedAtUtc);
        var reportSales = _sales
            .Concat(generatedSales)
            .OrderBy(s => s.SoldAt)
            .ThenBy(s => s.GameId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Ticket, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var closingRecord = new StoredClosingRecord(
            closedAtUtc,
            intervalStartUtc,
            reportTarget.BusinessDate,
            reportTarget.ShiftSequence,
            reportTarget.ShiftLabel,
            reportTarget.Folder,
            _closingScannedBins.Count,
            ActiveClosingBinCount(),
            _sales.Count + generatedSales.Count,
            _sales.Sum(s => s.Quantity) + generatedSales.Sum(s => s.Quantity),
            instantTicketSalesCents,
            onlineSaleCents,
            onlineCashoutCents,
            instantCashoutCents,
            expectedCashCents,
            unscannedSoldOutBundles.Count,
            currentBundlesForClosing.Count,
            _closingResolvedPlacements.Count,
            activatedBundles,
            _openIntervalId,
            _activeActorId,
            _activeUserName);
        var storedCurrentBundles = currentBundlesForClosing
            .Select(i => new StoredImportLine(i.GameId, i.BundleId, i.Ticket, i.Bin, i.Source, i.IsSoldOut))
            .ToList();
        var storedResolvedBundles = _closingResolvedPlacements
            .Select(i => new StoredImportLine(i.GameId, i.BundleId, i.Ticket, i.Bin, i.Source, i.IsSoldOut))
            .ToList();
        var reportRequest = new StoredClosingReportRequest(
            closingRecord,
            reportSales.Select(ToStoredSaleLine).ToList(),
            new List<StoredImportLine>(),
            storedCurrentBundles,
            storedResolvedBundles,
            selectedEmailAttachments.ToList());
        var auditRecord = NewAuditRecord(
            "closing",
            "Shift closed",
            $"{_closingScannedBins.Count.ToString(CultureInfo.InvariantCulture)} scanned bins, {unscannedSoldOutBundles.Count.ToString(CultureInfo.InvariantCulture)} closing-sold-out bundles, {_closingResolvedPlacements.Count.ToString(CultureInfo.InvariantCulture)} resolved bundles, {activatedBundles.ToString(CultureInfo.InvariantCulture)} activated bundles, expected cash {MoneyText(expectedCashCents)}");
        CompleteClosingResult closingResult;
        try
        {
            closingResult = _store.CompleteClosing(
                closedAtUtc,
                closingRecord,
                auditRecord,
                generatedSales.Select(ToStoredSaleLine),
                Array.Empty<StoredImportLine>(),
                storedCurrentBundles,
                storedResolvedBundles,
                reportRequest);
        }
        catch (Exception ex)
        {
            AppLog.Error("Closing finalization failed.", ex);
            ClosingStatusText.Text = $"Closing finalization failed: {ex.Message}";
            return;
        }

        _lastCloseUtc = closedAtUtc;
        _openIntervalId = closingResult.OpenIntervalId;
        foreach (var placement in currentBundlesForClosing)
            ReplaceImportLine(placement);
        foreach (var placement in _closingResolvedPlacements)
        {
            _imports.Add(placement);
            var received = _receivedBundles.FirstOrDefault(bundle =>
                string.Equals(bundle.GameId, placement.GameId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(bundle.BundleId, placement.BundleId, StringComparison.OrdinalIgnoreCase));
            if (received is not null)
                _receivedBundles.Remove(received);
        }
        foreach (var generated in closingResult.GeneratedSales)
            _allSales.Insert(0, FromStoredSaleLine(generated));
        _closingHistoryRows.Insert(0, ClosingHistoryRow.From(closingRecord));
        ApplyClosingHistoryPage(resetPage: true);
        ClosingHistoryListView.SelectedItem = _pagedClosingHistoryRows.FirstOrDefault();
        AddAuditLogRowToUi(auditRecord);
        _sales.Clear();
        ClosingOnlineSaleBox.Value = 0;
        ClosingOnlineCashoutBox.Value = 0;
        ClosingInstantCashoutBox.Value = 0;
        ResetClosingScanState();
        StatusText.Text = $"Shift closed. Generating reports for {reportTarget.ShiftLabel}.";
        ClosingStatusText.Text = $"Closed at {_lastCloseUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}. Report generation is pending.";
        _ = SpeakAsync("Shift closed. New sales count toward the next close.");
        RefreshTotals();
        RefreshOperationalPages();
        _ = ProcessClosingReportJobAsync(
            new StoredClosingReportJob(closingResult.ReportJobId, "pending", 0, string.Empty, closingResult.ReportRequest),
            announceResult: true);
    }

    private bool TryBuildClosingSoldOutChanges(
        IEnumerable<ImportLine> soldOutBundles,
        DateTime closedAtUtc,
        out List<SaleLine> sales,
        out List<ImportLine> placements,
        out string error)
    {
        sales = new List<SaleLine>();
        placements = new List<ImportLine>();
        error = string.Empty;
        foreach (var bundle in soldOutBundles)
        {
            if (!TryGetBundleTicketRange(bundle.GameId, out _, out var lastTicket, out var rangeError))
            {
                error = $"Game {bundle.GameId}, bundle {bundle.BundleId}: {rangeError}";
                return false;
            }

            var finalTicket = FormatTicketSerial(lastTicket, TicketSerialWidth(bundle.Ticket));
            if (!TryBuildTicketBackfillSale(
                    closedAtUtc.ToLocalTime(),
                    bundle,
                    finalTicket,
                    "closing_gap_fill_sold",
                    out var backfill,
                    out var backfillError))
            {
                error = $"Game {bundle.GameId}, bundle {bundle.BundleId}: {backfillError}";
                return false;
            }

            if (backfill.Sale.Amount <= 0)
            {
                error = $"Game {bundle.GameId}, bundle {bundle.BundleId} calculated a non-positive closing sale.";
                return false;
            }

            sales.Add(backfill.Sale);
            placements.Add(bundle with
            {
                Ticket = finalTicket,
                IsSoldOut = true
            });
        }

        return true;
    }

    private ClosingReportTarget BuildClosingReportTarget(DateTime closedAtUtc)
    {
        var closedAtLocal = closedAtUtc.ToLocalTime();
        var businessDate = closedAtLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var shiftSequence = _closingHistoryRows
            .Where(r => string.Equals(r.BusinessDate, businessDate, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.ShiftSequence)
            .DefaultIfEmpty(0)
            .Max() + 1;
        var shiftLabel = $"{businessDate} #{shiftSequence.ToString(CultureInfo.InvariantCulture)}";
        var folderName = $"{businessDate}_shift-{shiftSequence.ToString("000", CultureInfo.InvariantCulture)}";
        var storeName = SanitizePathSegment(string.IsNullOrWhiteSpace(_storeName) ? "SimpleLotto" : _storeName);
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SimpleLotto",
            "reports",
            storeName);
        var folder = Path.Combine(root, folderName);
        if (Directory.Exists(folder))
            folder = Path.Combine(root, $"{folderName}_{closedAtLocal.ToString("HHmmss", CultureInfo.InvariantCulture)}");

        return new ClosingReportTarget(businessDate, shiftSequence, shiftLabel, folder);
    }

    private void WriteClosingReports(StoredClosingReportRequest request)
    {
        var closing = request.Closing;
        var target = new ClosingReportTarget(
            closing.BusinessDate,
            closing.ShiftSequence,
            closing.ShiftLabel,
            closing.ReportFolder);
        var sales = request.Sales
            .Select(sale => new SaleLine(
                sale.SoldAtUtc.ToLocalTime(),
                sale.GameId,
                sale.Bin,
                sale.Ticket,
                sale.Quantity,
                sale.AmountCents / 100m,
                sale.Source,
                sale.BundleId,
                sale.Id,
                sale.IntervalId,
                sale.ActorId,
                sale.ActorName,
                sale.CorrectsSaleId,
                sale.MigrationState))
            .ToList();
        var closedBundles = request.ClosedBundles.Select(FromStoredImportLine).ToList();
        var currentBundles = request.CurrentBundles.Select(FromStoredImportLine).ToList();
        var resolvedBundles = request.ResolvedBundles.Select(FromStoredImportLine).ToList();
        WriteClosingReports(
            target,
            closing.IntervalStartUtc,
            closing.ClosedAtUtc,
            sales,
            closedBundles,
            currentBundles,
            resolvedBundles,
            closing.SalesCents,
            closing.OnlineSaleCents,
            closing.OnlineCashoutCents,
            closing.InstantCashoutCents,
            closing.ExpectedCashCents,
            closing.ActivatedBundles,
            request.SelectedEmailAttachments,
            closing.ScannedBins,
            closing.ActiveBins,
            closing.IntervalId,
            closing.ClosedByActorId,
            closing.ClosedByActorName);
    }

    private static ImportLine FromStoredImportLine(StoredImportLine line) =>
        new(line.GameId, line.BundleId, line.Ticket, line.Bin, line.Source, line.IsSoldOut);

    private void WriteClosingReports(
        ClosingReportTarget target,
        DateTime intervalStartUtc,
        DateTime closedAtUtc,
        IReadOnlyList<SaleLine> sales,
        IReadOnlyList<ImportLine> closedBundles,
        IReadOnlyList<ImportLine> currentBundles,
        IReadOnlyList<ImportLine> resolvedBundles,
        long instantTicketSalesCents,
        long onlineSaleCents,
        long onlineCashoutCents,
        long instantCashoutCents,
        long expectedCashCents,
        int activatedBundleCount,
        IReadOnlyList<string> selectedEmailAttachments,
        int scannedBinCount,
        int activeBinCount,
        long intervalId,
        string closedByActorId,
        string closedByActorName)
    {
        Directory.CreateDirectory(target.Folder);
        var formula = "instant_ticket_sales + online_sale - instant_cashout - online_cashout";
        var periodStart = intervalStartUtc == DateTime.MinValue
            ? "first_recorded_interval"
            : intervalStartUtc.ToLocalTime().ToString("O", CultureInfo.InvariantCulture);
        var periodEnd = closedAtUtc.ToLocalTime().ToString("O", CultureInfo.InvariantCulture);

        File.WriteAllLines(
            Path.Combine(target.Folder, "shift_summary.csv"),
            new[]
            {
                CsvLine("shift_label", "period_start", "period_end", "instant_ticket_sales", "online_sale", "instant_cashout", "online_cashout", "expected_cash", "expected_cash_formula", "scanned_bins", "active_bins", "closed_bundles", "current_bundles", "resolved_bundles", "activated_bundles", "interval_id", "closed_by_actor_id", "closed_by_actor"),
                CsvLine(target.ShiftLabel, periodStart, periodEnd, MoneyCsv(instantTicketSalesCents), MoneyCsv(onlineSaleCents), MoneyCsv(instantCashoutCents), MoneyCsv(onlineCashoutCents), MoneyCsv(expectedCashCents), formula, scannedBinCount.ToString(CultureInfo.InvariantCulture), activeBinCount.ToString(CultureInfo.InvariantCulture), closedBundles.Count.ToString(CultureInfo.InvariantCulture), currentBundles.Count.ToString(CultureInfo.InvariantCulture), resolvedBundles.Count.ToString(CultureInfo.InvariantCulture), activatedBundleCount.ToString(CultureInfo.InvariantCulture), intervalId.ToString(CultureInfo.InvariantCulture), closedByActorId, closedByActorName)
            },
            Encoding.UTF8);

        WriteSalesCsv(Path.Combine(target.Folder, "sales_detail.csv"), sales);
        WriteSalesCsv(
            Path.Combine(target.Folder, "corrections.csv"),
            sales.Where(s => s.Quantity < 0 || s.Amount < 0 || string.Equals(s.Source, "undo", StringComparison.OrdinalIgnoreCase)).ToList());
        WriteInventoryCsv(Path.Combine(target.Folder, "inventory.csv"), closedBundles, currentBundles, resolvedBundles);
        WritePlacementEventsCsv(Path.Combine(target.Folder, "placement_events.csv"), closedBundles, currentBundles, resolvedBundles);
        WriteBinAssignmentsCsv(Path.Combine(target.Folder, "bin_assignments.csv"), currentBundles, resolvedBundles);
        WriteAnomaliesCsv(Path.Combine(target.Folder, "anomalies.csv"));
        WriteEmailAttachmentsCsv(Path.Combine(target.Folder, "email_attachments.csv"), selectedEmailAttachments);
        File.WriteAllLines(
            Path.Combine(target.Folder, "initialization.csv"),
            new[] { CsvLine("event", "detail"), CsvLine("not_applicable", "No initialization rows are generated for this shift closing.") },
            Encoding.UTF8);
        File.WriteAllLines(
            Path.Combine(target.Folder, "closing_audit.csv"),
            new[]
            {
                CsvLine("event", "detail"),
                CsvLine("closing_finalized", $"shift={target.ShiftLabel}; expected_cash={MoneyCsv(expectedCashCents)}; report_folder={target.Folder}"),
                CsvLine("cash_formula", formula),
                CsvLine("manual_totals", $"online_sale={MoneyCsv(onlineSaleCents)}; online_cashout={MoneyCsv(onlineCashoutCents)}; instant_cashout={MoneyCsv(instantCashoutCents)}"),
                CsvLine("activated_bundles", activatedBundleCount.ToString(CultureInfo.InvariantCulture)),
                CsvLine("ledger_identity", $"interval_id={intervalId.ToString(CultureInfo.InvariantCulture)}; closed_by_actor_id={closedByActorId}; closed_by={closedByActorName}"),
                CsvLine("email_attachments", string.Join(";", selectedEmailAttachments)),
                CsvLine("pdf_report", Path.Combine(target.Folder, "closing_report.pdf"))
            },
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(target.Folder, "closing_report.txt"),
            BuildClosingReportText(target, periodStart, periodEnd, sales, instantTicketSalesCents, onlineSaleCents, onlineCashoutCents, instantCashoutCents, expectedCashCents, closedBundles.Count, currentBundles.Count, resolvedBundles.Count, activatedBundleCount),
            Encoding.UTF8);
        WriteClosingPdfReport(
            Path.Combine(target.Folder, "closing_report.pdf"),
            target,
            periodStart,
            periodEnd,
            sales,
            instantTicketSalesCents,
            onlineSaleCents,
            onlineCashoutCents,
            instantCashoutCents,
            expectedCashCents,
            closedBundles.Count,
            currentBundles.Count,
            resolvedBundles.Count,
            activatedBundleCount);
    }

    private async Task RetryPendingClosingReportsAsync(IReadOnlyList<StoredClosingReportJob> jobs)
    {
        foreach (var job in jobs)
            await ProcessClosingReportJobAsync(job, announceResult: false);
    }

    private async Task ProcessClosingReportJobAsync(StoredClosingReportJob job, bool announceResult)
    {
        try
        {
            await Task.Run(() =>
            {
                DeleteReportFolder(job.Request.Closing.ReportFolder);
                WriteClosingReports(job.Request);
                _store.MarkClosingReportCompleted(job.Id);
            });
            AppLog.Info($"Closing reports generated for {job.Request.Closing.ShiftLabel}.");
            if (!announceResult)
                return;

            StatusText.Text = $"Shift closed. Reports saved to {job.Request.Closing.ReportFolder}.";
            ClosingStatusText.Text = $"{job.Request.Closing.ShiftLabel} closed. Reports are ready.";
            TryRecordAudit(
                "report",
                "Closing reports generated",
                $"{job.Request.Closing.ShiftLabel}; folder {job.Request.Closing.ReportFolder}");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Closing report generation failed for {job.Request.Closing.ShiftLabel}.", ex);
            TryDeleteReportFolder(job.Request.Closing.ReportFolder);
            try
            {
                _store.MarkClosingReportFailed(job.Id, ex.Message);
            }
            catch (Exception persistenceError)
            {
                AppLog.Error($"Closing report failure state could not be persisted for job {job.Id.ToString(CultureInfo.InvariantCulture)}.", persistenceError);
            }

            if (!announceResult)
                return;

            StatusText.Text = $"Shift closed, but reports for {job.Request.Closing.ShiftLabel} are pending retry.";
            ClosingStatusText.Text = $"Report generation failed: {ex.Message}. The shift remains closed and reports will retry after restart.";
            TryRecordAudit(
                "report",
                "Closing reports pending retry",
                $"{job.Request.Closing.ShiftLabel}: {ex.Message}");
        }
    }

    private static void WriteClosingPdfReport(
        string path,
        ClosingReportTarget target,
        string periodStart,
        string periodEnd,
        IReadOnlyList<SaleLine> sales,
        long instantTicketSalesCents,
        long onlineSaleCents,
        long onlineCashoutCents,
        long instantCashoutCents,
        long expectedCashCents,
        int closedBundleCount,
        int currentBundleCount,
        int resolvedBundleCount,
        int activatedBundleCount)
    {
        var pdf = new SimplePdfDocument();
        pdf.AddPage(BuildClosingPdfPage(
            target,
            periodStart,
            periodEnd,
            sales,
            instantTicketSalesCents,
            onlineSaleCents,
            onlineCashoutCents,
            instantCashoutCents,
            expectedCashCents,
            closedBundleCount,
            currentBundleCount,
            resolvedBundleCount,
            activatedBundleCount));
        pdf.Save(path);
    }

    private static string BuildClosingPdfPage(
        ClosingReportTarget target,
        string periodStart,
        string periodEnd,
        IReadOnlyList<SaleLine> sales,
        long instantTicketSalesCents,
        long onlineSaleCents,
        long onlineCashoutCents,
        long instantCashoutCents,
        long expectedCashCents,
        int closedBundleCount,
        int currentBundleCount,
        int resolvedBundleCount,
        int activatedBundleCount)
    {
        var builder = new StringBuilder();
        var totalSalesCents = instantTicketSalesCents + onlineSaleCents;
        var totalCashoutCents = instantCashoutCents + onlineCashoutCents;
        var ticketCount = sales.Sum(s => s.Quantity);
        var denominations = PdfDenominations(sales);

        PdfRect(builder, 0, 744, 612, 48, 0.08, 0.16, 0.27, fill: true);
        PdfText(builder, 42, 766, "Shift Report", "F2", 19, 1, 1, 1);
        PdfText(builder, 42, 750, target.ShiftLabel, "F1", 9, 0.82, 0.9, 1);
        PdfText(builder, 380, 766, periodEnd, "F2", 9, 1, 1, 1);
        PdfText(builder, 380, 750, "SimpleLotto closing report", "F1", 9, 0.82, 0.9, 1);

        PdfText(builder, 42, 724, $"Period: {periodStart} to {periodEnd}", "F1", 9, 0.18, 0.24, 0.32);

        PdfMetricCard(builder, 42, 664, 118, "Instant sales", PdfMoney(instantTicketSalesCents), 0.90, 0.96, 0.90);
        PdfMetricCard(builder, 166, 664, 118, "Online sale", PdfMoney(onlineSaleCents), 0.90, 0.94, 1.0);
        PdfMetricCard(builder, 290, 664, 118, "Total sales", PdfMoney(totalSalesCents), 0.95, 0.94, 1.0);
        PdfMetricCard(builder, 414, 664, 118, "Expected cash", PdfMoney(expectedCashCents), 1.0, 0.95, 0.86);

        PdfSectionTitle(builder, 42, 626, "Sales By Ticket Amount");
        PdfDenominationChart(builder, 42, 452, 256, 158, denominations);

        PdfSectionTitle(builder, 326, 626, "Cash Reconciliation");
        PdfRect(builder, 326, 452, 226, 158, 0.98, 0.99, 1.0, fill: true);
        PdfRect(builder, 326, 452, 226, 158, 0.72, 0.78, 0.86, fill: false);
        PdfSummaryLine(builder, 342, 588, "Instant ticket sales", PdfMoney(instantTicketSalesCents));
        PdfSummaryLine(builder, 342, 566, "Online sale", PdfMoney(onlineSaleCents));
        PdfSummaryLine(builder, 342, 544, "Instant cashout", "-" + PdfMoney(instantCashoutCents));
        PdfSummaryLine(builder, 342, 522, "Online cashout", "-" + PdfMoney(onlineCashoutCents));
        PdfRect(builder, 342, 504, 194, 1, 0.55, 0.62, 0.72, fill: true);
        PdfSummaryLine(builder, 342, 484, "Total cashouts", PdfMoney(totalCashoutCents));
        PdfSummaryLine(builder, 342, 462, "Expected cash", PdfMoney(expectedCashCents), bold: true);

        PdfSectionTitle(builder, 42, 414, "Closing Inventory");
        PdfRect(builder, 42, 242, 256, 156, 0.98, 0.99, 1.0, fill: true);
        PdfRect(builder, 42, 242, 256, 156, 0.72, 0.78, 0.86, fill: false);
        PdfInventoryStat(builder, 58, 374, "Current bundles", currentBundleCount.ToString(CultureInfo.InvariantCulture));
        PdfInventoryStat(builder, 58, 348, "Closed out bundles", closedBundleCount.ToString(CultureInfo.InvariantCulture));
        PdfInventoryStat(builder, 58, 322, "Resolved bundles", resolvedBundleCount.ToString(CultureInfo.InvariantCulture));
        PdfInventoryStat(builder, 58, 296, "Activated bundles", activatedBundleCount.ToString(CultureInfo.InvariantCulture));
        PdfInventoryStat(builder, 58, 270, "Tickets", ticketCount.ToString(CultureInfo.InvariantCulture));
        PdfInventoryStat(builder, 58, 250, "Instant sales", PdfMoney(instantTicketSalesCents));

        PdfSectionTitle(builder, 42, 204, "Largest Sales Rows");
        PdfSalesTable(builder, 42, 64, 510, 124, sales);
        PdfText(builder, 42, 34, "Formula: expected cash = instant ticket sales + online sale - instant cashout - online cashout.", "F1", 7.8, 0.32, 0.38, 0.46);
        return builder.ToString();
    }

    private static IReadOnlyList<PdfDenominationRow> PdfDenominations(IReadOnlyList<SaleLine> sales) =>
        sales
            .Where(s => s.Quantity > 0 && s.Amount > 0)
            .GroupBy(s => Math.Max(1, (int)Math.Round(s.Amount / Math.Max(1, s.Quantity), MidpointRounding.AwayFromZero)))
            .Select(g => new PdfDenominationRow(
                g.Key,
                g.Sum(s => s.Quantity),
                (long)Math.Round(g.Sum(s => s.Amount) * 100m, MidpointRounding.AwayFromZero)))
            .OrderBy(r => r.Price)
            .ToList();

    private static void PdfMetricCard(StringBuilder builder, double x, double y, double width, string label, string value, double r, double g, double b)
    {
        PdfRect(builder, x, y, width, 48, r, g, b, fill: true);
        PdfRect(builder, x, y, width, 48, 0.68, 0.74, 0.82, fill: false);
        PdfText(builder, x + 10, y + 31, label, "F1", 7.8, 0.29, 0.35, 0.43);
        PdfText(builder, x + 10, y + 12, value, "F2", 14, 0.08, 0.16, 0.27);
    }

    private static void PdfSectionTitle(StringBuilder builder, double x, double y, string title)
    {
        PdfText(builder, x, y, title, "F2", 11, 0.08, 0.16, 0.27);
        PdfRect(builder, x, y - 8, 48, 2, 0.16, 0.42, 0.74, fill: true);
    }

    private static void PdfDenominationChart(StringBuilder builder, double x, double y, double width, double height, IReadOnlyList<PdfDenominationRow> rows)
    {
        PdfRect(builder, x, y, width, height, 0.98, 0.99, 1.0, fill: true);
        PdfRect(builder, x, y, width, height, 0.72, 0.78, 0.86, fill: false);
        var expectedPrices = new[] { 1, 2, 5, 10, 20, 25, 30, 40, 50 };
        var byPrice = rows.ToDictionary(r => r.Price);
        var max = Math.Max(1, rows.Select(r => (long?)r.AmountCents).Max() ?? 1);

        PdfText(builder, x + width - 66, y + height - 12, "Sales", "F2", 6.8, 0.28, 0.34, 0.42);
        PdfText(builder, x + width - 24, y + height - 12, "Tickets", "F2", 6.8, 0.28, 0.34, 0.42);
        var rowY = y + height - 24;
        foreach (var price in expectedPrices)
        {
            byPrice.TryGetValue(price, out var row);
            var amount = row?.AmountCents ?? 0;
            var tickets = row?.TicketCount ?? 0;
            PdfText(builder, x + 12, rowY + 2, "$" + price.ToString(CultureInfo.InvariantCulture), "F2", 7.7, 0.20, 0.26, 0.34);
            var barWidth = amount <= 0 ? 1 : Math.Max(4, (double)amount / max * (width - 120));
            PdfRect(builder, x + 42, rowY, barWidth, 9, 0.24, 0.62, 0.44, fill: true);
            PdfText(builder, x + width - 66, rowY + 2, PdfMoney(amount), "F1", 7.2, 0.20, 0.26, 0.34);
            PdfText(builder, x + width - 24, rowY + 2, tickets.ToString(CultureInfo.InvariantCulture), "F1", 7.2, 0.20, 0.26, 0.34);
            rowY -= 14;
        }

        PdfRect(builder, x + 12, y + 17, width - 24, 1, 0.72, 0.78, 0.86, fill: true);
        PdfText(builder, x + 12, y + 6, "Total", "F2", 7.4, 0.08, 0.16, 0.27);
        PdfText(builder, x + width - 66, y + 6, PdfMoney(rows.Sum(r => r.AmountCents)), "F2", 7.2, 0.08, 0.16, 0.27);
        PdfText(builder, x + width - 24, y + 6, rows.Sum(r => r.TicketCount).ToString(CultureInfo.InvariantCulture), "F2", 7.2, 0.08, 0.16, 0.27);
    }

    private static void PdfSummaryLine(StringBuilder builder, double x, double y, string label, string value, bool bold = false)
    {
        PdfText(builder, x, y, label, bold ? "F2" : "F1", 8.4, 0.20, 0.26, 0.34);
        PdfText(builder, x + 132, y, value, bold ? "F2" : "F1", 8.4, 0.08, 0.16, 0.27);
    }

    private static void PdfInventoryStat(StringBuilder builder, double x, double y, string label, string value)
    {
        PdfText(builder, x, y, label, "F1", 8.4, 0.20, 0.26, 0.34);
        PdfText(builder, x + 140, y, value, "F2", 9.2, 0.08, 0.16, 0.27);
    }

    private static void PdfSalesTable(StringBuilder builder, double x, double y, double width, double height, IReadOnlyList<SaleLine> sales)
    {
        PdfRect(builder, x, y, width, height, 0.98, 0.99, 1.0, fill: true);
        PdfRect(builder, x, y, width, height, 0.72, 0.78, 0.86, fill: false);
        var widths = new[] { 112d, 70d, 70d, 54d, 56d, 70d, 78d };
        PdfTableHeader(builder, x, y + height - 18, widths, new[] { "Time", "Game", "Bin", "Ticket", "Qty", "Amount", "Source" });
        var rowY = y + height - 34;
        foreach (var sale in sales
                     .OrderByDescending(s => Math.Abs(s.Amount))
                     .ThenByDescending(s => s.SoldAt)
                     .Take(7))
        {
            PdfRow(builder, x, rowY, 14, widths, new[]
            {
                sale.SoldAt.ToString("MM/dd h:mm tt", CultureInfo.InvariantCulture),
                sale.GameId,
                sale.Bin,
                sale.Ticket,
                sale.Quantity.ToString(CultureInfo.InvariantCulture),
                sale.Amount.ToString("0.00", CultureInfo.InvariantCulture),
                PdfTrim(SaleSourceLabel(sale.Source), 16)
            });
            rowY -= 14;
        }

        if (sales.Count == 0)
            PdfText(builder, x + 12, y + height - 46, "No sales rows for this shift.", "F1", 8, 0.28, 0.34, 0.42);
    }

    private static void PdfTableHeader(StringBuilder builder, double x, double y, IReadOnlyList<double> widths, IReadOnlyList<string> headers)
    {
        PdfRect(builder, x, y, widths.Sum(), 16, 0.82, 0.84, 0.86, fill: true);
        var cx = x;
        for (var i = 0; i < headers.Count; i++)
        {
            PdfRect(builder, cx, y, widths[i], 16, 0.25, 0.25, 0.25, fill: false);
            PdfText(builder, cx + 4, y + 5, headers[i], "F2", 8);
            cx += widths[i];
        }
    }

    private static void PdfRow(StringBuilder builder, double x, double y, double rowHeight, IReadOnlyList<double> widths, IReadOnlyList<string> cells)
    {
        var cx = x;
        for (var i = 0; i < cells.Count; i++)
        {
            PdfRect(builder, cx, y, widths[i], rowHeight, 0.36, 0.36, 0.36, fill: false);
            PdfText(builder, cx + 4, y + 3.4, cells[i], "F1", 7.3);
            cx += widths[i];
        }
    }

    private static void PdfRect(StringBuilder builder, double x, double y, double width, double height, double r, double g, double b, bool fill)
    {
        var colorOperator = fill ? "rg" : "RG";
        builder.AppendFormat(CultureInfo.InvariantCulture, "{0:0.###} {1:0.###} {2:0.###} {3}\n", r, g, b, colorOperator);
        builder.AppendFormat(CultureInfo.InvariantCulture, "{0:0.###} {1:0.###} {2:0.###} {3:0.###} re {4}\n", x, y, width, height, fill ? "f" : "S");
        builder.Append("0 0 0 rg\n0 0 0 RG\n");
    }

    private static void PdfText(StringBuilder builder, double x, double y, string text, string font, double size) =>
        PdfText(builder, x, y, text, font, size, 0, 0, 0);

    private static void PdfText(StringBuilder builder, double x, double y, string text, string font, double size, double r, double g, double b)
    {
        builder.AppendFormat(CultureInfo.InvariantCulture, "{0:0.###} {1:0.###} {2:0.###} rg\n", r, g, b);
        builder.AppendFormat(CultureInfo.InvariantCulture, "BT /{0} {1:0.###} Tf {2:0.###} {3:0.###} Td ({4}) Tj ET\n", font, size, x, y, PdfEscape(text));
        builder.Append("0 0 0 rg\n");
    }

    private static string PdfEscape(string? value)
    {
        value ??= string.Empty;
        var clean = new StringBuilder(value.Length);
        foreach (var ch in value)
            clean.Append(ch is >= ' ' and <= '~' ? ch : '?');

        return clean.ToString()
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);
    }

    private static string PdfTrim(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..Math.Max(0, maxLength - 1)] + "...";

    private static string PdfMoney(long cents) =>
        (cents / 100m).ToString("C", CultureInfo.GetCultureInfo("en-US"));

    private static void WriteSalesCsv(string path, IReadOnlyList<SaleLine> sales)
    {
        var lines = new List<string>
        {
            CsvLine("sale_id", "interval_id", "actor_id", "actor", "corrects_sale_id", "sold_at", "source", "game_id", "bundle_id", "bin", "ticket", "quantity", "amount")
        };
        lines.AddRange(sales.Select(s => CsvLine(
            s.Id.ToString(CultureInfo.InvariantCulture),
            s.IntervalId.ToString(CultureInfo.InvariantCulture),
            s.ActorId,
            s.ActorName,
            s.CorrectsSaleId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            s.SoldAt.ToString("O", CultureInfo.InvariantCulture),
            s.Source,
            s.GameId,
            s.BundleId,
            s.Bin,
            s.Ticket,
            s.Quantity.ToString(CultureInfo.InvariantCulture),
            AmountCsv(s.Amount))));
        File.WriteAllLines(path, lines, Encoding.UTF8);
    }

    private static void WriteInventoryCsv(
        string path,
        IReadOnlyList<ImportLine> closedBundles,
        IReadOnlyList<ImportLine> currentBundles,
        IReadOnlyList<ImportLine> resolvedBundles)
    {
        var lines = new List<string>
        {
            CsvLine("category", "game_id", "bundle_id", "ticket", "bin", "source")
        };
        lines.AddRange(closedBundles.Select(i => ImportCsvLine("closing_gap_fill_sold", i)));
        lines.AddRange(currentBundles.Select(i => ImportCsvLine("current_after_close", i)));
        lines.AddRange(resolvedBundles.Select(i => ImportCsvLine("resolved_during_close", i)));
        File.WriteAllLines(path, lines, Encoding.UTF8);
    }

    private static void WritePlacementEventsCsv(
        string path,
        IReadOnlyList<ImportLine> closedBundles,
        IReadOnlyList<ImportLine> currentBundles,
        IReadOnlyList<ImportLine> resolvedBundles)
    {
        var lines = new List<string>
        {
            CsvLine("event", "game_id", "bundle_id", "ticket", "bin", "source")
        };
        lines.AddRange(closedBundles.Select(i => ImportCsvLine("closed_bundle_removed", i)));
        lines.AddRange(currentBundles.Select(i => ImportCsvLine("current_bundle_kept", i)));
        lines.AddRange(resolvedBundles.Select(i => ImportCsvLine("closing_reconciliation", i)));
        File.WriteAllLines(path, lines, Encoding.UTF8);
    }

    private static void WriteBinAssignmentsCsv(
        string path,
        IReadOnlyList<ImportLine> currentBundles,
        IReadOnlyList<ImportLine> resolvedBundles)
    {
        var lines = new List<string>
        {
            CsvLine("bin", "game_id", "bundle_id", "ticket", "assignment_source")
        };
        lines.AddRange(currentBundles.Select(i => CsvLine(i.Bin, i.GameId, i.BundleId, i.Ticket, i.Source)));
        lines.AddRange(resolvedBundles.Select(i => CsvLine(i.Bin, i.GameId, i.BundleId, i.Ticket, "closing_reconciliation")));
        File.WriteAllLines(path, lines, Encoding.UTF8);
    }

    private static void WriteAnomaliesCsv(string path)
    {
        var lines = new List<string>
        {
            CsvLine("title", "detail")
        };
        File.WriteAllLines(path, lines, Encoding.UTF8);
    }

    private static void WriteEmailAttachmentsCsv(string path, IReadOnlyList<string> selectedEmailAttachments)
    {
        var lines = new List<string>
        {
            CsvLine("file_name", "selected")
        };

        var knownFiles = new[]
        {
            "shift_summary.csv",
            "inventory.csv",
            "sales_detail.csv",
            "corrections.csv",
            "anomalies.csv",
            "placement_events.csv",
            "bin_assignments.csv",
            "initialization.csv",
            "closing_audit.csv",
            "closing_report.pdf"
        };
        var selected = selectedEmailAttachments.ToHashSet(StringComparer.OrdinalIgnoreCase);
        lines.AddRange(knownFiles.Select(fileName => CsvLine(
            fileName,
            selected.Contains(fileName) ? "1" : "0")));
        File.WriteAllLines(path, lines, Encoding.UTF8);
    }

    private static string BuildClosingReportText(
        ClosingReportTarget target,
        string periodStart,
        string periodEnd,
        IReadOnlyList<SaleLine> sales,
        long instantTicketSalesCents,
        long onlineSaleCents,
        long onlineCashoutCents,
        long instantCashoutCents,
        long expectedCashCents,
        int closedBundleCount,
        int currentBundleCount,
        int resolvedBundleCount,
        int activatedBundleCount)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Closing report: {target.ShiftLabel}");
        builder.AppendLine($"Period: {periodStart} - {periodEnd}");
        builder.AppendLine();
        builder.AppendLine($"Instant ticket sales: {MoneyText(instantTicketSalesCents)}");
        builder.AppendLine($"Online sale: {MoneyText(onlineSaleCents)}");
        builder.AppendLine($"Instant cashout: {MoneyText(instantCashoutCents)}");
        builder.AppendLine($"Online cashout: {MoneyText(onlineCashoutCents)}");
        builder.AppendLine($"Expected cash: {MoneyText(expectedCashCents)}");
        builder.AppendLine();
        builder.AppendLine($"Sales rows: {sales.Count.ToString(CultureInfo.CurrentCulture)}");
        builder.AppendLine($"Ticket count: {sales.Sum(s => s.Quantity).ToString(CultureInfo.CurrentCulture)}");
        builder.AppendLine($"Closed bundles: {closedBundleCount.ToString(CultureInfo.CurrentCulture)}");
        builder.AppendLine($"Current bundles: {currentBundleCount.ToString(CultureInfo.CurrentCulture)}");
        builder.AppendLine($"Resolved bundles: {resolvedBundleCount.ToString(CultureInfo.CurrentCulture)}");
        builder.AppendLine($"Activated bundles: {activatedBundleCount.ToString(CultureInfo.CurrentCulture)}");
        return builder.ToString();
    }

    private static string ImportCsvLine(string category, ImportLine line) =>
        CsvLine(category, line.GameId, line.BundleId, line.Ticket, line.Bin, line.Source);

    private static string CsvLine(params string[] cells) =>
        string.Join(",", cells.Select(CsvCell));

    private static string CsvCell(string value)
    {
        var text = value ?? string.Empty;
        return text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0
            ? text
            : $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static string MoneyCsv(long cents) =>
        (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    private static string AmountCsv(decimal amount) =>
        amount.ToString("0.00", CultureInfo.InvariantCulture);

    private static void TryDeleteReportFolder(string folder)
    {
        try
        {
            DeleteReportFolder(folder);
        }
        catch
        {
            // Best-effort cleanup must not hide the report generation failure.
        }
    }

    private static void DeleteReportFolder(string folder)
    {
        var reportsRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SimpleLotto",
            "reports"));
        var fullFolder = Path.GetFullPath(folder);
        var relativeFolder = Path.GetRelativePath(reportsRoot, fullFolder);
        var segments = relativeFolder.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        if (Path.IsPathRooted(relativeFolder) ||
            relativeFolder.Equals("..", StringComparison.Ordinal) ||
            relativeFolder.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relativeFolder.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal) ||
            segments.Length < 2)
        {
            throw new InvalidOperationException("Closing report folder is outside the SimpleLotto reports directory.");
        }

        if (Directory.Exists(fullFolder))
            Directory.Delete(fullFolder, recursive: true);
    }

    private void ReplaceImportLine(ImportLine replacement)
    {
        for (var i = 0; i < _imports.Count; i++)
        {
            var line = _imports[i];
            if (!string.Equals(line.GameId, replacement.GameId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(line.BundleId, replacement.BundleId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(line.Bin, replacement.Bin, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _imports[i] = replacement;
            return;
        }
    }

    private bool TryBuildTicketBackfillSale(
        DateTime soldAt,
        ImportLine activeBundle,
        string scannedTicket,
        string source,
        out TicketBackfillSale backfill,
        out string error)
    {
        backfill = null!;
        error = string.Empty;
        if (!TryGetBundleTicketRange(activeBundle.GameId, out var firstTicket, out var lastTicket, out error))
            return false;

        if (!TryParseTicketSerial(scannedTicket, out var scannedSerial) ||
            scannedSerial < firstTicket ||
            scannedSerial > lastTicket)
        {
            error = $"Ticket {scannedTicket} is outside the configured {FormatTicketSerial(firstTicket, TicketSerialWidth(scannedTicket))}-{FormatTicketSerial(lastTicket, TicketSerialWidth(scannedTicket))} bundle range.";
            return false;
        }

        if (!TryParseTicketSerial(activeBundle.Ticket, out var currentSerial))
        {
            error = $"Current ticket {activeBundle.Ticket} is not a valid ticket serial.";
            return false;
        }

        if (currentSerial < firstTicket || currentSerial > lastTicket)
        {
            error = $"Current ticket {activeBundle.Ticket} is outside the configured bundle range. The bundle was retired from the active bin.";
            return false;
        }

        if (scannedSerial < currentSerial)
        {
            error = $"Ticket {scannedTicket} was already recorded for bundle {activeBundle.BundleId}. Current ticket is {activeBundle.Ticket}.";
            return false;
        }

        var width = Math.Max(TicketSerialWidth(activeBundle.Ticket), TicketSerialWidth(scannedTicket));
        var range = BuildTicketBackfillRange(currentSerial, scannedSerial, width);
        var price = GamePriceCents(activeBundle.GameId) / 100m;
        var amount = range.Quantity * price;
        if (amount <= 0)
        {
            error = $"Game {activeBundle.GameId} calculated a non-positive sale.";
            return false;
        }

        var isComplete = TryParseTicketSerial(range.NextTicket, out var nextSerial) && nextSerial > lastTicket;
        backfill = new TicketBackfillSale(
            new SaleLine(
                soldAt,
                activeBundle.GameId,
                activeBundle.Bin,
                range.SoldTicketText,
                range.Quantity,
                amount,
                source,
                activeBundle.BundleId),
            range.NextTicket,
            isComplete);
        return true;
    }

    private bool TryBuildActivationGapFillSale(
        DateTime soldAt,
        ImportTicket ticket,
        string bin,
        out TicketBackfillSale backfill,
        out string error)
    {
        backfill = null!;
        error = string.Empty;
        var scannedText = ticket.Ticket.Trim();
        var width = TicketSerialWidth(scannedText);
        if (!TryGetBundleTicketRange(ticket.GameId, out var firstTicket, out var lastTicket, out error))
            return false;

        if (!TryParseTicketSerial(scannedText, out var scannedSerial) ||
            scannedSerial < firstTicket ||
            scannedSerial > lastTicket)
        {
            error = $"Ticket {ticket.Ticket} is outside the configured {FormatTicketSerial(firstTicket, width)}-{FormatTicketSerial(lastTicket, width)} bundle range.";
            return false;
        }

        var quantity = scannedSerial - firstTicket + 1;
        var startText = FormatTicketSerial(firstTicket, width);
        var endText = FormatTicketSerial(scannedSerial, width);
        var soldText = quantity == 1 ? endText : $"{startText}-{endText}";
        var isComplete = scannedSerial == lastTicket;
        var price = GamePriceCents(ticket.GameId) / 100m;
        var amount = quantity * price;
        if (amount <= 0)
        {
            error = $"Game {ticket.GameId} calculated a non-positive activation sale.";
            return false;
        }

        backfill = new TicketBackfillSale(
            new SaleLine(
                soldAt,
                ticket.GameId,
                bin,
                soldText,
                quantity,
                amount,
                "activation_gap_fill",
                ticket.BundleId),
            isComplete ? endText : FormatTicketSerial(scannedSerial + 1, width),
            isComplete);
        return true;
    }

    private static TicketBackfillRange BuildTicketBackfillRange(int currentSerial, int scannedSerial, int width)
    {
        var quantity = scannedSerial - currentSerial + 1;
        var startText = FormatTicketSerial(currentSerial, width);
        var endText = FormatTicketSerial(scannedSerial, width);
        var soldText = quantity == 1 ? endText : $"{startText}-{endText}";
        return new TicketBackfillRange(soldText, quantity, FormatTicketSerial(scannedSerial + 1, width));
    }

    private static bool TryParseTicketSerial(string ticket, out int serial)
    {
        serial = 0;
        var value = ticket.Trim();
        return value.Length > 0 &&
            value.All(char.IsAsciiDigit) &&
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out serial);
    }

    private static int TicketSerialWidth(string ticket) =>
        DigitsOnly(ticket).Length;

    private static string FormatTicketSerial(int serial, int width) =>
        serial.ToString("D" + Math.Max(1, width).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static string BundleKey(ImportLine line) =>
        BundleKey(line.GameId, line.BundleId);

    private static string BundleKey(ImportTicket ticket) =>
        BundleKey(ticket.GameId, ticket.BundleId);

    private static string BundleKey(string gameId, string bundleId) =>
        $"{gameId.Trim()}|{bundleId.Trim()}";

    private async void AddGameButton_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireManagerAccess("game setup"))
            return;

        var gameIdBox = new TextBox
        {
            Header = "Game ID",
            PlaceholderText = "Scan or enter game ID"
        };
        var nameBox = new TextBox
        {
            Header = "Game name",
            PlaceholderText = "Display name"
        };
        var priceBox = new NumberBox
        {
            Header = "Game price ($)",
            Minimum = 1,
            SmallChange = 1,
            LargeChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        var content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                gameIdBox,
                nameBox,
                priceBox,
                new TextBlock
                {
                    Text = "Bundle total is automatic: $900 for a $50 ticket; otherwise $500. Ticket numbering uses the global setting.",
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Add game",
            Content = content,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
            return;

        var gameId = DigitsOnly(gameIdBox.Text);
        if (string.IsNullOrWhiteSpace(gameId))
        {
            GameCatalogStatusText.Text = "Enter a valid game ID.";
            return;
        }

        var name = string.IsNullOrWhiteSpace(nameBox.Text)
            ? $"Game {gameId}"
            : nameBox.Text.Trim();
        var priceCents = PriceCentsFromNumberBox(priceBox);
        var bundlePriceCents = AutomaticBundlePriceCents(priceCents);
        if (!TryValidateGameTicketConfiguration(priceCents, bundlePriceCents, out var configurationError))
        {
            GameCatalogStatusText.Text = configurationError;
            return;
        }

        var record = new GameCatalogRecord(
            gameId,
            name,
            priceCents,
            bundlePriceCents,
            "Manual",
            "ms-appx:///Assets/SimpleLottoLogo64.png",
            "Image not uploaded");

        if (!UpsertManualGameRecord(record))
        {
            GameCatalogStatusText.Text = $"Game {gameId} could not be saved.";
            return;
        }

        GameCatalogStatusText.Text = $"Game {gameId} added.";
    }

    private void GameCatalogListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GameCatalogListView.SelectedItem is not GameCatalogRecord game)
        {
            GameIdEditBox.Text = string.Empty;
            GameNameEditBox.Text = string.Empty;
            GamePriceEditBox.Value = 0;
            return;
        }

        GameIdEditBox.Text = game.GameId;
        GameNameEditBox.Text = game.Name;
        GamePriceEditBox.Value = game.PriceCents / 100d;
        GameCatalogStatusText.Text = $"Selected game {game.GameId}.";
    }

    private void SaveGameDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireManagerAccess("game setup"))
            return;

        if (GameCatalogListView.SelectedItem is not GameCatalogRecord game)
        {
            GameCatalogStatusText.Text = "Select a game before saving.";
            return;
        }

        var name = GameNameEditBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            GameCatalogStatusText.Text = "Game name is required.";
            return;
        }

        var priceCents = PriceCentsFromNumberBox(GamePriceEditBox);
        var bundlePriceCents = AutomaticBundlePriceCents(priceCents);
        if (!TryValidateGameTicketConfiguration(priceCents, bundlePriceCents, out var configurationError))
        {
            GameCatalogStatusText.Text = configurationError;
            return;
        }

        var updated = game with
        {
            Name = name,
            PriceCents = priceCents,
            BundlePriceCents = bundlePriceCents,
            Source = "Manual"
        };
        if (!UpsertManualGameRecord(updated))
        {
            GameCatalogStatusText.Text = $"Game {updated.GameId} details could not be saved.";
            return;
        }

        GameCatalogStatusText.Text = $"Game {updated.GameId} details saved.";
    }

    private void SaveGlobalFirstTicketButton_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireManagerAccess("global ticket numbering"))
            return;

        var selectedFirstTicket = GlobalFirstTicketEditComboBox.SelectedIndex == 1 ? 1 : 0;
        try
        {
            _store.SaveGlobalFirstTicketSerial(selectedFirstTicket);
            _globalFirstTicketSerial = selectedFirstTicket;

            foreach (var game in _manualGameCatalog)
                ReopenBundlesExtendedByGameSetup(game);

            RefreshGameCatalog();
            SyncRdisplayTiles();
            GameCatalogStatusText.Text = $"All games now start at {(selectedFirstTicket == 1 ? "001" : "000")}.";
            TryRecordAudit(
                "inventory",
                "Global first ticket saved",
                $"All games start at {(selectedFirstTicket == 1 ? "001" : "000")}");
        }
        catch (Exception ex)
        {
            GlobalFirstTicketEditComboBox.SelectedIndex = _globalFirstTicketSerial;
            GameCatalogStatusText.Text = $"Global first ticket could not be saved: {ex.Message}";
        }
    }

    private async void UploadGameImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireManagerAccess("game image setup"))
            return;

        if (GameCatalogListView.SelectedItem is not GameCatalogRecord game)
        {
            GameCatalogStatusText.Text = "Select a game before uploading an image.";
            return;
        }

        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            FileTypeFilter = { ".jpg", ".jpeg", ".png", ".webp" }
        };
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;

        try
        {
            Directory.CreateDirectory(GameImageCacheDir);
            var dest = GameImageCachePath(game.GameId, Path.GetExtension(file.Path));
            DeleteCachedGameImages(game.GameId);
            File.Copy(file.Path, dest, overwrite: true);
            var updated = game with
            {
                Source = "Manual",
                ImageUri = LocalFileUri(dest),
                ImageStatus = "Image uploaded"
            };
            UpsertManualGameRecord(updated);
            GameCatalogStatusText.Text = $"Image uploaded for game {game.GameId}.";
        }
        catch (Exception ex)
        {
            GameCatalogStatusText.Text = $"Image upload failed: {ex.Message}";
        }
    }

    private async void FetchSelectedGameImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireManagerAccess("game image setup"))
            return;

        if (GameCatalogListView.SelectedItem is not GameCatalogRecord game)
        {
            GameCatalogStatusText.Text = "Select a game before fetching an image.";
            return;
        }

        await FetchAndApplyGameImageAsync(game);
    }

    private async void ViewCachedGameImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireManagerAccess("game image setup"))
            return;

        if (GameCatalogListView.SelectedItem is not GameCatalogRecord game)
        {
            GameCatalogStatusText.Text = "Select a game before viewing its cached image.";
            return;
        }

        var cachedPath = CachedGameImagePath(game.GameId) ?? CachedFileImagePath(game.ImageUri);
        var hasCachedImage = cachedPath is not null;
        var content = new StackPanel
        {
            Spacing = 12,
            MinWidth = 420,
            Children =
            {
                new TextBlock
                {
                    Text = $"Game {game.GameId} | {game.Name}",
                    Foreground = ThemeBrush("SlTextBrush", ColorBrush(21, 23, 26)),
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };

        if (hasCachedImage)
        {
            content.Children.Add(new Border
            {
                Background = ThemeBrush("SlSurfaceAltBrush", ColorBrush(246, 248, 251)),
                BorderBrush = ThemeBrush("SlBorderBrush", ColorBrush(198, 204, 214)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8),
                Child = new Image
                {
                    Source = new BitmapImage(new Uri(LocalFileUri(cachedPath!))),
                    MaxHeight = 360,
                    Stretch = Stretch.Uniform
                }
            });
            content.Children.Add(new TextBlock
            {
                Text = cachedPath!,
                Style = (Style)Application.Current.Resources["SlCaptionTextStyle"],
                TextWrapping = TextWrapping.Wrap
            });
        }
        else
        {
            content.Children.Add(new TextBlock
            {
                Text = "No cached image exists for this game.",
                Style = (Style)Application.Current.Resources["SlCaptionTextStyle"],
                TextWrapping = TextWrapping.Wrap
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Cached Image",
            Content = content,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close
        };

        if (hasCachedImage)
            dialog.SecondaryButtonText = "Remove Image";

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Secondary || !hasCachedImage)
            return;

        try
        {
            DeleteCachedGameImages(game.GameId);
            if (Uri.TryCreate(game.ImageUri, UriKind.Absolute, out var imageUri) &&
                imageUri.IsFile &&
                File.Exists(imageUri.LocalPath))
            {
                File.Delete(imageUri.LocalPath);
            }

            UpsertManualGameRecord(game with
            {
                ImageUri = DefaultGameImageUri,
                ImageStatus = "Image not cached"
            });
            GameCatalogStatusText.Text = $"Cached image removed for game {game.GameId}.";
        }
        catch (Exception ex)
        {
            GameCatalogStatusText.Text = $"Unable to remove cached image: {ex.Message}";
        }
    }

    private async void FetchMissingImagesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireManagerAccess("game image setup"))
            return;

        var missing = _gameCatalog
            .Where(g => !IsCachedGameImage(g))
            .ToList();

        if (missing.Count == 0)
        {
            GameCatalogStatusText.Text = "No missing images to fetch.";
            return;
        }

        var fetched = 0;
        foreach (var game in missing)
        {
            var result = await FetchAndApplyGameImageAsync(game, quiet: true);
            if (result)
                fetched++;
        }

        GameCatalogStatusText.Text = $"Fetched {fetched.ToString(CultureInfo.CurrentCulture)} of {missing.Count.ToString(CultureInfo.CurrentCulture)} missing images.";
    }

    private async Task<bool> FetchAndApplyGameImageAsync(GameCatalogRecord game, bool quiet = false)
    {
        Directory.CreateDirectory(GameImageCacheDir);

        var cached = CachedGameImagePath(game.GameId);
        if (cached is not null)
        {
            UpsertManualGameRecord(game with
            {
                ImageUri = LocalFileUri(cached),
                ImageStatus = "Image cached"
            });
            if (!quiet)
                GameCatalogStatusText.Text = $"Image already cached for game {game.GameId}.";
            return true;
        }

        var urls = OfficialImageUrls(game.GameId, _storeState).ToList();
        if (urls.Count == 0)
        {
            if (!quiet)
                GameCatalogStatusText.Text = $"No official image source configured for state {_storeState}.";
            return false;
        }

        foreach (var url in urls)
        {
            try
            {
                using var response = await ImageHttpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                    continue;

                var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    continue;

                var bytes = await response.Content.ReadAsByteArrayAsync();
                if (bytes.Length < 1024)
                    continue;

                var extension = contentType.Contains("png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
                var path = GameImageCachePath(game.GameId, extension);
                DeleteCachedGameImages(game.GameId);
                await File.WriteAllBytesAsync(path, bytes);

                UpsertManualGameRecord(game with
                {
                    Source = "Official",
                    ImageUri = LocalFileUri(path),
                    ImageStatus = "Image fetched"
                });
                if (!quiet)
                    GameCatalogStatusText.Text = $"Image fetched for game {game.GameId}.";
                return true;
            }
            catch
            {
                // Try the next official candidate; final status is set below.
            }
        }

        if (!quiet)
            GameCatalogStatusText.Text = $"Official image was not found for game {game.GameId}.";
        return false;
    }

    private async Task EnsureGameImageCachedForGameAsync(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId))
            return;

        var game = _gameCatalog.FirstOrDefault(g =>
                       string.Equals(g.GameId, gameId, StringComparison.OrdinalIgnoreCase)) ??
                   _manualGameCatalog.FirstOrDefault(g =>
                       string.Equals(g.GameId, gameId, StringComparison.OrdinalIgnoreCase)) ??
                   GameCatalogRecord.FromImport(gameId);

        try
        {
            await FetchAndApplyGameImageAsync(game, quiet: true);
        }
        catch
        {
            // Image lookup is best-effort and must not block activation/import.
        }
    }

    private static bool IsCachedGameImage(GameCatalogRecord game)
    {
        if (CachedGameImagePath(game.GameId) is not null)
            return true;

        if (!Uri.TryCreate(game.ImageUri, UriKind.Absolute, out var uri) || !uri.IsFile)
            return false;

        return File.Exists(uri.LocalPath);
    }

    private static IEnumerable<string> OfficialImageUrls(string gameId, string state)
    {
        var digits = DigitsOnly(gameId);
        if (string.IsNullOrWhiteSpace(digits))
            yield break;

        var stateCode = state.Trim().ToUpperInvariant();
        var game3 = digits.Length >= 3 ? digits[..3] : digits;
        var game4 = digits.Length >= 4 ? digits[..4] : digits;

        if (stateCode == "GA")
            yield return $"https://www.galottery.com/content/dam/portal/images/scratchers-games/{game4}/thumb-lg.png";
        if (stateCode == "NJ")
            yield return $"https://www.njlottery.com/content/dam/portal/images/instant-games/{game3}.png";
        if (stateCode == "SC")
            yield return $"https://www.sceducationlottery.com/images/games/instantgames/{game3}.jpg";
        if (stateCode == "PA")
            yield return $"https://www.palottery.state.pa.us/Games/Scratch-Offs/~/media/Images/Scratch-Offs/{game3}.png";
    }

    private static string GameImageCacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SimpleLotto",
        "game-images");

    private static string? CachedGameImagePath(string gameId)
    {
        var safe = SafeGameImageKey(gameId);
        var jpg = Path.Combine(GameImageCacheDir, $"{safe}.jpg");
        if (File.Exists(jpg))
            return jpg;

        var png = Path.Combine(GameImageCacheDir, $"{safe}.png");
        return File.Exists(png) ? png : null;
    }

    private static string? CachedFileImagePath(string imageUri)
    {
        if (!Uri.TryCreate(imageUri, UriKind.Absolute, out var uri) ||
            !uri.IsFile ||
            !File.Exists(uri.LocalPath))
        {
            return null;
        }

        return uri.LocalPath;
    }

    private static string GameImageCachePath(string gameId, string extension)
    {
        var safe = SafeGameImageKey(gameId);
        var ext = string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
            ? ".png"
            : ".jpg";
        return Path.Combine(GameImageCacheDir, $"{safe}{ext}");
    }

    private static void DeleteCachedGameImages(string gameId)
    {
        var safe = SafeGameImageKey(gameId);
        foreach (var extension in new[] { ".jpg", ".png" })
        {
            var path = Path.Combine(GameImageCacheDir, $"{safe}{extension}");
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static string SafeGameImageKey(string gameId)
    {
        var chars = gameId
            .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')
            .ToArray();
        return chars.Length == 0 ? "unknown" : new string(chars);
    }

    private static string LocalFileUri(string path) =>
        new Uri(path).AbsoluteUri;

    private static HttpClient CreateImageHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) SimpleLotto/0.0.1");
        return client;
    }

    private void SaveScannerSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _scanPairTimeoutSeconds = Math.Clamp(CoerceInt(ScanPairTimeoutBox.Value, 5), 1, 30);
        _displayBurnInEnabled = DisplayBurnInCheckBox.IsChecked == true;
        _displayBurnInIntervalMinutes = Math.Clamp(CoerceInt(DisplayBurnInIntervalBox.Value, 15), 1, 1440);
        ScanPairTimeoutBox.Value = _scanPairTimeoutSeconds;
        DisplayBurnInIntervalBox.Value = _displayBurnInIntervalMinutes;
        SaveSetting(ScanPairTimeoutSettingKey, _scanPairTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        SaveSetting(DisplayBurnInEnabledSettingKey, BoolSetting(_displayBurnInEnabled));
        SaveSetting(DisplayBurnInIntervalSettingKey, _displayBurnInIntervalMinutes.ToString(CultureInfo.InvariantCulture));
        _rdisplay.ConfigureDisplaySettings(_displayBurnInEnabled, _displayBurnInIntervalMinutes);
        SettingsScannerText.Text = $"Scanner: WindowsPOS HID pairing model; activation scan timeout {_scanPairTimeoutSeconds.ToString(CultureInfo.CurrentCulture)} seconds";
        ScannerPairingStatusText.Text = "Scanner and display settings saved.";
    }

    private async void CheckLicenseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireManagerAccess("license registration"))
            return;

        if (string.IsNullOrWhiteSpace(_storeName) ||
            string.IsNullOrWhiteSpace(_storeState))
        {
            LicenseStatusText.Text = "Store name and state are required before checking license registration.";
            return;
        }

        LicenseStatusText.Text = "Checking license registration...";
        try
        {
            var status = await _license.PhoneHomeAsync(CurrentLicenseStoreInfo());
            ApplyLicenseStatus(status);
            TryRecordAudit("license", "License checked", $"Status {status.Status}");
        }
        catch (Exception ex)
        {
            AppLog.Error("License check failed.", ex);
            LicenseStatusText.Text = $"License check could not complete: {ex.Message}";
        }
    }

    private async void PairScannerButton_Click(object sender, RoutedEventArgs e)
    {
        var picked = await ShowScannerPairDialogAsync();
        if (picked is null)
            return;

        _scannerVid = picked.Vid;
        _scannerPid = picked.Pid;
        _scannerSerial = picked.Serial;
        SaveSetting(ScannerVidSettingKey, _scannerVid);
        SaveSetting(ScannerPidSettingKey, _scannerPid);
        SaveSetting(ScannerSerialSettingKey, _scannerSerial);
        _scannerInput.Configure(_scannerVid, _scannerPid, _scannerSerial);
        RefreshScannerPairingStatus();
        StatusText.Text = $"Scanner paired: {picked.DisplayLabel}.";
    }

    private void UnpairScannerButton_Click(object sender, RoutedEventArgs e)
    {
        _scannerVid = string.Empty;
        _scannerPid = string.Empty;
        _scannerSerial = string.Empty;
        SaveSetting(ScannerVidSettingKey, string.Empty);
        SaveSetting(ScannerPidSettingKey, string.Empty);
        SaveSetting(ScannerSerialSettingKey, string.Empty);
        _scannerInput.Configure(string.Empty, string.Empty, string.Empty);
        RefreshScannerPairingStatus();
        ScannerPairingStatusText.Text = "Scanner unpaired.";
        StatusText.Text = "Scanner unpaired. Focused scan capture remains available.";
    }

    private async Task<HidDeviceInfo?> ShowScannerPairDialogAsync()
    {
        var devices = HidDeviceEnumerator.Enumerate()
            .Select(d => d.Info)
            .OrderBy(d => LooksLikeScanner(d) ? 0 : 1)
            .ThenBy(d => d.DisplayLabel, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(d => d.Vid, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Pid, StringComparer.OrdinalIgnoreCase)
            .ToList();

        HidDeviceInfo? selected = null;
        ContentDialog? dialog = null;
        var deviceButtons = new Dictionary<RadioButton, HidDeviceInfo>();
        var statusText = new TextBlock
        {
            Text = devices.Count == 0
                ? "No HID keyboard-class USB devices were found."
                : "Select the barcode scanner from the detected HID keyboard devices.",
            TextWrapping = TextWrapping.Wrap
        };

        var deviceList = new StackPanel { Spacing = 8 };
        foreach (var device in devices)
        {
            var detail = ScannerDeviceDetail(device);
            var label = new StackPanel { Spacing = 2 };
            label.Children.Add(new TextBlock
            {
                Text = device.DisplayLabel,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            label.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 12,
                Opacity = 0.72,
                TextWrapping = TextWrapping.Wrap
            });

            var radio = new RadioButton
            {
                Content = label,
                GroupName = "SimpleLottoScannerPairDevices",
                Tag = device
            };
            radio.Checked += (_, _) =>
            {
                selected = device;
                if (dialog is not null)
                    dialog.IsPrimaryButtonEnabled = true;
                statusText.Text = $"Selected: {device.DisplayLabel}";
            };
            deviceButtons[radio] = device;
            deviceList.Children.Add(radio);
        }

        if (!string.IsNullOrWhiteSpace(_scannerVid) && !string.IsNullOrWhiteSpace(_scannerPid))
        {
            foreach (var entry in deviceButtons)
            {
                if (!entry.Value.MatchesIdentity(_scannerVid, _scannerPid, _scannerSerial))
                    continue;

                entry.Key.IsChecked = true;
                selected = entry.Value;
                break;
            }
        }

        var panel = new StackPanel
        {
            Spacing = 12,
            MaxWidth = 560,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        panel.Children.Add(statusText);
        panel.Children.Add(new ScrollViewer
        {
            Content = deviceList,
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        panel.Children.Add(new TextBlock
        {
            Text = "If the scanner is not listed, plug it in, close this dialog, and open Pair scanner again.",
            Style = (Style)Application.Current.Resources["SlCaptionTextStyle"],
            TextWrapping = TextWrapping.Wrap
        });

        dialog = new ContentDialog
        {
            Title = "Pair barcode scanner",
            Content = panel,
            PrimaryButtonText = "Pair this device",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = selected is not null,
            XamlRoot = RootGrid.XamlRoot
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? selected : null;
    }

    private static bool LooksLikeScanner(HidDeviceInfo device)
    {
        var label = $"{device.Product} {device.Manufacturer}".ToLowerInvariant();
        return label.Contains("scanner", StringComparison.Ordinal) ||
               label.Contains("barcode", StringComparison.Ordinal) ||
               label.Contains("symbol", StringComparison.Ordinal) ||
               label.Contains("zebra", StringComparison.Ordinal) ||
               label.Contains("honeywell", StringComparison.Ordinal);
    }

    private static string ScannerDeviceDetail(HidDeviceInfo info) =>
        $"VID {info.Vid} / PID {info.Pid}" +
        (string.IsNullOrWhiteSpace(info.Serial) ? " / no serial" : $" / SN {info.Serial}") +
        (string.IsNullOrWhiteSpace(info.Manufacturer) ? string.Empty : $" / {info.Manufacturer}");

    private async void RegisterDisplayButton_Click(object sender, RoutedEventArgs e)
    {
        var host = DisplayHostBox.Text.Trim();
        var port = CoerceInt(DisplayPortBox.Value, 5001);
        DisplayStatusText.Text = "Registering Rdisplay...";
        _rdisplay.ConfigureDisplaySettings(_displayBurnInEnabled, _displayBurnInIntervalMinutes);

        var result = await _rdisplay.RegisterAsync(host, port);
        if (!result.IsSuccess || result.Display is null)
        {
            DisplayStatusText.Text = result.ErrorMessage ?? "Rdisplay registration failed.";
            return;
        }

        DisplayStatusText.Text = $"Registered {result.Display.Name} at {result.Display.BaseUrl}.";
        TryRecordAudit("display", "Rdisplay registered", $"{result.Display.Name} at {result.Display.BaseUrl}");
        RefreshRegisteredDisplayCards();
        SyncRdisplayTiles();
    }

    private async void RefreshSelectedDisplayButton_Click(object sender, RoutedEventArgs e)
    {
        if (RegisteredDisplaysListView.SelectedItem is not RegisteredDisplayCard display)
        {
            DisplayStatusText.Text = "Select a registered display before refreshing.";
            return;
        }

        DisplayStatusText.Text = $"Refreshing {display.Name}...";
        SyncRdisplayTiles();
        var probe = await _rdisplay.ProbeDisplayHardwareAsync(display.Id);
        if (!probe.IsSuccess)
        {
            DisplayStatusText.Text = probe.ErrorMessage ?? "Display hardware probe failed.";
            return;
        }

        var result = await _rdisplay.RefreshDisplayAsync(display.Id);
        RefreshRegisteredDisplayCards();
        DisplayStatusText.Text = result.IsSuccess
            ? $"Refresh sent to {display.Name}."
            : result.ErrorMessage ?? "Display refresh failed.";
        if (result.IsSuccess)
            TryRecordAudit("display", "Rdisplay refreshed", display.Name);
    }

    private async void DeregisterSelectedDisplayButton_Click(object sender, RoutedEventArgs e)
    {
        if (RegisteredDisplaysListView.SelectedItem is not RegisteredDisplayCard display)
        {
            DisplayStatusText.Text = "Select a registered display before deregistering.";
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Deregister Rdisplay?",
            Content = $"Deregister {display.Name} at {display.Endpoint}? The display will need to be registered again before it can show SimpleLotto tiles.",
            PrimaryButtonText = "Deregister",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        var response = await dialog.ShowAsync();
        if (response != ContentDialogResult.Primary)
            return;

        DisplayStatusText.Text = $"Deregistering {display.Name}...";
        var result = await _rdisplay.UnregisterAsync(display.Id);
        DisplayStatusText.Text = result.IsSuccess
            ? $"{display.Name} deregistered."
            : result.ErrorMessage ?? "Deregister failed.";
        if (result.IsSuccess)
            TryRecordAudit("display", "Rdisplay deregistered", display.Name);
        RefreshRegisteredDisplayCards();
    }

    private void BackupNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireManagerAccess("backup settings"))
            return;

        var folder = BackupFolderBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder))
        {
            BackupStatusText.Text = "Enter a backup folder path.";
            return;
        }

        if (!Directory.Exists(folder))
        {
            BackupStatusText.Text = $"Folder does not exist: {folder}";
            return;
        }

        try
        {
            SaveSetting("backup_folder_path", folder);
            var storeName = SanitizePathSegment(string.IsNullOrWhiteSpace(_storeName) ? "SimpleLotto" : _storeName);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            var destDir = Path.Combine(folder, storeName, "backup", stamp);
            Directory.CreateDirectory(destDir);
            var zipPath = Path.Combine(destDir, "backup.zip");
            var pendingZipPath = Path.Combine(destDir, "backup.partial");
            var snapshotPath = Path.Combine(Path.GetTempPath(), $"simplelotto-backup-{Guid.NewGuid():N}.db");
            try
            {
                _store.BackupDatabase(snapshotPath);
                using (var fs = File.Create(pendingZipPath))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    zip.CreateEntryFromFile(snapshotPath, "simplelotto.db", CompressionLevel.Optimal);

                    var entry = zip.CreateEntry("backup-meta.txt", CompressionLevel.Optimal);
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write($"created_at_utc={DateTime.UtcNow:O}{Environment.NewLine}");
                    writer.Write($"store_name={_storeName}{Environment.NewLine}");
                    writer.Write($"database={_store.DatabasePath}{Environment.NewLine}");
                    writer.Write($"backup_method=sqlite_online_backup{Environment.NewLine}");
                }

                File.Move(pendingZipPath, zipPath);
            }
            finally
            {
                try
                {
                    if (File.Exists(snapshotPath))
                        File.Delete(snapshotPath);
                    if (File.Exists(pendingZipPath))
                        File.Delete(pendingZipPath);
                }
                catch (Exception cleanupError)
                {
                    AppLog.Error("Temporary SQLite backup files could not be deleted.", cleanupError);
                }
            }

            var size = new FileInfo(zipPath).Length;
            BackupStatusText.Text = $"Backup complete: {zipPath} ({size / 1024:N0} KB)";
        }
        catch (Exception ex)
        {
            BackupStatusText.Text = $"Backup failed: {ex.Message}";
        }
    }

    private void SaveEmailSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireManagerAccess("email settings"))
            return;

        SaveSetting("smtp_host", SmtpHostBox.Text.Trim());
        SaveSetting("smtp_port", CoerceInt(SmtpPortBox.Value, 587).ToString(CultureInfo.InvariantCulture));
        SaveSetting("smtp_user", SmtpUserBox.Text.Trim());
        if (!string.IsNullOrWhiteSpace(SmtpPasswordBox.Password))
            SaveSecretSetting("smtp_password", SmtpPasswordBox.Password);
        SaveSetting("email_to", EmailToBox.Text.Trim());
        SaveClosingEmailSettingsFromSettings();
        SmtpPasswordBox.Password = string.Empty;
        EmailSettingsStatusText.Text = $"Email settings saved. Application password is stored encrypted. {SettingsEmailChoicesStatusText.Text}";
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
            builder.Append(invalid.Contains(c) ? '_' : c);
        return builder.ToString().Trim();
    }

    private string BuildGameMixText()
    {
        if (_sales.Count == 0)
            return "No sales recorded.";

        return string.Join(Environment.NewLine,
            _sales
                .GroupBy(s => s.GameId)
                .OrderByDescending(g => g.Sum(s => s.Amount))
                .ThenBy(g => g.Key)
                .Select(g =>
                    $"{g.Key}: {g.Sum(s => s.Quantity)} ticket(s), {g.Sum(s => s.Amount).ToString("C", CultureInfo.CurrentCulture)}"));
    }

    private static int CoerceInt(double value, int fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return fallback;

        return Math.Max(1, (int)Math.Round(value, MidpointRounding.AwayFromZero));
    }

    private static long PriceCentsFromNumberBox(NumberBox box)
    {
        if (double.IsNaN(box.Value) || double.IsInfinity(box.Value))
            return 0;

        var dollars = Math.Max(0, (int)Math.Round(box.Value, MidpointRounding.AwayFromZero));
        return dollars * 100L;
    }

    private static bool TryReadMoneyCents(NumberBox box, out long cents)
    {
        cents = 0;
        if (double.IsNaN(box.Value) || double.IsInfinity(box.Value) || box.Value < 0)
            return false;

        cents = (long)Math.Round((decimal)box.Value * 100m, MidpointRounding.AwayFromZero);
        return cents >= 0;
    }

    private static bool TryReadMoneyCentsOrZero(NumberBox? box, out long cents)
    {
        cents = 0;
        return box is not null && TryReadMoneyCents(box, out cents);
    }

    private static string MoneyText(long cents) =>
        (cents / 100m).ToString("C", CultureInfo.CurrentCulture);

    private static decimal CoerceMoney(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 0;

        return Math.Max(0, Math.Round((decimal)value, 2, MidpointRounding.AwayFromZero));
    }

    private static SolidColorBrush ColorBrush(byte r, byte g, byte b) =>
        new(Color.FromArgb(255, r, g, b));

    private static Brush ThemeBrush(string key, SolidColorBrush fallback) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : fallback;

    private static Brush EmptyTileBrush => ThemeBrush("SlBinEmptyBrush", ColorBrush(236, 236, 238));
    private static Brush EmptyTileBorderBrush => ThemeBrush("SlBorderBrush", ColorBrush(190, 196, 205));
    private static Brush LowTileBrush => ThemeBrush("SlBinLowBrush", ColorBrush(191, 219, 254));
    private static Brush LowTileStackedBrush => ThemeBrush("SlBinLowStackedBrush", ColorBrush(96, 165, 250));
    private static Brush MediumTileBrush => ThemeBrush("SlBinMediumBrush", ColorBrush(187, 247, 208));
    private static Brush MediumTileStackedBrush => ThemeBrush("SlBinMediumStackedBrush", ColorBrush(74, 222, 128));
    private static Brush HighTileBrush => ThemeBrush("SlBinHighBrush", ColorBrush(253, 186, 116));
    private static Brush HighTileStackedBrush => ThemeBrush("SlBinHighStackedBrush", ColorBrush(251, 146, 60));
    private static Brush BhagvaTileBrush => HighTileBrush;
    private static Brush BhagvaBorderBrush => HighTileStackedBrush;
    private static SolidColorBrush DarkTileTextBrush => ColorBrush(21, 23, 26);
    private static BinActivity ActivityForGameSales(int gameSales, int mediumThreshold, int highThreshold)
    {
        if (gameSales >= highThreshold)
            return BinActivity.High;
        if (gameSales >= mediumThreshold)
            return BinActivity.Medium;
        return BinActivity.Low;
    }

    private string GameDisplayName(string gameId)
    {
        var manual = _manualGameCatalog.FirstOrDefault(g =>
            string.Equals(g.GameId, gameId, StringComparison.OrdinalIgnoreCase));

        return manual is null || string.IsNullOrWhiteSpace(manual.Name)
            ? $"Game {gameId}"
            : manual.Name.Trim();
    }

    private GameCatalogRecord? FindKnownGame(string gameId) =>
        _manualGameCatalog.FirstOrDefault(g =>
            string.Equals(g.GameId, gameId, StringComparison.OrdinalIgnoreCase)) ??
        _gameCatalog.FirstOrDefault(g =>
            string.Equals(g.GameId, gameId, StringComparison.OrdinalIgnoreCase));

    private long GamePriceCents(string gameId)
    {
        var game = FindKnownGame(gameId);
        return game?.PriceCents ?? 0;
    }

    private static long AutomaticBundlePriceCents(long ticketPriceCents) =>
        ticketPriceCents <= 0
            ? 0
            : ticketPriceCents == 5_000
                ? FiftyDollarBundlePriceCents
                : StandardBundlePriceCents;

    private bool HasCompleteGameSetup(string gameId)
    {
        var game = FindKnownGame(gameId);
        return game is not null &&
            TryValidateGameTicketConfiguration(
                game.PriceCents,
                AutomaticBundlePriceCents(game.PriceCents),
                out _);
    }

    private static bool TryValidateGameTicketConfiguration(long ticketPriceCents, long bundlePriceCents, out string error)
    {
        error = string.Empty;
        if (ticketPriceCents <= 0)
        {
            error = "Enter a positive ticket price.";
            return false;
        }

        if (bundlePriceCents <= 0)
        {
            error = "The automatic bundle total could not be calculated.";
            return false;
        }

        if (bundlePriceCents % ticketPriceCents != 0)
        {
            error = "The automatic bundle total must divide evenly by the ticket price.";
            return false;
        }

        var ticketCount = bundlePriceCents / ticketPriceCents;
        if (ticketCount > int.MaxValue)
        {
            error = "Bundle ticket count is too large.";
            return false;
        }

        return true;
    }

    private bool TryGetBundleTicketRange(string gameId, out int firstTicket, out int lastTicket, out string error)
    {
        firstTicket = 0;
        lastTicket = 0;
        error = string.Empty;
        var game = FindKnownGame(gameId);
        var bundlePriceCents = AutomaticBundlePriceCents(game?.PriceCents ?? 0);
        if (game is null || !TryValidateGameTicketConfiguration(game.PriceCents, bundlePriceCents, out error))
        {
            error = string.IsNullOrWhiteSpace(error)
                ? $"Game {gameId} needs ticket price setup."
                : $"Game {gameId}: {error}";
            return false;
        }

        var ticketCount = bundlePriceCents / game.PriceCents;
        if (ticketCount > int.MaxValue)
        {
            error = $"Game {gameId} bundle ticket count is too large.";
            return false;
        }

        firstTicket = _globalFirstTicketSerial;
        lastTicket = checked(firstTicket + (int)ticketCount - 1);
        return true;
    }

    private void ReopenBundlesExtendedByGameSetup(GameCatalogRecord game)
    {
        if (!TryGetBundleTicketRange(game.GameId, out _, out var lastTicket, out _))
            return;

        foreach (var bundle in _imports.Where(bundle =>
                     bundle.IsSoldOut &&
                     string.Equals(bundle.GameId, game.GameId, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (!TryParseTicketSerial(bundle.Ticket, out var displayedTicket) || displayedTicket >= lastTicket)
                continue;

            try
            {
                var highestClaimedTicket = _store.GetHighestClaimedTicketSerial(bundle.GameId, bundle.BundleId);
                var nextTicket = Math.Max(displayedTicket, highestClaimedTicket ?? displayedTicket) + 1;
                if (nextTicket > lastTicket)
                    continue;

                var reopened = bundle with
                {
                    Ticket = FormatTicketSerial(nextTicket, TicketSerialWidth(bundle.Ticket)),
                    IsSoldOut = false
                };
                _store.UpdateImportState(new StoredImportLine(
                    reopened.GameId,
                    reopened.BundleId,
                    reopened.Ticket,
                    reopened.Bin,
                    reopened.Source,
                    false));
                ReplaceImportLine(reopened);
                TryRecordAudit(
                    "inventory",
                    "Bundle reopened after game setup",
                    $"Game {bundle.GameId}, bundle {bundle.BundleId}, bin {bundle.Bin}, previous sold-out ticket {bundle.Ticket}, corrected next ticket {reopened.Ticket}, configured last ticket {FormatTicketSerial(lastTicket, TicketSerialWidth(bundle.Ticket))}");
            }
            catch (Exception ex)
            {
                AppLog.Error($"Unable to reopen bundle {bundle.GameId}/{bundle.BundleId} after game setup changed.", ex);
                StatusText.Text = $"Game setup saved, but bundle {bundle.BundleId} could not be reopened: {ex.Message}";
            }
        }
    }

    private static int PriceCentsForDisplay(long priceCents) =>
        priceCents <= 0
            ? 0
            : priceCents > int.MaxValue
                ? int.MaxValue
                : (int)priceCents;

    private int TicketsRemainingForDisplay(ImportLine bundle, GameCatalogRecord? game)
    {
        if (bundle.IsSoldOut || game is null ||
            !TryGetBundleTicketRange(bundle.GameId, out _, out var lastTicket, out _) ||
            !TryParseTicketSerial(bundle.Ticket, out var currentTicket))
        {
            return 0;
        }

        return Math.Max(0, lastTicket - currentTicket + 1);
    }

    private static string PlacementSourceLabel(string source) =>
        source switch
        {
            "activation" => "Activation",
            "initial_import" => "Opening import",
            _ => "Placement"
        };

    private string GameNameForDetail(string gameId)
    {
        var manual = _manualGameCatalog.FirstOrDefault(g =>
            string.Equals(g.GameId, gameId, StringComparison.OrdinalIgnoreCase));

        return manual is null || string.IsNullOrWhiteSpace(manual.Name)
            ? "Name not set"
            : manual.Name.Trim();
    }

    private void ResizeWindow(int widthDip, int heightDip)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new SizeInt32(
            (int)(widthDip * scale),
            (int)(heightDip * scale)));
    }

    private sealed record BinCard(
        int Number,
        int BundleCount,
        string GameName,
        BinActivity Activity,
        bool IsSoldOut)
    {
        public string BinText => Number.ToString(CultureInfo.InvariantCulture);
        public string GameTextShort => BundleCount == 0 ? string.Empty : CompactGameName(GameName);
        public Visibility GameTextVisibility => BundleCount == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        public Brush BackgroundBrush => BundleCount == 0 || IsSoldOut
            ? EmptyTileBrush
            : Activity switch
            {
                BinActivity.High => BundleCount > 1 ? HighTileStackedBrush : HighTileBrush,
                BinActivity.Medium => BundleCount > 1 ? MediumTileStackedBrush : MediumTileBrush,
                _ => BundleCount > 1 ? LowTileStackedBrush : LowTileBrush
            };
        public Brush BorderBrush => BundleCount == 0 || IsSoldOut
            ? EmptyTileBorderBrush
            : Activity switch
            {
                BinActivity.High => HighTileStackedBrush,
                BinActivity.Medium => MediumTileStackedBrush,
                _ => LowTileStackedBrush
            };
        public Brush ForegroundBrush => DarkTileTextBrush;

        public static BinCard From(
            int number,
            ImportLine? current,
            int bundleCount,
            string gameName,
            BinActivity activity) =>
            current is null
                ? new BinCard(number, 0, string.Empty, BinActivity.Low, false)
                : new BinCard(number, bundleCount, gameName, activity, current.IsSoldOut);

        private static string CompactGameName(string gameName)
        {
            var text = string.IsNullOrWhiteSpace(gameName) ? string.Empty : gameName.Trim();
            const int maxLength = 8;
            return text.Length <= maxLength ? text : text[..maxLength];
        }
    }

    private enum BinActivity
    {
        Low,
        Medium,
        High
    }

    private sealed record BundleDetailLine(
        string GameId,
        string GameName,
        string BundleId,
        string Ticket,
        string Bin,
        bool IsCurrent,
        bool IsSoldOut)
    {
        public string SummaryText => $"{(IsSoldOut ? "Sold out" : IsCurrent ? "Current" : "Dormant")} | Game {GameId} | Bundle {BundleId}";
        public string DetailText => IsSoldOut ? $"Bin {Bin} | Sold out at ticket {Ticket}" : $"Bin {Bin} | Current ticket {Ticket}";
        public string StatusText => IsSoldOut ? "Sold out" : IsCurrent ? "Current" : "Dormant";
        public string GameIdText => $"Game ID {GameId}";
        public string GameNameText => $"Game Name {GameName}";
        public string BundleText => $"Bundle ID {BundleId}";
        public string TicketText => $"Ticket Number {Ticket}";
        public string BinText => $"Bin {Bin}";
        public Brush CardBackgroundBrush => IsSoldOut
            ? EmptyTileBrush
            : IsCurrent
            ? MediumTileBrush
            : ThemeBrush("SlSurfaceAltBrush", ColorBrush(247, 248, 250));
        public Brush CardBorderBrush => IsSoldOut
            ? EmptyTileBorderBrush
            : IsCurrent
            ? MediumTileStackedBrush
            : ThemeBrush("SlBorderBrush", ColorBrush(198, 204, 214));
        public Brush CardForegroundBrush => IsCurrent && !IsSoldOut
            ? DarkTileTextBrush
            : ThemeBrush("SlTextBrush", ColorBrush(21, 23, 26));

        public static BundleDetailLine From(ImportLine line, string gameName, bool isCurrent) =>
            new(line.GameId, gameName, line.BundleId, line.Ticket, line.Bin, isCurrent, line.IsSoldOut);
    }

    private sealed record InventoryRecord(
        string Source,
        string GameId,
        string BundleId,
        string Ticket,
        string Bin,
        string Status)
    {
        public string GameText => $"Game {GameId}";
        public string BundleText => $"Bundle {BundleId}";
    }

    private sealed record ReceivedBundleLine(
        string GameId,
        string BundleId,
        DateTime ReceivedAt,
        string Source);

    private sealed record ReceivingScanRow(string GameId, string BundleId)
    {
        public string ScannedText => $"Game {GameId}  |  Bundle {BundleId}";
    }

    private sealed record ActivationLine(
        DateTime ActivatedAt,
        string GameId,
        string BundleId,
        string Bin,
        string Source,
        long IntervalId = 0,
        string ActorId = "",
        string ActorName = "");

    private sealed record GameCatalogRecord(
        string GameId,
        string Name,
        long PriceCents,
        long BundlePriceCents,
        string Source,
        string ImageUri,
        string ImageStatus)
    {
        public BitmapImage ImageSource => new(new Uri(ImageUri));
        public string PriceText => PriceCents <= 0
            ? "Not set"
            : (PriceCents / 100m).ToString("C0", CultureInfo.CurrentCulture);

        public static GameCatalogRecord FromImport(string gameId) =>
            new(
                gameId,
                "Name not set",
                0,
                0,
                "Initial import",
                "ms-appx:///Assets/SimpleLottoLogo64.png",
                "Image not cached");
    }

    private sealed record ClosingBinCard(
        int Number,
        string Status,
        string Detail,
        bool Scanned,
        bool HasBundle,
        bool IsSoldOut)
    {
        public string BinText => Number.ToString(CultureInfo.InvariantCulture);
        public Brush BackgroundBrush => IsSoldOut
            ? EmptyTileBrush
            : Scanned
            ? MediumTileBrush
            : EmptyTileBrush;
        public Brush BorderBrush => IsSoldOut
            ? EmptyTileBorderBrush
            : Scanned
            ? MediumTileStackedBrush
            : EmptyTileBorderBrush;
        public Brush ForegroundBrush => DarkTileTextBrush;

        public static ClosingBinCard From(int number, ImportLine? current, bool scanned)
        {
            var detail = current is null
                ? $"Bin {number.ToString(CultureInfo.CurrentCulture)} is empty."
                : $"Bin {number.ToString(CultureInfo.CurrentCulture)}{Environment.NewLine}Expected game ID: {current.GameId}{Environment.NewLine}Bundle ID: {current.BundleId}{Environment.NewLine}{(current.IsSoldOut ? "Sold out at ticket" : "Current ticket")}: {current.Ticket}{Environment.NewLine}Scan status: {(current.IsSoldOut ? "Sold out" : scanned ? "Scanned" : "Needs scan")}";
            var status = scanned
                ? "Scanned"
                : current is null
                    ? "Empty"
                    : current.IsSoldOut
                        ? "Sold out"
                    : "Need scan";
            return new ClosingBinCard(number, status, detail, scanned, current is not null, current?.IsSoldOut == true);
        }
    }

    private sealed record ClosingScanRow(string ScannedText, string Status)
    {
        public string Raw { get; init; } = ScannedText;

        public string DisplayText => $"{ScannedText} — {Status}";

        public bool CanDiscard => !string.Equals(Status, "Scanned", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ClosingScanIssue(string Raw, string Title, string Detail);

    private sealed record ClosingScanSale(string BundleKey, SaleLine Sale);

    private sealed record TicketBackfillSale(SaleLine Sale, string NextTicket, bool IsBundleComplete);

    private sealed record TicketBackfillRange(string SoldTicketText, int Quantity, string NextTicket);

    private sealed record ClosingReportTarget(string BusinessDate, int ShiftSequence, string ShiftLabel, string Folder);

    private static List<ClosingHistoryRow> BuildClosingHistoryRows(IEnumerable<StoredClosingRecord> records) =>
        records
            .OrderByDescending(record => record.IntervalId)
            .ThenByDescending(record => record.ClosedAtUtc)
            .Select(ClosingHistoryRow.From)
            .ToList();

    private sealed record ClosingHistoryRow(
        DateTime ClosedAt,
        DateTime IntervalStart,
        string BusinessDate,
        int ShiftSequence,
        string ShiftLabel,
        string ReportFolder,
        int ScannedBins,
        int ActiveBins,
        int SalesCount,
        int TicketCount,
        decimal SalesAmount,
        decimal OnlineSaleAmount,
        decimal OnlineCashoutAmount,
        decimal InstantCashoutAmount,
        decimal ExpectedCashAmount,
        int ClosedBundles,
        int CurrentBundles,
        int ResolvedBundles,
        int ActivatedBundles)
    {
        public string ShiftText => string.IsNullOrWhiteSpace(ShiftLabel)
            ? $"{BusinessDate} #{ShiftSequence.ToString(CultureInfo.CurrentCulture)}"
            : ShiftLabel;
        public string ClosedText => ClosedAt.ToString("g", CultureInfo.CurrentCulture);
        public string ClosedDateText => string.IsNullOrWhiteSpace(BusinessDate)
            ? ClosedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : BusinessDate;
        public string LocalClosedDateText => ClosedAt.ToString("d", CultureInfo.CurrentCulture);
        public string IntervalStartText => IntervalStart == DateTime.MinValue
            ? "First recorded interval"
            : IntervalStart.ToString("g", CultureInfo.CurrentCulture);
        public string SalesText => SalesAmount.ToString("C", CultureInfo.CurrentCulture);
        public string ExpectedCashText => ExpectedCashAmount.ToString("C", CultureInfo.CurrentCulture);
        public string OnlineSaleText => OnlineSaleAmount.ToString("C", CultureInfo.CurrentCulture);
        public string OnlineCashoutText => OnlineCashoutAmount.ToString("C", CultureInfo.CurrentCulture);
        public string InstantCashoutText => InstantCashoutAmount.ToString("C", CultureInfo.CurrentCulture);
        public string ManualTotalsText =>
            $"online sale {OnlineSaleAmount.ToString("C", CultureInfo.CurrentCulture)}, " +
            $"online cashout {OnlineCashoutAmount.ToString("C", CultureInfo.CurrentCulture)}, " +
            $"instant cashout {InstantCashoutAmount.ToString("C", CultureInfo.CurrentCulture)}";
        public string TicketText => TicketCount.ToString(CultureInfo.CurrentCulture);
        public string BinText => $"{ScannedBins.ToString(CultureInfo.CurrentCulture)} / {ActiveBins.ToString(CultureInfo.CurrentCulture)}";
        public string ReconciliationText =>
            $"{ClosedBundles.ToString(CultureInfo.CurrentCulture)} closed, {CurrentBundles.ToString(CultureInfo.CurrentCulture)} updated, {ResolvedBundles.ToString(CultureInfo.CurrentCulture)} resolved, {ActivatedBundles.ToString(CultureInfo.CurrentCulture)} activated";
        public bool HasReportFolder => !string.IsNullOrWhiteSpace(ReportFolder) && Directory.Exists(ReportFolder);
        public string ReportFolderText => string.IsNullOrWhiteSpace(ReportFolder)
            ? "No report folder recorded."
            : ReportFolder;
        public bool MatchesSearch(string search)
        {
            if (string.IsNullOrWhiteSpace(search))
                return true;

            var trimmed = search.Trim();
            return ClosedDateText.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                LocalClosedDateText.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                ShiftDateText().Contains(trimmed, StringComparison.OrdinalIgnoreCase);
        }

        public static ClosingHistoryRow From(StoredClosingRecord record) =>
            new(
                record.ClosedAtUtc.ToLocalTime(),
                record.IntervalStartUtc == DateTime.MinValue
                    ? DateTime.MinValue
                    : record.IntervalStartUtc.ToLocalTime(),
                record.BusinessDate,
                record.ShiftSequence,
                record.ShiftLabel,
                record.ReportFolder,
                record.ScannedBins,
                record.ActiveBins,
                record.SalesCount,
                record.TicketCount,
                record.SalesCents / 100m,
                record.OnlineSaleCents / 100m,
                record.OnlineCashoutCents / 100m,
                record.InstantCashoutCents / 100m,
                record.ExpectedCashCents / 100m,
                record.ClosedBundles,
                record.CurrentBundles,
                record.ResolvedBundles,
                record.ActivatedBundles);

        private string ShiftDateText()
        {
            var marker = ShiftText.IndexOf('#', StringComparison.Ordinal);
            return marker < 0
                ? ShiftText
                : ShiftText[..marker].TrimEnd();
        }
    }

    private sealed record PdfDenominationRow(int Price, int TicketCount, long AmountCents);

    private sealed class SimplePdfDocument
    {
        private readonly List<string> _pages = new();

        public void AddPage(string content) => _pages.Add(content);

        public void Save(string path)
        {
            var totalObjects = 4 + _pages.Count * 2;
            var objects = new string[totalObjects + 1];
            objects[1] = "<< /Type /Catalog /Pages 2 0 R >>";
            var kids = string.Join(" ", Enumerable.Range(0, _pages.Count).Select(i => $"{5 + i * 2} 0 R"));
            objects[2] = $"<< /Type /Pages /Kids [{kids}] /Count {_pages.Count} >>";
            objects[3] = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";
            objects[4] = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>";

            for (var i = 0; i < _pages.Count; i++)
            {
                var pageId = 5 + i * 2;
                var contentId = pageId + 1;
                objects[pageId] =
                    "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                    $"/Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentId} 0 R >>";
                var bytes = Encoding.ASCII.GetBytes(_pages[i]);
                objects[contentId] = $"<< /Length {bytes.Length} >>\nstream\n{_pages[i]}\nendstream";
            }

            using var stream = File.Create(path);
            WriteAscii(stream, "%PDF-1.4\n");
            var offsets = new long[totalObjects + 1];
            for (var i = 1; i <= totalObjects; i++)
            {
                offsets[i] = stream.Position;
                WriteAscii(stream, $"{i} 0 obj\n{objects[i]}\nendobj\n");
            }

            var xrefAt = stream.Position;
            WriteAscii(stream, $"xref\n0 {totalObjects + 1}\n");
            WriteAscii(stream, "0000000000 65535 f \n");
            for (var i = 1; i <= totalObjects; i++)
                WriteAscii(stream, $"{offsets[i]:0000000000} 00000 n \n");
            WriteAscii(
                stream,
                $"trailer\n<< /Size {totalObjects + 1} /Root 1 0 R >>\nstartxref\n{xrefAt}\n%%EOF\n");
        }

        private static void WriteAscii(Stream stream, string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }
    }

    private sealed record AuditLogRow(
        DateTime OccurredAt,
        string Category,
        string Action,
        string Actor,
        string Detail)
    {
        public string TimeText => OccurredAt.ToString("g", CultureInfo.CurrentCulture);

        public static AuditLogRow From(StoredAuditRecord record) =>
            new(
                record.OccurredAtUtc.ToLocalTime(),
                record.Category,
                record.Action,
                record.Actor,
                record.Detail);
    }

    private sealed record RegisteredDisplayCard(
        long Id,
        string Name,
        string Endpoint,
        string ServerUrlText,
        string Status,
        string ScreenText,
        string LastSeenText,
        string RegisteredText)
    {
        public static RegisteredDisplayCard From(RdisplayRegistration display)
        {
            var screenText = display.ActiveScreenCount <= 0
                ? "No active screens"
                : $"{display.ActiveScreenCount.ToString(CultureInfo.CurrentCulture)} active screen{(display.ActiveScreenCount == 1 ? string.Empty : "s")}";
            var lastSeen = display.LastSeenAt is null
                ? "Never seen"
                : $"Seen {display.LastSeenAt.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}";
            var registered = display.LastRegisteredAt is null
                ? "Registration pending"
                : $"Registered {display.LastRegisteredAt.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}";
            var status = display.IsActive
                ? "Active"
                : "Inactive";

            return new RegisteredDisplayCard(
                display.Id,
                display.Name,
                display.BaseUrl,
                string.IsNullOrWhiteSpace(display.LastServerUrl)
                    ? "Server URL not sent yet"
                    : $"Server URL {display.LastServerUrl}",
                status,
                screenText,
                lastSeen,
                registered);
        }
    }

    private sealed record SaleLine(
        DateTime SoldAt,
        string GameId,
        string Bin,
        string Ticket,
        int Quantity,
        decimal Amount,
        string Source = "normal_sale",
        string BundleId = "",
        long Id = 0,
        long IntervalId = 0,
        string ActorId = "",
        string ActorName = "",
        long? CorrectsSaleId = null,
        string MigrationState = "native")
    {
        public string TimeText => SoldAt.ToString("h:mm tt", CultureInfo.CurrentCulture);
        public string AmountText => Amount.ToString("C", CultureInfo.CurrentCulture);
        public string GameText => $"Game {GameId}";
        public string DetailText => $"Bin {Bin} | Ticket {Ticket}";
    }

    private sealed record ImportLine(
        string GameId,
        string BundleId,
        string Ticket,
        string Bin,
        string Source,
        bool IsSoldOut = false)
    {
        public string SummaryText => IsSoldOut
            ? $"Game {GameId} | Bundle {BundleId} | Sold out at {Ticket} | Bin {Bin}"
            : $"Game {GameId} | Bundle {BundleId} | Ticket {Ticket} | Bin {Bin}";
    }

    private sealed class ImportBin : INotifyPropertyChanged
    {
        private int _importedCount;

        public ImportBin(int number)
        {
            Number = number;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public int Number { get; }
        public string Label => Number.ToString(CultureInfo.InvariantCulture);
        public string StatusText => ImportedCount == 0 ? "Open" : ImportedCount.ToString(CultureInfo.InvariantCulture);
        public Brush StatusBackgroundBrush => ImportedCount == 0
            ? EmptyTileBrush
            : BhagvaTileBrush;
        public Brush StatusBorderBrush => ImportedCount == 0
            ? EmptyTileBorderBrush
            : BhagvaBorderBrush;
        public Brush StatusTextBrush => DarkTileTextBrush;

        public int ImportedCount
        {
            get => _importedCount;
            set
            {
                if (_importedCount == value)
                    return;

                _importedCount = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ImportedCount)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBackgroundBrush)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBorderBrush)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusTextBrush)));
            }
        }
    }

    private sealed record ImportTicket(
        string GameId,
        string BundleId,
        string Ticket,
        string Raw);

    private sealed record StateOption(
        string Code,
        string Name,
        string? DefaultLayout)
    {
        public override string ToString() => $"{Code} - {Name}";
    }

    private sealed record BarcodeLayout(
        string Name,
        int Game,
        int Pack,
        int Ticket,
        int? Validation)
    {
        public int PackTicketLength => Game + Pack + Ticket;
        public int? FullBackLength => Validation.HasValue ? PackTicketLength + Validation : null;
        public IEnumerable<int> CandidateLengths
        {
            get
            {
                if (FullBackLength.HasValue)
                    yield return FullBackLength.Value;
                yield return PackTicketLength;
            }
        }

        public (string Game, string Pack, string Ticket)? TryParse(string digits)
        {
            string body;
            if (digits.Length == PackTicketLength)
            {
                body = digits;
            }
            else if (FullBackLength is { } fullLength && digits.Length == fullLength)
            {
                body = digits[..PackTicketLength];
            }
            else
            {
                return null;
            }

            return (
                body[..Game],
                body.Substring(Game, Pack),
                body.Substring(Game + Pack, Ticket));
        }
    }

    private static readonly IReadOnlyList<BarcodeLayout> BarcodeLayouts =
        new[]
        {
            new BarcodeLayout("ga_4_7_3", 4, 7, 3, 10),
            new BarcodeLayout("sc_4_6_3", 4, 7, 3, 10),
            new BarcodeLayout("ky_3_7_3", 3, 7, 3, 11),
            new BarcodeLayout("ia_3_6_3", 3, 6, 3, 10)
        };

    private static readonly IReadOnlyList<StateOption> StateOptions =
        new[]
        {
            new StateOption("AZ", "Arizona", "ky_3_7_3"),
            new StateOption("CA", "California", null),
            new StateOption("CO", "Colorado", "ga_4_7_3"),
            new StateOption("CT", "Connecticut", "ga_4_7_3"),
            new StateOption("DC", "D.C.", "ga_4_7_3"),
            new StateOption("DE", "Delaware", "ga_4_7_3"),
            new StateOption("FL", "Florida", "ga_4_7_3"),
            new StateOption("GA", "Georgia", "ga_4_7_3"),
            new StateOption("IA", "Iowa", "ia_3_6_3"),
            new StateOption("ID", "Idaho", null),
            new StateOption("IL", "Illinois", null),
            new StateOption("IN", "Indiana", null),
            new StateOption("KS", "Kansas", "ga_4_7_3"),
            new StateOption("KY", "Kentucky", "ky_3_7_3"),
            new StateOption("LA", "Louisiana", "ga_4_7_3"),
            new StateOption("MA", "Massachusetts", "ga_4_7_3"),
            new StateOption("MD", "Maryland", null),
            new StateOption("ME", "Maine", "ga_4_7_3"),
            new StateOption("MI", "Michigan", null),
            new StateOption("MN", "Minnesota", "ga_4_7_3"),
            new StateOption("MO", "Missouri", null),
            new StateOption("MS", "Mississippi", "ga_4_7_3"),
            new StateOption("MT", "Montana", "ga_4_7_3"),
            new StateOption("NC", "North Carolina", "ia_3_6_3"),
            new StateOption("ND", "North Dakota", "ga_4_7_3"),
            new StateOption("NE", "Nebraska", "ga_4_7_3"),
            new StateOption("NH", "New Hampshire", "ga_4_7_3"),
            new StateOption("NJ", "New Jersey", "ga_4_7_3"),
            new StateOption("NM", "New Mexico", "ga_4_7_3"),
            new StateOption("NY", "New York", null),
            new StateOption("OH", "Ohio", null),
            new StateOption("OK", "Oklahoma", "ga_4_7_3"),
            new StateOption("OR", "Oregon", "ga_4_7_3"),
            new StateOption("PA", "Pennsylvania", "ga_4_7_3"),
            new StateOption("RI", "Rhode Island", "ga_4_7_3"),
            new StateOption("SC", "South Carolina", "sc_4_6_3"),
            new StateOption("SD", "South Dakota", "ga_4_7_3"),
            new StateOption("TN", "Tennessee", "ia_3_6_3"),
            new StateOption("TX", "Texas", "ga_4_7_3"),
            new StateOption("VA", "Virginia", null),
            new StateOption("VT", "Vermont", "ga_4_7_3"),
            new StateOption("WA", "Washington", "ky_3_7_3"),
            new StateOption("WI", "Wisconsin", null),
            new StateOption("WV", "West Virginia", "ga_4_7_3"),
            new StateOption("WY", "Wyoming", "ga_4_7_3")
        };

    private static string DigitsOnly(string raw)
    {
        Span<char> buffer = stackalloc char[raw.Length];
        var count = 0;
        foreach (var c in raw)
        {
            if (c is >= '0' and <= '9')
                buffer[count++] = c;
        }

        return new string(buffer[..count]);
    }

    private const uint ImageIcon = 1;
    private const uint LoadFromFile = 0x00000010;
    private const uint TrayIconId = 1;
    private const uint TrayCallbackMessage = 0x8001;
    private static readonly UIntPtr TraySubclassId = new(1);
    private static readonly UIntPtr RestoreCommandId = new(1001);
    private static readonly UIntPtr ExitCommandId = new(1002);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr SubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr refData);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr IconHandle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public uint TimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public uint InfoFlags;
        public Guid GuidItem;
        public IntPtr BalloonIconHandle;
    }

    private static class TrayMessages
    {
        public const uint Add = 0;
        public const uint Modify = 1;
        public const uint Delete = 2;
    }

    private static class TrayFlags
    {
        public const uint Message = 0x00000001;
        public const uint Icon = 0x00000002;
        public const uint Tip = 0x00000004;
        public const uint Info = 0x00000010;
    }

    private static class WindowMessages
    {
        public const uint LeftButtonDoubleClick = 0x0203;
        public const uint RightButtonUp = 0x0205;
    }

    private static class MenuFlags
    {
        public const uint String = 0x00000000;
        public const uint RightButton = 0x00000002;
        public const uint ReturnCommand = 0x00000100;
    }

    private enum StartupStage
    {
        Setup,
        Import,
        Login
    }

    private enum UserRole
    {
        None,
        Clerk,
        Manager
    }
}

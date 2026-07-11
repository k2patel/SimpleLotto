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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
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
    private readonly ObservableCollection<SaleLine> _sales = new();
    private readonly ObservableCollection<SaleLine> _allSales = new();
    private readonly ObservableCollection<ImportLine> _imports = new();
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
    private string _storeState = string.Empty;
    private string? _storeBarcodeLayout;
    private string _storeName = string.Empty;
    private string _storeStreet = string.Empty;
    private string _storeCity = string.Empty;
    private string _databaseSchemaVersion = string.Empty;
    private ClosingHistoryRow? _selectedClosingReport;
    private int _configuredBinCount = 90;
    private int _scanPairTimeoutSeconds = 5;
    private bool _displayBurnInEnabled = true;
    private int _displayBurnInIntervalMinutes = 15;
    private string _scannerVid = string.Empty;
    private string _scannerPid = string.Empty;
    private string _scannerSerial = string.Empty;
    private DateTime _lastCloseUtc = DateTime.MinValue;
    private bool _setupComplete;
    private bool _initialImportComplete;
    private bool _isWindowInitialized;
    private bool _isScannerPaired;
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
    private DateTime? _dashboardPendingBinAtUtc;
    private DateTime? _dashboardPendingTicketAtUtc;
    private int? _selectedBinNumber;
    private bool _isWorkflowDialogOpen;
    private const string ScannerVidSettingKey = "barcode_scanner_vid";
    private const string ScannerPidSettingKey = "barcode_scanner_pid";
    private const string ScannerSerialSettingKey = "barcode_scanner_serial";
    private const string ScanPairTimeoutSettingKey = "scan_pair_timeout_seconds";
    private const string DisplayBurnInEnabledSettingKey = "display_burn_in_enabled";
    private const string DisplayBurnInIntervalSettingKey = "display_burn_in_interval_minutes";
    private const string LicenseStatusSettingKey = "license_status";
    private const string LicenseLastCheckUtcSettingKey = "license_last_check_utc";
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

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

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
        _subclassProc = TraySubclassProc;
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
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
        _scanPairTimeoutSeconds = Math.Clamp(ReadIntSetting(state, ScanPairTimeoutSettingKey, 5), 1, 30);
        _displayBurnInEnabled = ReadBoolSetting(state, DisplayBurnInEnabledSettingKey, true);
        _displayBurnInIntervalMinutes = Math.Clamp(ReadIntSetting(state, DisplayBurnInIntervalSettingKey, 15), 1, 1440);
        _scannerVid = ReadSetting(state, ScannerVidSettingKey);
        _scannerPid = ReadSetting(state, ScannerPidSettingKey);
        _scannerSerial = ReadSetting(state, ScannerSerialSettingKey);
        _managerPasswordHash = ReadSetting(state, "manager_password_hash");
        _clerkName = ReadSetting(state, "clerk_name");
        _clerkPasswordHash = ReadSetting(state, "clerk_password_hash");
        _lastCloseUtc = ReadDateTimeSetting(state, "last_close_utc");

        StoreNameBox.Text = _storeName;
        StoreStreetBox.Text = _storeStreet;
        StoreCityBox.Text = _storeCity;
        BinCountBox.Value = _configuredBinCount;
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

        var selectedState = StateOptions.FirstOrDefault(s => s.Code == _storeState);
        if (selectedState is not null)
            StateComboBox.SelectedItem = selectedState;
        else
            UpdateBarcodeFormatFromState();

        _imports.Clear();
        foreach (var line in state.Imports)
            _imports.Add(new ImportLine(line.GameId, line.BundleId, line.Ticket, line.Bin, line.Source));

        _sales.Clear();
        _allSales.Clear();
        foreach (var line in state.Sales)
        {
            var saleLine = new SaleLine(
                line.SoldAtUtc.ToLocalTime(),
                line.GameId,
                line.Bin,
                line.Ticket,
                line.Quantity,
                line.AmountCents / 100m,
                line.Source);
            _allSales.Add(saleLine);
            if (line.SoldAtUtc > _lastCloseUtc)
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

        BuildImportBins(clearImports: false);

        if (_initialImportComplete)
            ShowLoginStage();
        else
            ShowImportStage();
    }

    private void SaveSetupState()
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
                _clerkPasswordHash));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Unable to save SQLite setup: {ex.Message}";
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
        var record = NewAuditRecord(category, action, detail);
        try
        {
            _store.InsertAudit(record);
            _auditLogRows.Insert(0, AuditLogRow.From(record));
            ApplyAuditLogPage(resetPage: true);
        }
        catch
        {
            // Audit failures should not block the operator workflow.
        }
    }

    private StoredAuditRecord NewAuditRecord(string category, string action, string detail) =>
        new(
            DateTime.UtcNow,
            category,
            action,
            string.IsNullOrWhiteSpace(_activeUserName) ? "system" : _activeUserName,
            detail);

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

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = SHA256.HashData(Combine(salt, Encoding.UTF8.GetBytes(password)));
        return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string storedHash)
    {
        var parts = storedHash.Split(':', 2);
        if (parts.Length != 2)
            return false;

        try
        {
            var salt = Convert.FromBase64String(parts[0]);
            var expected = Convert.FromBase64String(parts[1]);
            var actual = SHA256.HashData(Combine(salt, Encoding.UTF8.GetBytes(password)));
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] Combine(byte[] left, byte[] right)
    {
        var output = new byte[left.Length + right.Length];
        Buffer.BlockCopy(left, 0, output, 0, left.Length);
        Buffer.BlockCopy(right, 0, output, left.Length, right.Length);
        return output;
    }

    private void StartupPrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        switch (_startupStage)
        {
            case StartupStage.Setup:
                CompleteSetupStage();
                break;
            case StartupStage.Import:
                CompleteImportStage();
                break;
            case StartupStage.Login:
                CompleteLoginStage();
                break;
        }
    }

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
        if (_isWorkflowDialogOpen)
            return;

        if (StartupOverlay.Visibility == Visibility.Visible)
        {
            if (_startupStage == StartupStage.Import)
                CaptureScanKey(e, _startupScanBuffer, ProcessImportScanInput, ImportScanStatusText);
            return;
        }

        CaptureScanKey(e, _focusedScanBuffer, ProcessFocusedScanInput, DashboardScannerStatusText);
    }

    private static void CaptureScanKey(
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

        if (TryMapScanKey(e.Key, out var c))
        {
            e.Handled = true;
            buffer.Append(c);
            if (statusText is not null)
                statusText.Text = "Scanning...";
        }
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

        if (string.IsNullOrWhiteSpace(ManagerPasswordBox.Password))
        {
            StartupStatusText.Text = "Manager password is required.";
            return;
        }

        _storeState = state.Code;
        _storeBarcodeLayout = state.DefaultLayout;
        _storeName = storeName;
        _storeStreet = storeStreet;
        _storeCity = storeCity;
        _configuredBinCount = Math.Min(500, binCount);
        _managerPasswordHash = HashPassword(ManagerPasswordBox.Password);
        _clerkName = ClerkNameBox.Text.Trim();
        _clerkPasswordHash = string.IsNullOrWhiteSpace(ClerkPasswordBox.Password)
            ? string.Empty
            : HashPassword(ClerkPasswordBox.Password);
        _setupComplete = true;
        _initialImportComplete = false;
        BuildImportBins();
        SaveSetupState();
        ShowImportStage();
    }

    private void CompleteLoginStage()
    {
        if (LoginUserComboBox.SelectedItem is not ComboBoxItem userItem)
        {
            StartupStatusText.Text = "Select a user to login.";
            return;
        }

        var user = userItem.Content?.ToString() ?? string.Empty;
        var isManager = string.Equals(user, "Manager", StringComparison.Ordinal);
        var expectedHash = isManager ? _managerPasswordHash : _clerkPasswordHash;
        if (!VerifyPassword(LoginPasswordBox.Password, expectedHash))
        {
            StartupStatusText.Text = "Password does not match the selected user.";
            return;
        }

        _activeUserRole = isManager ? UserRole.Manager : UserRole.Clerk;
        _activeUserName = user;
        StartupOverlay.Visibility = Visibility.Collapsed;
        StatusText.Text = $"{user} logged in as {_activeUserRole} for {_storeState}.";
        DashboardScannerModeText.Text = "Global scanner";
        DashboardScannerStatusText.Text = "Ready for scanner input.";
        DashboardPairingStatusText.Text = "Background capture: not paired";
        ApplyRoleAccess();
        RefreshOperationalPages();
        TryRecordAudit("auth", "Login", $"{user} logged in as {_activeUserRole}");
    }

    private void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        TryRecordAudit("auth", "Logout", "User logged out");
        _activeUserRole = UserRole.None;
        _activeUserName = string.Empty;
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
        StartupStatusText.Text = "Select the store state and create the Manager password.";
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
        StartupStatusText.Text = "Enter the password for the selected user.";
    }

    private void CompleteImportStage()
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

        _initialImportComplete = true;
        SaveSetupState();
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

        var segments = SplitImportScanInput(raw).ToArray();
        if (segments.Length == 0)
        {
            UpdateImportStatusText("Scan a bin barcode or ticket barcode.");
            ImportScanStatusText.Text = "No scan data received.";
            return;
        }

        foreach (var segment in segments)
        {
            ProcessImportScanSegment(segment);
            if (_hasImportFailure)
                break;
        }
    }

    private void ProcessImportScanSegment(string raw)
    {
        if (TryParseBinNumber(raw, out var binNumber))
        {
            var bin = _importBins.FirstOrDefault(b => b.Number == binNumber);
            if (bin is null)
            {
                FailImport($"Wrong bin {binNumber}. Valid bins are 1 through {_configuredBinCount}.", "Wrong bin.");
                return;
            }

            AcceptImportBin(bin);
            return;
        }

        var ticket = TryParseImportTicket(raw);
        if (ticket is null)
        {
            FailImport("Scan was not recognized as a configured-state ticket or a BIN barcode.", "Import failure.");
            return;
        }

        AcceptImportTicket(ticket);
    }

    private void ProcessFocusedScanInput(string raw)
    {
        var segments = SplitImportScanInput(raw).ToArray();
        if (segments.Length == 0)
        {
            DashboardScannerStatusText.Text = "No scan data received.";
            return;
        }

        foreach (var segment in segments)
            ProcessFocusedScanSegment(segment);
    }

    private void ProcessFocusedScanSegment(string raw)
    {
        ExpireDashboardPendingScanPair();

        if (TryParseBinNumber(raw, out var binNumber))
        {
            if (!IsConfiguredBin(binNumber))
            {
                DashboardScannerStatusText.Text = $"Wrong bin {binNumber}. Scan a valid bin.";
                DashboardLastScanText.Text = $"Last scan failed: BIN-{binNumber}";
                StatusText.Text = $"Wrong bin {binNumber}.";
                _ = SpeakAsync("Wrong bin.");
                return;
            }

            _dashboardPendingBin = binNumber.ToString(CultureInfo.InvariantCulture);
            _dashboardPendingBinAtUtc = DateTime.UtcNow;
            if (_dashboardPendingTicket is not null)
            {
                PlaceDashboardBundle(_dashboardPendingBin, _dashboardPendingTicket);
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
            return;
        }

        if (!string.IsNullOrWhiteSpace(_dashboardPendingBin))
        {
            PlaceDashboardBundle(_dashboardPendingBin, ticket);
            return;
        }

        var activeBundle = FindActiveBundle(ticket);
        if (activeBundle is null)
        {
            _ = PromptForActivationBinAsync(ticket);
            return;
        }

        var backfill = BuildTicketBackfillSale(DateTime.Now, activeBundle, ticket.Ticket, "normal_sale");
        var line = backfill.Sale;
        var updatedBundle = activeBundle with { Ticket = backfill.NextTicket };
        _sales.Insert(0, line);
        SaveSaleLineAndUpdateImportTicket(line, updatedBundle);
        ReplaceImportLine(updatedBundle);
        SalesListView.SelectedItem = line;
        DashboardScannerStatusText.Text = $"Ticket captured for game {ticket.GameId}.";
        DashboardLastScanText.Text = $"Game {ticket.GameId} | Bundle {ticket.BundleId} | Sold {line.Ticket} | Next {backfill.NextTicket} | Bin {activeBundle.Bin}";
        StatusText.Text = $"Scanner sale captured for game {ticket.GameId}, {line.Quantity.ToString(CultureInfo.CurrentCulture)} ticket{(line.Quantity == 1 ? string.Empty : "s")}.";
        RefreshTotals();
        RefreshOperationalPages();
    }

    private async Task PromptForActivationBinAsync(ImportTicket ticket)
    {
        ClearDashboardPendingScanPair();
        DashboardScannerStatusText.Text = $"Bundle {ticket.BundleId} is not placed. Enter or scan bin.";
        DashboardLastScanText.Text = $"Game {ticket.GameId} | Bundle {ticket.BundleId} | waiting for bin";
        StatusText.Text = "Bundle is not placed. Enter a bin number or scan a bin barcode.";
        _ = SpeakAsync("Enter bin number or scan bin.");

        var binNumber = await ShowActivationBinDialogAsync(ticket);
        if (binNumber is null)
        {
            DashboardScannerStatusText.Text = "Bundle activation cancelled.";
            StatusText.Text = "Bundle activation cancelled.";
            return;
        }

        var bin = binNumber.Value.ToString(CultureInfo.InvariantCulture);
        await ActivateBundleInBinAsync(bin, ticket, updateDashboardStatus: true);
    }

    private async Task<int?> ShowActivationBinDialogAsync(ImportTicket ticket)
    {
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

        binBox.KeyDown += (_, e) =>
        {
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
        try
        {
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary || selectedBin is not null)
                return selectedBin;

            return null;
        }
        finally
        {
            _isWorkflowDialogOpen = false;
        }
    }

    private async void PlaceDashboardBundle(string bin, ImportTicket ticket)
    {
        await ActivateBundleInBinAsync(bin, ticket, updateDashboardStatus: true);
    }

    private async Task<bool> ActivateBundleInBinAsync(string bin, ImportTicket ticket, bool updateDashboardStatus)
    {
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

        if (GamePriceCents(ticket.GameId) <= 0)
        {
            var priceSaved = await ShowActivationPriceDialogAsync(bin, ticket);
            if (!priceSaved)
            {
                if (updateDashboardStatus)
                {
                    DashboardScannerStatusText.Text = $"Price required for game {ticket.GameId}. Bundle not activated.";
                    DashboardLastScanText.Text = $"Game {ticket.GameId} | Bundle {ticket.BundleId} | price missing";
                    ClearDashboardPendingScanPair();
                }

                StatusText.Text = $"Enter a price before activating game {ticket.GameId}.";
                _ = SpeakAsync("Price required.");
                return false;
            }
        }

        var line = new ImportLine(ticket.GameId, ticket.BundleId, ticket.Ticket, bin, "activation");
        _imports.Insert(0, line);
        SaveImportLine(line);
        if (updateDashboardStatus)
        {
            DashboardScannerStatusText.Text = $"Bundle activated in bin {bin}.";
            DashboardLastScanText.Text = $"Game {ticket.GameId} | Bundle {ticket.BundleId} | Ticket {ticket.Ticket} | Bin {bin}";
            ClearDashboardPendingScanPair();
        }

        StatusText.Text = $"Bundle {ticket.BundleId} activated in bin {bin}.";
        _ = SpeakAsync($"Bundle activated in bin {bin}.");
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
        _dashboardPendingBinAtUtc = null;
        _dashboardPendingTicketAtUtc = null;
    }

    private async Task<bool> ShowActivationPriceDialogAsync(string bin, ImportTicket ticket)
    {
        var existingGame = FindKnownGame(ticket.GameId);
        if (existingGame is { PriceCents: > 0 } &&
            !string.IsNullOrWhiteSpace(existingGame.Name) &&
            !string.Equals(existingGame.Name, "Name not set", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

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
            Value = existingGame?.PriceCents > 0 ? existingGame.PriceCents / 100d : double.NaN,
            Minimum = 1,
            SmallChange = 1,
            LargeChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        var statusText = new TextBlock
        {
            Text = "Enter the ticket price, or scan the price while the game price box is focused.",
            TextWrapping = TextWrapping.Wrap
        };
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
            Title = "Price required",
            Content = content,
            PrimaryButtonText = "Save and Activate",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (PriceCentsFromNumberBox(priceBox) > 0)
                return;

            args.Cancel = true;
            statusText.Text = "Enter or scan a positive ticket price in the game price box before continuing.";
        };

        _isWorkflowDialogOpen = true;
        try
        {
            _ = priceBox.Focus(FocusState.Programmatic);
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return false;
        }
        finally
        {
            _isWorkflowDialogOpen = false;
        }

        var priceCents = PriceCentsFromNumberBox(priceBox);
        if (priceCents <= 0)
            return false;

        var name = string.IsNullOrWhiteSpace(nameBox.Text)
            ? $"Game {ticket.GameId}"
            : nameBox.Text.Trim();
        var record = new GameCatalogRecord(
            ticket.GameId,
            name,
            priceCents,
            "Activation",
            existingGame?.ImageUri ?? "ms-appx:///Assets/SimpleLottoLogo64.png",
            existingGame?.ImageStatus ?? "Image not uploaded");

        UpsertManualGameRecord(record);
        return true;
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
        var line = new ImportLine(
            ticket.GameId,
            ticket.BundleId,
            ticket.Ticket,
            bin.Number.ToString(CultureInfo.InvariantCulture),
            "initial_import");
        _imports.Insert(0, line);
        SaveImportLine(line);
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
            ImportInstructionText.Text = $"{_imports.Count} import{(_imports.Count == 1 ? string.Empty : "s")} recorded. Continue when all physical bins are imported.";
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
        if (SalesListView.SelectedItem is not SaleLine sale)
        {
            StatusText.Text = "Select a sale to void.";
            return;
        }

        var correction = new SaleLine(
            DateTime.Now,
            sale.GameId,
            sale.Bin,
            sale.Ticket,
            -sale.Quantity,
            -sale.Amount,
            "undo");
        _sales.Insert(0, correction);
        SaveSaleLine(correction);
        TryRecordAudit("correction", "Sale voided", $"Game {sale.GameId}, ticket {sale.Ticket}, bin {sale.Bin}");
        StatusText.Text = $"Voided game {sale.GameId} sale for {sale.AmountText}.";
        RefreshTotals();
        RefreshBinCards();
    }

    private async void CloseShiftButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSection("Closing");
        await StartClosingScanWorkflowAsync();
    }

    private void SaveImportLine(ImportLine line)
    {
        try
        {
            _store.InsertImport(new StoredImportLine(line.GameId, line.BundleId, line.Ticket, line.Bin, line.Source));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Unable to save placement to SQLite: {ex.Message}";
        }
    }

    private void SaveSaleLine(SaleLine line)
    {
        try
        {
            _store.InsertSale(ToStoredSaleLine(line));
            _allSales.Insert(0, line);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Unable to save sale to SQLite: {ex.Message}";
        }
    }

    private void SaveSaleLineAndUpdateImportTicket(SaleLine line, ImportLine updatedBundle)
    {
        try
        {
            _store.InsertSaleAndUpdateImportTicket(
                ToStoredSaleLine(line),
                new StoredImportLine(
                    updatedBundle.GameId,
                    updatedBundle.BundleId,
                    updatedBundle.Ticket,
                    updatedBundle.Bin,
                    updatedBundle.Source));
            _allSales.Insert(0, line);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Unable to save sale and ticket state to SQLite: {ex.Message}";
        }
    }

    private void DeleteSaleLine(SaleLine line)
    {
        try
        {
            _store.DeleteSale(ToStoredSaleLine(line));
            _allSales.Remove(line);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Unable to delete sale from SQLite: {ex.Message}";
        }
    }

    private void ClearStoredSales()
    {
        try
        {
            _store.ClearSales();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Unable to clear SQLite sales: {ex.Message}";
        }
    }

    private void SaveManualGame(GameCatalogRecord game)
    {
        try
        {
            _store.UpsertManualGame(new StoredGameRecord(
                game.GameId,
                game.Name,
                game.PriceCents,
                game.Source,
                game.ImageUri,
                game.ImageStatus));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Unable to save game to SQLite: {ex.Message}";
        }
    }

    private void UpsertManualGameRecord(GameCatalogRecord game)
    {
        var existing = _manualGameCatalog.FindIndex(g =>
            string.Equals(g.GameId, game.GameId, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
            _manualGameCatalog[existing] = game;
        else
            _manualGameCatalog.Add(game);

        SaveManualGame(game);
        RefreshGameCatalog();
        SelectGame(game.GameId);
        SyncRdisplayTiles();
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

    private static StoredSaleLine ToStoredSaleLine(SaleLine line) =>
        new(
            line.SoldAt.ToUniversalTime(),
            line.GameId,
            line.Bin,
            line.Ticket,
            line.Quantity,
            (long)Math.Round(line.Amount * 100m, MidpointRounding.AwayFromZero),
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
        var average = ticketCount == 0 ? 0 : revenue / ticketCount;

        SalesSubtitleText.Text = salesCount == 0
            ? "No entries yet"
            : $"{salesCount} sale entr{(salesCount == 1 ? "y" : "ies")}";
        RevenueText.Text = revenue.ToString("C", CultureInfo.CurrentCulture);
        TicketsText.Text = ticketCount.ToString(CultureInfo.CurrentCulture);
        AverageText.Text = average.ToString("C", CultureInfo.CurrentCulture);
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
            ClosingExpectedCashText.Text = selected.ExpectedCashText;
            return;
        }

        ClosingSalesText.Text = _sales.Sum(s => s.Amount).ToString("C", CultureInfo.CurrentCulture);
        ClosingTicketsText.Text = _sales.Sum(s => s.Quantity).ToString(CultureInfo.CurrentCulture);
        ClosingExpectedCashText.Text = MoneyText(CurrentClosingExpectedCashCents());
    }

    private long CurrentClosingExpectedCashCents()
    {
        var instantTicketSalesCents = (long)Math.Round(_sales.Sum(s => s.Amount) * 100m, MidpointRounding.AwayFromZero);
        instantTicketSalesCents += _closingScanSales.Sum(s =>
            (long)Math.Round(s.Sale.Amount * 100m, MidpointRounding.AwayFromZero));
        if (_closingScanCaptured && _closingScanIssues.Count == 0 && _closingUnmatchedTickets.Count == 0)
            instantTicketSalesCents += ClosingSoldOutBundles().Sum(bundle => GamePriceCents(bundle.GameId));

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

        AuditLogCountText.Text = $"{filtered.Count.ToString(CultureInfo.CurrentCulture)} of {_auditLogRows.Count.ToString(CultureInfo.CurrentCulture)} action{(_auditLogRows.Count == 1 ? string.Empty : "s")}";
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

    private void ClosingHistorySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyClosingHistoryPage(resetPage: true);
    }

    private void AuditLogSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyAuditLogPage(resetPage: true);
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

        if (ClosingTabs.SelectedItem == ClosingScanEvidenceTab)
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
            .Select(x => new RdisplayTileState(
                x.Bin,
                x.Current.GameId,
                x.Game?.Name ?? $"Game {x.Current.GameId}",
                x.Current.Ticket,
                PriceCentsForDisplay(x.Game?.PriceCents ?? 0)));

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
            if (current is not null)
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

    private void RefreshLicenseRegistrationStatus()
    {
        var state = _store.Load();
        var licenseStatus = ReadSetting(state, LicenseStatusSettingKey);
        var lastCheckUtc = ReadDateTimeSetting(state, LicenseLastCheckUtcSettingKey);
        LicenseRegistrationText.Text = string.IsNullOrWhiteSpace(licenseStatus)
            ? "Device registration has not been checked."
            : $"License status: {licenseStatus}";
        LicenseLastCheckedText.Text = lastCheckUtc == DateTime.MinValue
            ? "License last checked: never"
            : $"License last checked: {lastCheckUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}";
    }

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
        SettingsDatabasePathText.Text = LocalStore.DbPath;
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

        ScannerPairingStatusText.Text = "Scanner paired";
        ScannerPairingDetailText.Text = string.IsNullOrWhiteSpace(_scannerSerial)
            ? $"VID {_scannerVid} / PID {_scannerPid} / no serial"
            : $"VID {_scannerVid} / PID {_scannerPid} / SN {_scannerSerial}";
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
        BackupSettingsTab.Visibility = managerVisibility;
        EmailSettingsTab.Visibility = managerVisibility;
        AuditSettingsTab.Visibility = managerVisibility;
        GameSettingsTab.Visibility = managerVisibility;

        SettingsSubtitleText.Text = IsManager
            ? "Store, scanner, display, and game setup controls for the current installation."
            : "Scanner and display settings available for Clerk access.";

        if (!IsManager)
        {
            if (SettingsTabs.SelectedItem is not TabViewItem selectedSettingsTab ||
                selectedSettingsTab != ScannerDisplaySettingsTab)
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

    private async void AddBundleToBinButton_Click(object sender, RoutedEventArgs e)
    {
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
        _selectedBinBundles.Clear();
        var lines = _imports
            .Where(i => string.Equals(i.Bin, binNumber.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase))
            .ToList();

        _selectedBinNumber = binNumber;
        AddBundleToBinButton.IsEnabled = true;
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
            string.Equals(i.BundleId, ticket.BundleId, StringComparison.OrdinalIgnoreCase));

    private async void StartClosingScanButton_Click(object sender, RoutedEventArgs e)
    {
        await StartClosingScanWorkflowAsync();
    }

    private async Task StartClosingScanWorkflowAsync()
    {
        if (_isWorkflowDialogOpen)
            return;

        ExitClosingReportContext();
        ClosingTabs.SelectedItem = ClosingScanEvidenceTab;
        _closingScannedBins.Clear();
        _closingScannedBundleKeys.Clear();
        _closingCurrentPlacements.Clear();
        _closingUnmatchedTickets.Clear();
        _closingResolvedPlacements.Clear();
        _closingScanRows.Clear();
        _closingScanIssues.Clear();
        _closingScanSales.Clear();
        _closingScanCaptured = false;
        RefreshClosingActionState();
        RefreshClosingBins();
        ClosingStatusText.Text = "Closing scan started. Scan the current ticket from each physical bin.";
        AppLog.Info("Closing scan overlay starting.");
        _ = SpeakAsync("Start scanning.");

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
            DisplayMemberPath = "ScannedText",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        void RefreshDialogTotals()
        {
            totalText.Text = _closingScanRows.Count.ToString(CultureInfo.CurrentCulture);
            if (_selectedClosingReport is null)
                ClosingEvidenceText.Text = $"{_closingScannedBins.Count.ToString(CultureInfo.CurrentCulture)} / {ActiveClosingBinCount().ToString(CultureInfo.CurrentCulture)}";
        }

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
                statusText.Text = $"Closing scan failed to process the last barcode: {ex.Message}";
                ClosingStatusText.Text = "Closing scan failed to process the last barcode. Re-scan or close scanning and restart.";
            }
        }

        var rootSize = Content.XamlRoot?.Size ?? new Windows.Foundation.Size(0, 0);
        var availableDialogWidth = rootSize.Width > 0
            ? Math.Max(360, rootSize.Width - 128)
            : 1180;
        var availableDialogHeight = rootSize.Height > 0
            ? Math.Max(320, rootSize.Height - 180)
            : 760;
        var dialogMinWidth = rootSize.Width > 0
            ? Math.Min(520, availableDialogWidth)
            : 520;
        var dialogMaxWidth = rootSize.Width > 0
            ? Math.Min(1180, availableDialogWidth)
            : 1180;
        var dialogMinHeight = rootSize.Height > 0
            ? Math.Min(420, availableDialogHeight)
            : 420;
        var content = new Grid
        {
            MinHeight = dialogMinHeight,
            MaxHeight = availableDialogHeight,
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
                CaptureScanKey(args, dialogScanBuffer, AcceptDialogScan, statusText)),
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

        void ApplyResponsiveDialogLayout(double width)
        {
            var stacked = width > 0 && width < 560;
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

            Grid.SetRow(statusText, stacked ? 3 : 2);
            Grid.SetColumn(statusText, 0);
            Grid.SetColumnSpan(statusText, 2);
        }

        ApplyResponsiveDialogLayout(rootSize.Width);
        content.SizeChanged += (_, args) => ApplyResponsiveDialogLayout(args.NewSize.Width);
        content.Children.Add(statusText);

        void ApplyDialogSize(Windows.Foundation.Size size)
        {
            availableDialogWidth = size.Width > 0
                ? Math.Max(360, size.Width - 128)
                : 1180;
            availableDialogHeight = size.Height > 0
                ? Math.Max(320, size.Height - 180)
                : 760;
            dialogMinWidth = size.Width > 0
                ? Math.Min(520, availableDialogWidth)
                : 520;
            dialogMaxWidth = size.Width > 0
                ? Math.Min(1180, availableDialogWidth)
                : 1180;
            dialogMinHeight = size.Height > 0
                ? Math.Min(420, availableDialogHeight)
                : 420;

            var panelWidth = dialogMaxWidth;
            var panelHeight = availableDialogHeight;
            ClosingScanOverlayPanel.Width = panelWidth;
            ClosingScanOverlayPanel.Height = panelHeight;
            content.Width = Math.Max(160, panelWidth - 32);
            content.Height = Math.Max(160, panelHeight - 84);
            content.MinHeight = dialogMinHeight;
            content.MaxHeight = Math.Max(160, panelHeight - 84);
            scanList.MaxHeight = Math.Max(180, panelHeight - 220);
            ApplyResponsiveDialogLayout(size.Width);
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
        RoutedEventHandler closeHandler = (_, _) => closed.TrySetResult(true);
        ClosingScanOverlayCloseButton.Click += closeHandler;
        ClosingScanOverlay.SizeChanged += overlaySizeChanged;

        _isWorkflowDialogOpen = true;
        try
        {
            AppLog.Info("Closing scan overlay opened.");
            ClosingScanOverlayTitleText.Text = "Closing Scan";
            ClosingScanOverlayContent.Children.Clear();
            ClosingScanOverlayContent.Children.Add(content);
            ClosingScanOverlay.Visibility = Visibility.Visible;
            ApplyOverlaySizeFromActual();
            _ = content.Focus(FocusState.Programmatic);
            await closed.Task;
        }
        finally
        {
            ClosingScanOverlayCloseButton.Click -= closeHandler;
            ClosingScanOverlay.SizeChanged -= overlaySizeChanged;
            ClosingScanOverlay.Visibility = Visibility.Collapsed;
            ClosingScanOverlayContent.Children.Clear();

            AppLog.Info($"Closing scan overlay closed. rows={_closingScanRows.Count.ToString(CultureInfo.InvariantCulture)}; scannedBins={_closingScannedBins.Count.ToString(CultureInfo.InvariantCulture)}; issues={_closingScanIssues.Count.ToString(CultureInfo.InvariantCulture)}; unmatched={_closingUnmatchedTickets.Count.ToString(CultureInfo.InvariantCulture)}.");
            _isWorkflowDialogOpen = false;
            _closingScanCaptured = true;
            RefreshClosingBins();
            RefreshClosingActionState();
            ClosingStatusText.Text = $"{_closingScannedBins.Count.ToString(CultureInfo.CurrentCulture)} bin{(_closingScannedBins.Count == 1 ? string.Empty : "s")} scanned. Unscanned active bins remain marked.";
        }
    }

    private int ActiveClosingBinCount() =>
        _imports
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
                statusText.Text = $"No active bin matched scan {raw}.";
                return;
            }

            _closingScannedBins.Add(binNumber);
            _closingScannedBundleKeys.Add(BundleKey(activeBundle));
            var backfill = BuildTicketBackfillSale(DateTime.Now, activeBundle, ticket.Ticket, "closing_gap_fill_sold");
            UpsertClosingScanSale(BundleKey(activeBundle), backfill.Sale);
            ReplaceClosingCurrentPlacement(activeBundle with { Ticket = backfill.NextTicket });
            _closingScanRows.Insert(0, new ClosingScanRow(
                $"Bin {binNumber.ToString(CultureInfo.CurrentCulture)} | {backfill.Sale.Ticket}",
                "Scanned"));
            statusText.Text = $"Bin {binNumber.ToString(CultureInfo.CurrentCulture)} scanned. {backfill.Sale.Quantity.ToString(CultureInfo.CurrentCulture)} ticket{(backfill.Sale.Quantity == 1 ? string.Empty : "s")} captured.";
            return;
        }

        if (TryParseBinNumber(raw, out var directBin) && IsConfiguredBin(directBin))
        {
            _closingScanRows.Insert(0, new ClosingScanRow(raw, "Ignored bin scan"));
            statusText.Text = "Closing scan accepts ticket barcodes only. Bin scan ignored.";
            return;
        }

        _closingScanRows.Insert(0, new ClosingScanRow(raw, "Unrecognized"));
        AddClosingScanIssue(
            "Unrecognized scan",
            $"Scan {raw} was not recognized as a ticket barcode. Re-scan the ticket or resolve before finalizing.");
        statusText.Text = $"Scan was not recognized: {raw}";
    }

    private void AddClosingScanIssue(string title, string detail) =>
        _closingScanIssues.Add(new ClosingScanIssue(title, detail));

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
        ResolveClosingIssuesButton.IsEnabled = _closingUnmatchedTickets.Count > 0;
        FinalizeClosingButton.IsEnabled = _closingScanCaptured &&
            _closingScanIssues.Count == 0 &&
            _closingUnmatchedTickets.Count == 0;

        if (_closingScanIssues.Count > 0)
        {
            ClosingExceptionText.Text = "One or more scans were not recognized. Restart the closing scan and scan ticket barcodes only.";
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
            .Where(i => !_closingScannedBundleKeys.Contains(BundleKey(i)))
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
        }

        RefreshClosingBins();
        RefreshClosingActionState();
    }

    private async void FinalizeClosingButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_closingScanCaptured)
        {
            ClosingStatusText.Text = "Run closing scan before finalizing.";
            return;
        }

        if (_closingScanIssues.Count > 0)
        {
            ClosingStatusText.Text = "Restart the closing scan before finalizing. One or more scans were not recognized.";
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

        var closedBundles = ClosingSoldOutBundles();
        var closedAtUtc = DateTime.UtcNow;
        var generatedSales = _closingScanSales
            .Select(s => s.Sale)
            .Concat(BuildClosingGeneratedSales(closedBundles, closedAtUtc))
            .ToList();
        var instantTicketSalesCents = (long)Math.Round(
            (_sales.Sum(s => s.Amount) + generatedSales.Sum(s => s.Amount)) * 100m,
            MidpointRounding.AwayFromZero);
        var expectedCashCents = instantTicketSalesCents + onlineSaleCents - instantCashoutCents - onlineCashoutCents;
        var selectedEmailAttachments = SelectedSettingsEmailReportNames();
        var closingEmailSummary = BuildClosingEmailSummaryText(
            SettingsEmailSendCheckBox.IsChecked == true,
            selectedEmailAttachments);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Finalize shift closing?",
            Content = $"{_closingScannedBins.Count.ToString(CultureInfo.CurrentCulture)} bin{(_closingScannedBins.Count == 1 ? string.Empty : "s")} scanned. {closedBundles.Count.ToString(CultureInfo.CurrentCulture)} unscanned active bundle{(closedBundles.Count == 1 ? string.Empty : "s")} will be closed out as closing gap-fill sold.{Environment.NewLine}{Environment.NewLine}Instant ticket sales: {MoneyText(instantTicketSalesCents)}{Environment.NewLine}Online sale: {MoneyText(onlineSaleCents)}{Environment.NewLine}Instant cashout: {MoneyText(instantCashoutCents)}{Environment.NewLine}Online cashout: {MoneyText(onlineCashoutCents)}{Environment.NewLine}Expected cash: {MoneyText(expectedCashCents)}{Environment.NewLine}{Environment.NewLine}{closingEmailSummary}",
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
        try
        {
            WriteClosingReports(
                reportTarget,
                intervalStartUtc,
                closedAtUtc,
                reportSales,
                closedBundles,
                _closingCurrentPlacements,
                _closingResolvedPlacements,
                instantTicketSalesCents,
                onlineSaleCents,
                onlineCashoutCents,
                instantCashoutCents,
                expectedCashCents,
                selectedEmailAttachments);
        }
        catch (Exception ex)
        {
            AppLog.Error("Closing report generation failed.", ex);
            ClosingStatusText.Text = $"Closing report generation failed: {ex.Message}";
            StatusText.Text = "Closing report generation failed. Shift was not closed.";
            return;
        }

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
            closedBundles.Count,
            _closingCurrentPlacements.Count,
            _closingResolvedPlacements.Count);
        var auditRecord = NewAuditRecord(
            "closing",
            "Shift closed",
            $"{_closingScannedBins.Count.ToString(CultureInfo.InvariantCulture)} scanned bins, {closedBundles.Count.ToString(CultureInfo.InvariantCulture)} closed bundles, {_closingResolvedPlacements.Count.ToString(CultureInfo.InvariantCulture)} resolved bundles, expected cash {MoneyText(expectedCashCents)}");
        try
        {
            _store.CompleteClosing(
                closedAtUtc,
                closingRecord,
                auditRecord,
                generatedSales.Select(ToStoredSaleLine),
                closedBundles.Select(i => new StoredImportLine(i.GameId, i.BundleId, i.Ticket, i.Bin, i.Source)),
                _closingCurrentPlacements.Select(i => new StoredImportLine(i.GameId, i.BundleId, i.Ticket, i.Bin, i.Source)),
                _closingResolvedPlacements.Select(i => new StoredImportLine(i.GameId, i.BundleId, i.Ticket, i.Bin, i.Source)));
        }
        catch (Exception ex)
        {
            AppLog.Error("Closing finalization failed.", ex);
            TryDeleteReportFolder(reportTarget.Folder);
            ClosingStatusText.Text = $"Closing finalization failed: {ex.Message}";
            return;
        }

        _lastCloseUtc = closedAtUtc;
        foreach (var bundle in closedBundles)
            _imports.Remove(bundle);
        foreach (var placement in _closingCurrentPlacements)
            ReplaceImportLine(placement);
        foreach (var placement in _closingResolvedPlacements)
            _imports.Add(placement);
        foreach (var generated in generatedSales)
            _allSales.Insert(0, generated);
        _closingHistoryRows.Insert(0, ClosingHistoryRow.From(closingRecord));
        ApplyClosingHistoryPage(resetPage: true);
        ClosingHistoryListView.SelectedItem = _pagedClosingHistoryRows.FirstOrDefault();
        _auditLogRows.Insert(0, AuditLogRow.From(auditRecord));
        ApplyAuditLogPage(resetPage: true);
        _sales.Clear();
        ClosingOnlineSaleBox.Value = 0;
        ClosingOnlineCashoutBox.Value = 0;
        ClosingInstantCashoutBox.Value = 0;
        _closingScannedBins.Clear();
        _closingScannedBundleKeys.Clear();
        _closingCurrentPlacements.Clear();
        _closingUnmatchedTickets.Clear();
        _closingResolvedPlacements.Clear();
        _closingScanRows.Clear();
        _closingScanIssues.Clear();
        _closingScanSales.Clear();
        _closingScanCaptured = false;
        StatusText.Text = $"Shift closed. Reports saved to {reportTarget.Folder}.";
        ClosingStatusText.Text = $"Closed at {_lastCloseUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}. Reports saved to {reportTarget.ShiftLabel}.";
        _ = SpeakAsync("Shift closed. New sales count toward the next close.");
        RefreshTotals();
        RefreshOperationalPages();
    }

    private List<SaleLine> BuildClosingGeneratedSales(IEnumerable<ImportLine> closedBundles, DateTime closedAtUtc) =>
        closedBundles
            .Select(bundle => new SaleLine(
                closedAtUtc.ToLocalTime(),
                bundle.GameId,
                bundle.Bin,
                bundle.Ticket,
                1,
                GamePriceCents(bundle.GameId) / 100m,
                "closing_gap_fill_sold"))
            .ToList();

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
        IReadOnlyList<string> selectedEmailAttachments)
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
                CsvLine("shift_label", "period_start", "period_end", "instant_ticket_sales", "online_sale", "instant_cashout", "online_cashout", "expected_cash", "expected_cash_formula", "scanned_bins", "active_bins", "closed_bundles", "current_bundles", "resolved_bundles"),
                CsvLine(target.ShiftLabel, periodStart, periodEnd, MoneyCsv(instantTicketSalesCents), MoneyCsv(onlineSaleCents), MoneyCsv(instantCashoutCents), MoneyCsv(onlineCashoutCents), MoneyCsv(expectedCashCents), formula, _closingScannedBins.Count.ToString(CultureInfo.InvariantCulture), ActiveClosingBinCount().ToString(CultureInfo.InvariantCulture), closedBundles.Count.ToString(CultureInfo.InvariantCulture), currentBundles.Count.ToString(CultureInfo.InvariantCulture), resolvedBundles.Count.ToString(CultureInfo.InvariantCulture))
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
                CsvLine("email_attachments", string.Join(";", selectedEmailAttachments)),
                CsvLine("pdf_report", Path.Combine(target.Folder, "closing_report.pdf"))
            },
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(target.Folder, "closing_report.txt"),
            BuildClosingReportText(target, periodStart, periodEnd, sales, instantTicketSalesCents, onlineSaleCents, onlineCashoutCents, instantCashoutCents, expectedCashCents, closedBundles.Count, currentBundles.Count, resolvedBundles.Count),
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
            resolvedBundles.Count);
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
        int resolvedBundleCount)
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
            resolvedBundleCount));
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
        int resolvedBundleCount)
    {
        var builder = new StringBuilder();
        var totalSalesCents = instantTicketSalesCents + onlineSaleCents;
        var totalCashoutCents = instantCashoutCents + onlineCashoutCents;
        var ticketCount = sales.Sum(s => s.Quantity);
        var saleCount = sales.Count;
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
        PdfInventoryStat(builder, 58, 296, "Sales rows", saleCount.ToString(CultureInfo.InvariantCulture));
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
            CsvLine("sold_at", "source", "game_id", "bin", "ticket", "quantity", "amount")
        };
        lines.AddRange(sales.Select(s => CsvLine(
            s.SoldAt.ToString("O", CultureInfo.InvariantCulture),
            s.Source,
            s.GameId,
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

    private void WriteAnomaliesCsv(string path)
    {
        var lines = new List<string>
        {
            CsvLine("title", "detail")
        };
        lines.AddRange(_closingScanIssues.Select(i => CsvLine(i.Title, i.Detail)));
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
        int resolvedBundleCount)
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
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch
        {
            // A failed cleanup should not hide the original closing failure.
        }
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

    private TicketBackfillSale BuildTicketBackfillSale(DateTime soldAt, ImportLine activeBundle, string scannedTicket, string source)
    {
        var range = BuildTicketBackfillRange(activeBundle.Ticket, scannedTicket);
        var price = GamePriceCents(activeBundle.GameId) / 100m;
        return new TicketBackfillSale(
            new SaleLine(
                soldAt,
                activeBundle.GameId,
                activeBundle.Bin,
                range.SoldTicketText,
                range.Quantity,
                range.Quantity * price,
                source),
            range.NextTicket);
    }

    private static TicketBackfillRange BuildTicketBackfillRange(string currentTicket, string scannedTicket)
    {
        var currentText = string.IsNullOrWhiteSpace(currentTicket) ? scannedTicket : currentTicket.Trim();
        var scannedText = string.IsNullOrWhiteSpace(scannedTicket) ? currentText : scannedTicket.Trim();
        var width = Math.Max(TicketSerialWidth(currentText), TicketSerialWidth(scannedText));

        if (TryParseTicketSerial(currentText, out var currentSerial) &&
            TryParseTicketSerial(scannedText, out var scannedSerial))
        {
            if (scannedSerial >= currentSerial)
            {
                var quantity = scannedSerial - currentSerial + 1;
                var startText = FormatTicketSerial(currentSerial, width);
                var endText = FormatTicketSerial(scannedSerial, width);
                var soldText = quantity == 1 ? endText : $"{startText}-{endText}";
                return new TicketBackfillRange(soldText, quantity, FormatTicketSerial(scannedSerial + 1, width));
            }

            return new TicketBackfillRange(scannedText, 1, FormatTicketSerial(currentSerial, width));
        }

        if (TryParseTicketSerial(scannedText, out var fallbackScannedSerial))
            return new TicketBackfillRange(scannedText, 1, FormatTicketSerial(fallbackScannedSerial + 1, width));

        return new TicketBackfillRange(scannedText, 1, currentText);
    }

    private static bool TryParseTicketSerial(string ticket, out int serial)
    {
        var digits = DigitsOnly(ticket);
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out serial);
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
                priceBox
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
        if (priceCents <= 0)
        {
            GameCatalogStatusText.Text = $"Enter a positive price before adding game {gameId}.";
            return;
        }

        var record = new GameCatalogRecord(
            gameId,
            name,
            priceCents,
            "Manual",
            "ms-appx:///Assets/SimpleLottoLogo64.png",
            "Image not uploaded");

        UpsertManualGameRecord(record);
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
        if (priceCents <= 0)
        {
            GameCatalogStatusText.Text = $"Enter a positive price before saving game {game.GameId}.";
            return;
        }

        var updated = game with
        {
            Name = name,
            PriceCents = priceCents,
            Source = "Manual"
        };
        UpsertManualGameRecord(updated);
        GameCatalogStatusText.Text = $"Game {updated.GameId} details saved.";
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

    private void CheckLicenseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireManagerAccess("license registration"))
            return;

        if (string.IsNullOrWhiteSpace(_storeName) ||
            string.IsNullOrWhiteSpace(_storeState))
        {
            LicenseStatusText.Text = "Store name and state are required before checking license registration.";
            return;
        }

        var checkedAtUtc = DateTime.UtcNow;
        SaveSetting(LicenseStatusSettingKey, "Pending license service");
        SaveSetting(LicenseLastCheckUtcSettingKey, checkedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        RefreshLicenseRegistrationStatus();
        LicenseStatusText.Text = "License service is not connected in this scaffold. Store identity is ready for the WindowsPOS-compatible license check adapter.";
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
        RefreshScannerPairingStatus();
        ScannerPairingStatusText.Text = $"Scanner paired: {picked.DisplayLabel}";
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
            using (var conn = _store.Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE)";
                cmd.ExecuteNonQuery();
            }

            var storeName = SanitizePathSegment(string.IsNullOrWhiteSpace(_storeName) ? "SimpleLotto" : _storeName);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            var destDir = Path.Combine(folder, storeName, "backup", stamp);
            Directory.CreateDirectory(destDir);
            var zipPath = Path.Combine(destDir, "backup.zip");

            using (var fs = File.Create(zipPath))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                if (File.Exists(LocalStore.DbPath))
                    zip.CreateEntryFromFile(LocalStore.DbPath, "simplelotto.db", CompressionLevel.Optimal);

                var entry = zip.CreateEntry("backup-meta.txt", CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open());
                writer.Write($"created_at_utc={DateTime.UtcNow:O}{Environment.NewLine}");
                writer.Write($"store_name={_storeName}{Environment.NewLine}");
                writer.Write($"database={LocalStore.DbPath}{Environment.NewLine}");
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
    private static Brush LowTileBrush => ThemeBrush("SlBinLowBrush", ColorBrush(147, 197, 253));
    private static Brush LowTileStackedBrush => ThemeBrush("SlBinLowStackedBrush", ColorBrush(96, 165, 250));
    private static Brush MediumTileBrush => ThemeBrush("SlBinMediumBrush", ColorBrush(134, 239, 172));
    private static Brush MediumTileStackedBrush => ThemeBrush("SlBinMediumStackedBrush", ColorBrush(74, 222, 128));
    private static Brush HighTileBrush => ThemeBrush("SlBinHighBrush", ColorBrush(255, 153, 51));
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

    private static int PriceCentsForDisplay(long priceCents) =>
        priceCents <= 0
            ? 0
            : priceCents > int.MaxValue
                ? int.MaxValue
                : (int)priceCents;

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
        BinActivity Activity)
    {
        public string BinText => Number.ToString(CultureInfo.InvariantCulture);
        public string GameTextShort => BundleCount == 0 ? string.Empty : CompactGameName(GameName);
        public Visibility GameTextVisibility => BundleCount == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        public Brush BackgroundBrush => BundleCount == 0
            ? EmptyTileBrush
            : Activity switch
            {
                BinActivity.High => BundleCount > 1 ? HighTileStackedBrush : HighTileBrush,
                BinActivity.Medium => BundleCount > 1 ? MediumTileStackedBrush : MediumTileBrush,
                _ => BundleCount > 1 ? LowTileStackedBrush : LowTileBrush
            };
        public Brush BorderBrush => BundleCount == 0
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
                ? new BinCard(number, 0, string.Empty, BinActivity.Low)
                : new BinCard(number, bundleCount, gameName, activity);

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
        bool IsCurrent)
    {
        public string SummaryText => $"{(IsCurrent ? "Current" : "Dormant")} | Game {GameId} | Bundle {BundleId}";
        public string DetailText => $"Bin {Bin} | Current ticket {Ticket}";
        public string StatusText => IsCurrent ? "Current" : "Dormant";
        public string GameIdText => $"Game ID {GameId}";
        public string GameNameText => $"Game Name {GameName}";
        public string BundleText => $"Bundle ID {BundleId}";
        public string TicketText => $"Ticket Number {Ticket}";
        public string BinText => $"Bin {Bin}";
        public Brush CardBackgroundBrush => IsCurrent
            ? MediumTileBrush
            : ThemeBrush("SlSurfaceAltBrush", ColorBrush(247, 248, 250));
        public Brush CardBorderBrush => IsCurrent
            ? MediumTileStackedBrush
            : ThemeBrush("SlBorderBrush", ColorBrush(198, 204, 214));
        public Brush CardForegroundBrush => IsCurrent
            ? DarkTileTextBrush
            : ThemeBrush("SlTextBrush", ColorBrush(21, 23, 26));

        public static BundleDetailLine From(ImportLine line, string gameName, bool isCurrent) =>
            new(line.GameId, gameName, line.BundleId, line.Ticket, line.Bin, isCurrent);
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

    private sealed record GameCatalogRecord(
        string GameId,
        string Name,
        long PriceCents,
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
                "Initial import",
                "ms-appx:///Assets/SimpleLottoLogo64.png",
                "Image not cached");
    }

    private sealed record ClosingBinCard(
        int Number,
        string Status,
        string Detail,
        bool Scanned,
        bool HasBundle)
    {
        public string BinText => Number.ToString(CultureInfo.InvariantCulture);
        public Brush BackgroundBrush => Scanned
            ? MediumTileBrush
            : EmptyTileBrush;
        public Brush BorderBrush => Scanned
            ? MediumTileStackedBrush
            : EmptyTileBorderBrush;
        public Brush ForegroundBrush => DarkTileTextBrush;

        public static ClosingBinCard From(int number, ImportLine? current, bool scanned)
        {
            var detail = current is null
                ? $"Bin {number.ToString(CultureInfo.CurrentCulture)} is empty."
                : $"Bin {number.ToString(CultureInfo.CurrentCulture)}{Environment.NewLine}Expected game ID: {current.GameId}{Environment.NewLine}Bundle ID: {current.BundleId}{Environment.NewLine}Current ticket: {current.Ticket}{Environment.NewLine}Scan status: {(scanned ? "Scanned" : "Needs scan")}";
            var status = scanned
                ? "Scanned"
                : current is null
                    ? "Empty"
                    : "Need scan";
            return new ClosingBinCard(number, status, detail, scanned, current is not null);
        }
    }

    private sealed record ClosingScanRow(string ScannedText, string Status);

    private sealed record ClosingScanIssue(string Title, string Detail);

    private sealed record ClosingScanSale(string BundleKey, SaleLine Sale);

    private sealed record TicketBackfillSale(SaleLine Sale, string NextTicket);

    private sealed record TicketBackfillRange(string SoldTicketText, int Quantity, string NextTicket);

    private sealed record ClosingReportTarget(string BusinessDate, int ShiftSequence, string ShiftLabel, string Folder);

    private static List<ClosingHistoryRow> BuildClosingHistoryRows(IEnumerable<StoredClosingRecord> records) =>
        records
            .OrderByDescending(record => record.ClosedAtUtc)
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
        int ResolvedBundles)
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
            $"{ClosedBundles.ToString(CultureInfo.CurrentCulture)} closed, {CurrentBundles.ToString(CultureInfo.CurrentCulture)} updated, {ResolvedBundles.ToString(CultureInfo.CurrentCulture)} resolved";
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
                record.ResolvedBundles);

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
        string Source = "normal_sale")
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
        string Source)
    {
        public string SummaryText => $"Game {GameId} | Bundle {BundleId} | Ticket {Ticket} | Bin {Bin}";
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

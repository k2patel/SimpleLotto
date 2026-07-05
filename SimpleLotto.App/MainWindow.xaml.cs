using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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
using SimpleLotto.App.Services;
using WinRT.Interop;
using Windows.Graphics;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using Windows.System;
using Windows.UI;

namespace SimpleLotto.App;

public sealed partial class MainWindow : Window
{
    private readonly RdisplayService _rdisplay;
    private readonly ObservableCollection<SaleLine> _sales = new();
    private readonly ObservableCollection<ImportLine> _imports = new();
    private readonly ObservableCollection<ImportBin> _importBins = new();
    private readonly ObservableCollection<BinCard> _binCards = new();
    private readonly ObservableCollection<BundleDetailLine> _selectedBinBundles = new();
    private readonly ObservableCollection<InventoryRecord> _inventoryRecords = new();
    private readonly ObservableCollection<GameCatalogRecord> _gameCatalog = new();
    private readonly ObservableCollection<ClosingBinCard> _closingBinCards = new();
    private readonly List<GameCatalogRecord> _manualGameCatalog = new();
    private bool _isNavCollapsed;
    private StartupStage _startupStage = StartupStage.Setup;
    private string _managerPassword = string.Empty;
    private string _clerkName = string.Empty;
    private string _clerkPassword = string.Empty;
    private string _storeState = string.Empty;
    private string? _storeBarcodeLayout;
    private string _storeName = string.Empty;
    private string _storeStreet = string.Empty;
    private string _storeCity = string.Empty;
    private int _configuredBinCount = 90;
    private ImportBin? _pendingImportBin;
    private ImportTicket? _pendingImportTicket;
    private bool _hasImportFailure;
    private readonly StringBuilder _startupScanBuffer = new();
    private readonly StringBuilder _focusedScanBuffer = new();
    private string _dashboardPendingBin = string.Empty;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    public MainWindow(RdisplayService rdisplay)
    {
        _rdisplay = rdisplay;
        InitializeComponent();
        Title = "SimpleLotto";
        ResizeWindow(1240, 760);
        PopulateStateComboBox();
        SalesListView.ItemsSource = _sales;
        ImportListView.ItemsSource = _imports;
        ImportBinsGridView.ItemsSource = _importBins;
        BinsGridView.ItemsSource = _binCards;
        BinBundlesListView.ItemsSource = _selectedBinBundles;
        InventoryListView.ItemsSource = _inventoryRecords;
        GameCatalogListView.ItemsSource = _gameCatalog;
        ClosingBinsGridView.ItemsSource = _closingBinCards;
        RootGrid.AddHandler(
            UIElement.KeyDownEvent,
            new KeyEventHandler(OnGlobalKeyDown),
            handledEventsToo: true);
        RefreshTotals();
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
        _managerPassword = ManagerPasswordBox.Password;
        _clerkName = ClerkNameBox.Text.Trim();
        _clerkPassword = ClerkPasswordBox.Password;
        BuildImportBins();
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
        var expectedPassword = user == "Manager" ? _managerPassword : _clerkPassword;
        if (string.IsNullOrWhiteSpace(expectedPassword) ||
            LoginPasswordBox.Password != expectedPassword)
        {
            StartupStatusText.Text = "Password does not match the selected user.";
            return;
        }

        StartupOverlay.Visibility = Visibility.Collapsed;
        StatusText.Text = $"{user} logged in. Shift started for {_storeState}.";
        DashboardScannerModeText.Text = "Focused capture";
        DashboardScannerStatusText.Text = "Ready for scanner input.";
        DashboardPairingStatusText.Text = "Background capture: not paired";
        RefreshOperationalPages();
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

    private void ShowLoginStage()
    {
        _startupStage = StartupStage.Login;
        StartupSubtitleText.Text = "Login";
        SetupPanel.Visibility = Visibility.Collapsed;
        ImportPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
        StartupBackButton.Visibility = Visibility.Visible;
        StartupPrimaryButton.Content = "Start Shift";
        LoginUserComboBox.Items.Clear();
        LoginUserComboBox.Items.Add(new ComboBoxItem { Content = "Manager" });
        if (!string.IsNullOrWhiteSpace(_clerkName) &&
            !string.IsNullOrWhiteSpace(_clerkPassword))
        {
            LoginUserComboBox.Items.Add(new ComboBoxItem { Content = _clerkName });
        }
        LoginUserComboBox.SelectedIndex = 0;
        LoginPasswordBox.Password = string.Empty;
        StartupStatusText.Text = "Login starts the first active shift.";
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

        ShowLoginStage();
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

    private void BuildImportBins()
    {
        _importBins.Clear();
        for (var i = 1; i <= _configuredBinCount; i++)
            _importBins.Add(new ImportBin(i));

        _imports.Clear();
        _pendingImportBin = null;
        _pendingImportTicket = null;
        ClearImportFailure();
        ImportBinCountText.Text = $"{_configuredBinCount} bin{(_configuredBinCount == 1 ? string.Empty : "s")} ready";
        ImportPendingText.Text = "No pending scan.";
        ImportScanStatusText.Text = "Scan BIN barcode and ticket barcode in either order.";
        RefreshOperationalPages();
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
        if (TryParseBinNumber(raw, out var binNumber))
        {
            _dashboardPendingBin = binNumber.ToString(CultureInfo.InvariantCulture);
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

        var bin = string.IsNullOrWhiteSpace(_dashboardPendingBin)
            ? "Scanner"
            : _dashboardPendingBin;
        var line = new SaleLine(
            DateTime.Now,
            ticket.GameId,
            bin,
            ticket.Ticket,
            1,
            0);

        _sales.Insert(0, line);
        SalesListView.SelectedItem = line;
        DashboardScannerStatusText.Text = $"Ticket captured for game {ticket.GameId}.";
        DashboardLastScanText.Text = $"Game {ticket.GameId} | Bundle {ticket.BundleId} | Ticket {ticket.Ticket} | Bin {bin}";
        StatusText.Text = $"Scanner sale captured for game {ticket.GameId}, ticket {ticket.Ticket}.";
        _dashboardPendingBin = string.Empty;
        RefreshTotals();
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
        var line = new ImportLine(ticket.GameId, ticket.BundleId, ticket.Ticket, bin.Number.ToString(CultureInfo.InvariantCulture));
        _imports.Insert(0, line);
        bin.ImportedCount++;

        _pendingImportBin = null;
        _pendingImportTicket = null;
        ClearImportFailure();
        UpdatePendingImportText();
        UpdateImportStatusText($"Success. Imported game {ticket.GameId}, bundle {ticket.BundleId}, ticket {ticket.Ticket} in bin {bin.Number}.");
        ImportScanStatusText.Text = $"Imported bin {bin.Number}: game {ticket.GameId}, bundle {ticket.BundleId}, ticket {ticket.Ticket}.";
        _ = SpeakAsync($"Success. Bin {bin.Number} imported.");
        RefreshOperationalPages();
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
        try
        {
            using var synth = new SpeechSynthesizer();
            var stream = await synth.SynthesizeTextToStreamAsync(text);
            var player = new MediaPlayer
            {
                Source = MediaSource.CreateFromStream(stream, stream.ContentType),
                AutoPlay = false,
                Volume = 1.0
            };

            var done = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Ended(MediaPlayer sender, object args) => done.TrySetResult(null);
            void Failed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => done.TrySetResult(null);

            player.MediaEnded += Ended;
            player.MediaFailed += Failed;
            try
            {
                player.Play();
                await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(4)));
            }
            finally
            {
                player.MediaEnded -= Ended;
                player.MediaFailed -= Failed;
                player.Dispose();
                stream.Dispose();
            }
        }
        catch
        {
            StatusText.Text = text;
        }
    }

    private void NavToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _isNavCollapsed = !_isNavCollapsed;
        NavColumn.Width = new GridLength(_isNavCollapsed ? 64 : 220);
        NavToggleButton.Content = _isNavCollapsed ? "->" : "<-";

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

        _sales.Remove(sale);
        StatusText.Text = $"Voided game {sale.GameId} sale for {sale.AmountText}.";
        RefreshTotals();
    }

    private async void CloseShiftButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sales.Count == 0)
        {
            StatusText.Text = "Nothing to reset.";
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Close the current shift?",
            Content = $"This will clear {_sales.Count} local prototype sale entry(s).",
            PrimaryButtonText = "Close Shift",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
            return;

        _sales.Clear();
        StatusText.Text = "Shift closed.";
        RefreshTotals();
    }

    private void RefreshTotals()
    {
        var salesCount = _sales.Count;
        var ticketCount = _sales.Sum(s => s.Quantity);
        var revenue = _sales.Sum(s => s.Amount);
        var average = ticketCount == 0 ? 0 : revenue / ticketCount;

        SalesSubtitleText.Text = salesCount == 0
            ? "No entries yet"
            : $"{salesCount} sale entr{(salesCount == 1 ? "y" : "ies")}";
        RevenueText.Text = revenue.ToString("C", CultureInfo.CurrentCulture);
        TicketsText.Text = ticketCount.ToString(CultureInfo.CurrentCulture);
        AverageText.Text = average.ToString("C", CultureInfo.CurrentCulture);
        GameMixText.Text = BuildGameMixText();
        ClosingSalesText.Text = revenue.ToString("C", CultureInfo.CurrentCulture);
        ClosingTicketsText.Text = ticketCount.ToString(CultureInfo.CurrentCulture);
    }

    private void RefreshOperationalPages()
    {
        RefreshBinCards();
        RefreshInventoryRecords();
        RefreshGameCatalog();
        SyncRdisplayTiles();
        RefreshClosingBins();
        RefreshSettingsSummary();
    }

    private void RefreshBinCards()
    {
        _binCards.Clear();
        var grouped = _imports
            .GroupBy(i => i.Bin)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        for (var i = 1; i <= _configuredBinCount; i++)
        {
            var bin = i.ToString(CultureInfo.InvariantCulture);
            grouped.TryGetValue(bin, out var bundles);
            var current = bundles?.FirstOrDefault();
            _binCards.Add(BinCard.From(i, current, bundles?.Count ?? 0));
        }

        var activeBins = _binCards.Count(b => b.BundleCount > 0);
        BinsTotalText.Text = _configuredBinCount.ToString(CultureInfo.CurrentCulture);
        BinsActiveText.Text = activeBins.ToString(CultureInfo.CurrentCulture);
        BinsBundleText.Text = _imports.Count.ToString(CultureInfo.CurrentCulture);

        if (_selectedBinBundles.Count == 0)
            BinDetailText.Text = activeBins == 0
                ? "No bundles imported yet."
                : "Select a bin to view current and dormant bundle records.";
    }

    private void RefreshInventoryRecords()
    {
        _inventoryRecords.Clear();
        foreach (var line in _imports)
            _inventoryRecords.Add(new InventoryRecord("Initial", line.GameId, line.BundleId, line.Ticket, $"Placed in bin {line.Bin}"));

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

        InventoryGameCountText.Text = $"{_gameCatalog.Count.ToString(CultureInfo.CurrentCulture)} game{(_gameCatalog.Count == 1 ? string.Empty : "s")} defined";
    }

    private void SyncRdisplayTiles()
    {
        var gameNames = _gameCatalog.ToDictionary(
            g => g.GameId,
            g => g.Name,
            StringComparer.OrdinalIgnoreCase);

        var tiles = _imports
            .GroupBy(i => i.Bin, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Bin = int.TryParse(g.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0,
                Current = g.First()
            })
            .Where(x => x.Bin > 0)
            .Select(x => new RdisplayTileState(
                x.Bin,
                x.Current.GameId,
                gameNames.TryGetValue(x.Current.GameId, out var name) ? name : $"Game {x.Current.GameId}",
                x.Current.Ticket,
                PriceCents: 0));

        _rdisplay.UpdateTiles(tiles);
    }

    private void RefreshClosingBins()
    {
        _closingBinCards.Clear();
        var grouped = _imports
            .GroupBy(i => i.Bin)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        for (var i = 1; i <= _configuredBinCount; i++)
        {
            var bin = i.ToString(CultureInfo.InvariantCulture);
            grouped.TryGetValue(bin, out var bundles);
            _closingBinCards.Add(ClosingBinCard.From(i, bundles?.FirstOrDefault(), scanned: false));
        }

        ClosingEvidenceText.Text = $"0 / {_configuredBinCount.ToString(CultureInfo.CurrentCulture)}";
    }

    private void RefreshSettingsSummary()
    {
        SettingsStoreText.Text = string.IsNullOrWhiteSpace(_storeName)
            ? "Store setup not completed."
            : $"{_storeName}{Environment.NewLine}{_storeStreet}, {_storeCity}";
        SettingsStateText.Text = string.IsNullOrWhiteSpace(_storeState)
            ? "State: not selected"
            : $"State: {_storeState}";
        SettingsBarcodeText.Text = string.IsNullOrWhiteSpace(_storeBarcodeLayout)
            ? "Barcode format: not selected"
            : $"Barcode format: {_storeBarcodeLayout}";
        SettingsScannerText.Text = "Scanner: focused capture only";
        var registered = _rdisplay.Displays.Count(d => d.IsRegistered);
        DisplayStatusText.Text = registered == 0
            ? $"Rdisplay API listening on port {RdisplayService.ApiPort}. No display registered."
            : $"Rdisplay API listening on port {RdisplayService.ApiPort}. {registered} display{(registered == 1 ? string.Empty : "s")} registered.";
    }

    private void BinsGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not BinCard card)
            return;

        _selectedBinBundles.Clear();
        var lines = _imports
            .Where(i => string.Equals(i.Bin, card.Number.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (lines.Count == 0)
        {
            BinDetailText.Text = $"Bin {card.Number} is empty.";
            return;
        }

        BinDetailText.Text = lines.Count == 1
            ? $"Bin {card.Number} has one bundle."
            : $"Bin {card.Number} has {lines.Count} bundles. The latest scan is current; older bundles are dormant.";

        for (var i = 0; i < lines.Count; i++)
            _selectedBinBundles.Add(BundleDetailLine.From(lines[i], i == 0));
    }

    private async void StartClosingScanButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Shift Closing Scan",
            Content = "Closing scan collection is the next workflow wiring step. This page now shows all bins so scan evidence can mark bins green and leave missed bins gray.",
            PrimaryButtonText = "OK",
            DefaultButton = ContentDialogButton.Primary
        };

        await dialog.ShowAsync();
        ClosingStatusText.Text = "Closing scan route is not active yet.";
    }

    private async void AddGameButton_Click(object sender, RoutedEventArgs e)
    {
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
        var content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                gameIdBox,
                nameBox
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
        var existing = _manualGameCatalog.FindIndex(g => string.Equals(g.GameId, gameId, StringComparison.OrdinalIgnoreCase));
        var record = new GameCatalogRecord(
            gameId,
            name,
            "Manual",
            "ms-appx:///Assets/SimpleLottoLogo64.png",
            "Image not uploaded");

        if (existing >= 0)
            _manualGameCatalog[existing] = record;
        else
            _manualGameCatalog.Add(record);

        RefreshGameCatalog();
        SyncRdisplayTiles();
        GameCatalogStatusText.Text = $"Game {gameId} added.";
    }

    private void UploadGameImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (GameCatalogListView.SelectedItem is not GameCatalogRecord game)
        {
            GameCatalogStatusText.Text = "Select a game before uploading an image.";
            return;
        }

        GameCatalogStatusText.Text = $"Image upload for game {game.GameId} will use the WindowsPOS crop/upload flow.";
    }

    private void FetchMissingImagesButton_Click(object sender, RoutedEventArgs e)
    {
        GameCatalogStatusText.Text = "Missing image fetch will run cache-first during receiving and activation.";
    }

    private async void RegisterDisplayButton_Click(object sender, RoutedEventArgs e)
    {
        var host = DisplayHostBox.Text.Trim();
        var port = CoerceInt(DisplayPortBox.Value, 5001);
        DisplayStatusText.Text = "Registering Rdisplay...";

        var result = await _rdisplay.RegisterAsync(host, port);
        if (!result.IsSuccess || result.Display is null)
        {
            DisplayStatusText.Text = result.ErrorMessage ?? "Rdisplay registration failed.";
            return;
        }

        DisplayStatusText.Text = $"Registered {result.Display.Name} at {result.Display.BaseUrl}.";
        SyncRdisplayTiles();
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

    private static decimal CoerceMoney(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 0;

        return Math.Max(0, Math.Round((decimal)value, 2, MidpointRounding.AwayFromZero));
    }

    private static SolidColorBrush ColorBrush(byte r, byte g, byte b) =>
        new(Color.FromArgb(255, r, g, b));

    private static SolidColorBrush EmptyTileBrush => ColorBrush(34, 37, 43);
    private static SolidColorBrush EmptyTileBorderBrush => ColorBrush(70, 76, 87);
    private static SolidColorBrush BhagvaTileBrush => ColorBrush(255, 153, 51);
    private static SolidColorBrush BhagvaStackedTileBrush => ColorBrush(124, 45, 18);
    private static SolidColorBrush BhagvaBorderBrush => ColorBrush(255, 178, 92);
    private static SolidColorBrush DarkTileTextBrush => ColorBrush(21, 23, 26);
    private static SolidColorBrush LightTileTextBrush => ColorBrush(255, 255, 255);

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
        string GameId,
        string BundleId,
        string Ticket)
    {
        public string BinText => $"Bin {Number}";
        public string GameText => string.IsNullOrWhiteSpace(GameId) ? "Empty" : $"Game {GameId}";
        public string TicketText => string.IsNullOrWhiteSpace(Ticket) ? "No ticket" : $"Ticket {Ticket}";
        public string StackText => BundleCount switch
        {
            0 => "Open",
            1 => "Current",
            _ => $"{BundleCount} bundles"
        };
        public Brush BackgroundBrush => BundleCount switch
        {
            0 => EmptyTileBrush,
            1 => BhagvaTileBrush,
            _ => BhagvaStackedTileBrush
        };
        public Brush BorderBrush => BundleCount == 0
            ? EmptyTileBorderBrush
            : BhagvaBorderBrush;
        public Brush ForegroundBrush => BundleCount == 1
            ? DarkTileTextBrush
            : LightTileTextBrush;

        public static BinCard From(int number, ImportLine? current, int bundleCount) =>
            current is null
                ? new BinCard(number, 0, string.Empty, string.Empty, string.Empty)
                : new BinCard(number, bundleCount, current.GameId, current.BundleId, current.Ticket);
    }

    private sealed record BundleDetailLine(
        string GameId,
        string BundleId,
        string Ticket,
        string Bin,
        bool IsCurrent)
    {
        public string SummaryText => $"{(IsCurrent ? "Current" : "Dormant")} | Game {GameId} | Bundle {BundleId}";
        public string DetailText => $"Bin {Bin} | Current ticket {Ticket}";

        public static BundleDetailLine From(ImportLine line, bool isCurrent) =>
            new(line.GameId, line.BundleId, line.Ticket, line.Bin, isCurrent);
    }

    private sealed record InventoryRecord(
        string Source,
        string GameId,
        string BundleId,
        string Ticket,
        string Status)
    {
        public string GameText => $"Game {GameId}";
        public string BundleText => $"Bundle {BundleId}";
    }

    private sealed record GameCatalogRecord(
        string GameId,
        string Name,
        string Source,
        string ImageUri,
        string ImageStatus)
    {
        public BitmapImage ImageSource => new(new Uri(ImageUri));

        public static GameCatalogRecord FromImport(string gameId) =>
            new(
                gameId,
                $"Game {gameId}",
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
        public string BinText => $"Bin {Number}";
        public Brush BackgroundBrush => Scanned
            ? BhagvaTileBrush
            : EmptyTileBrush;
        public Brush BorderBrush => Scanned
            ? BhagvaBorderBrush
            : EmptyTileBorderBrush;
        public Brush ForegroundBrush => Scanned
            ? DarkTileTextBrush
            : LightTileTextBrush;

        public static ClosingBinCard From(int number, ImportLine? current, bool scanned)
        {
            var detail = current is null
                ? "Empty"
                : $"G{current.GameId} T{current.Ticket}";
            var status = scanned
                ? "Scanned"
                : "Unscanned";
            return new ClosingBinCard(number, status, detail, scanned, current is not null);
        }
    }

    private sealed record SaleLine(
        DateTime SoldAt,
        string GameId,
        string Bin,
        string Ticket,
        int Quantity,
        decimal Amount)
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
        string Bin)
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
        public Brush StatusTextBrush => ImportedCount == 0
            ? LightTileTextBrush
            : DarkTileTextBrush;

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

    private enum StartupStage
    {
        Setup,
        Import,
        Login
    }
}

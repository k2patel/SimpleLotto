using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;
using Windows.Graphics;

namespace SimpleLotto.App;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<SaleLine> _sales = new();
    private bool _isNavCollapsed;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    public MainWindow()
    {
        InitializeComponent();
        Title = "SimpleLotto";
        ResizeWindow(1240, 760);
        SalesListView.ItemsSource = _sales;
        RefreshTotals();
    }

    private void NavToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _isNavCollapsed = !_isNavCollapsed;
        NavColumn.Width = new GridLength(_isNavCollapsed ? 64 : 220);
        NavToggleButton.Content = _isNavCollapsed ? "Nav" : "Menu";

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

        DashboardContent.Visibility = Visibility.Collapsed;
        SectionContent.Visibility = Visibility.Visible;
        SetSelectedNav(section);
        SectionTitleText.Text = section;
        SectionDescriptionText.Text = section switch
        {
            "Bins" => "Bin display and bundle placement will use the product rules for active and dormant bundles.",
            "Inventory" => "Inventory will handle receiving, bundle setup, and activation without becoming the sales ledger.",
            "Closing" => "Closing will reconcile the current shift while keeping sales totals separate from inventory state.",
            "Settings" => "Settings will hold state setup, game prices, bundle prices, ticket numbering, displays, scanner, and audio options.",
            _ => "This section is defined by the product instructions."
        };
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

    private void AddSaleButton_Click(object sender, RoutedEventArgs e)
    {
        var gameId = string.IsNullOrWhiteSpace(GameIdTextBox.Text)
            ? "Unknown"
            : GameIdTextBox.Text.Trim();
        var bin = string.IsNullOrWhiteSpace(BinTextBox.Text)
            ? "Unassigned"
            : BinTextBox.Text.Trim();
        var quantity = CoerceInt(QuantityBox.Value, 1);
        var amount = CoerceMoney(PriceBox.Value);
        var ticket = string.IsNullOrWhiteSpace(TicketTextBox.Text)
            ? "No ticket entered"
            : TicketTextBox.Text.Trim();

        var line = new SaleLine(
            DateTime.Now,
            gameId,
            bin,
            ticket,
            quantity,
            amount);

        _sales.Insert(0, line);
        GameIdTextBox.Text = string.Empty;
        BinTextBox.Text = string.Empty;
        TicketTextBox.Text = string.Empty;
        QuantityBox.Value = 1;
        PriceBox.Value = 0;
        SalesListView.SelectedItem = line;
        StatusText.Text = $"Added {quantity} ticket(s) for game {gameId}, bin {bin}.";
        RefreshTotals();
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

    private void ResizeWindow(int widthDip, int heightDip)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new SizeInt32(
            (int)(widthDip * scale),
            (int)(heightDip * scale)));
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
}

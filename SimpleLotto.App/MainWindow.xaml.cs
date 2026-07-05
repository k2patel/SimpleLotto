using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;
using Windows.Graphics;

namespace SimpleLotto.App;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<SaleLine> _sales = new();

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

    private void AddSaleButton_Click(object sender, RoutedEventArgs e)
    {
        var game = SelectedGame();
        var quantity = CoerceInt(QuantityBox.Value, 1);
        var unitPrice = CoerceMoney(PriceBox.Value);
        var ticket = string.IsNullOrWhiteSpace(TicketTextBox.Text)
            ? "Walk-up sale"
            : TicketTextBox.Text.Trim();

        var line = new SaleLine(
            DateTime.Now,
            game,
            ticket,
            quantity,
            unitPrice);

        _sales.Insert(0, line);
        TicketTextBox.Text = string.Empty;
        QuantityBox.Value = 1;
        SalesListView.SelectedItem = line;
        StatusText.Text = $"Added {quantity} {game} ticket(s) for {line.AmountText}.";
        RefreshTotals();
    }

    private void QuickPickButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
            return;

        var parts = tag.Split('|');
        if (parts.Length != 2 || !double.TryParse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var price))
            return;

        SelectGame(parts[0]);
        PriceBox.Value = price;
        QuantityBox.Value = 1;
        StatusText.Text = $"{parts[0]} quick pick selected.";
    }

    private void VoidSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (SalesListView.SelectedItem is not SaleLine sale)
        {
            StatusText.Text = "Select a sale to void.";
            return;
        }

        _sales.Remove(sale);
        StatusText.Text = $"Voided {sale.Game} sale for {sale.AmountText}.";
        RefreshTotals();
    }

    private async void ResetDayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sales.Count == 0)
        {
            StatusText.Text = "Nothing to reset.";
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Reset the current day?",
            Content = $"This will remove {_sales.Count} sale entry(s) from the local list.",
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
            return;

        _sales.Clear();
        StatusText.Text = "Day reset.";
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
                .GroupBy(s => s.Game)
                .OrderByDescending(g => g.Sum(s => s.Amount))
                .ThenBy(g => g.Key)
                .Select(g =>
                    $"{g.Key}: {g.Sum(s => s.Quantity)} ticket(s), {g.Sum(s => s.Amount).ToString("C", CultureInfo.CurrentCulture)}"));
    }

    private string SelectedGame()
    {
        if (GameComboBox.SelectedItem is ComboBoxItem item &&
            item.Content is string game &&
            !string.IsNullOrWhiteSpace(game))
        {
            return game;
        }

        return "Other";
    }

    private void SelectGame(string game)
    {
        foreach (var item in GameComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), game, StringComparison.OrdinalIgnoreCase))
            {
                GameComboBox.SelectedItem = item;
                return;
            }
        }
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
        string Game,
        string Ticket,
        int Quantity,
        decimal UnitPrice)
    {
        public decimal Amount => Quantity * UnitPrice;
        public string TimeText => SoldAt.ToString("h:mm tt", CultureInfo.CurrentCulture);
        public string AmountText => Amount.ToString("C", CultureInfo.CurrentCulture);
    }
}

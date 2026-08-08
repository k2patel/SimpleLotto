using System;
using System.Globalization;
using System.Linq;

namespace SimpleLotto.App.Services;

internal static class TicketSerialFormatter
{
    private const int BarcodeSerialWidth = 3;

    public static int GetWidth(string? ticket)
    {
        var width = BarcodeSerialWidth;
        foreach (var part in (ticket ?? string.Empty).Split(
                     '-',
                     StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParse(part, out var serial))
            {
                width = Math.Max(
                    width,
                    serial.ToString(CultureInfo.InvariantCulture).Length);
                continue;
            }

            width = Math.Max(width, part.Count(char.IsAsciiDigit));
        }

        return width;
    }

    public static string Format(int serial, int width) =>
        serial.ToString(
            "D" + Math.Max(BarcodeSerialWidth, width).ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture);

    public static string NormalizeSingle(string ticket)
    {
        var value = (ticket ?? string.Empty).Trim();
        return TryParse(value, out var serial)
            ? Format(serial, GetWidth(value))
            : value;
    }

    private static bool TryParse(string value, out int serial)
    {
        serial = 0;
        var ticket = (value ?? string.Empty).Trim();
        return ticket.Length > 0 &&
            ticket.All(char.IsAsciiDigit) &&
            int.TryParse(ticket, NumberStyles.None, CultureInfo.InvariantCulture, out serial);
    }
}

using System;
using System.Globalization;

namespace SimpleLotto.App;

internal static class OperatorInputGuard
{
    public const long MaximumManualMoneyCents = 999_999_999;
    public const int MaximumTicketPriceDollars = 300;
    public const int MaximumNumericTextLength = 32;

    public static bool TryReadWholeNumber(double value, int minimum, int maximum, out int result)
    {
        result = 0;
        if (minimum > maximum ||
            double.IsNaN(value) ||
            double.IsInfinity(value) ||
            value < minimum ||
            value > maximum ||
            value != Math.Truncate(value))
        {
            return false;
        }

        result = (int)value;
        return true;
    }

    public static bool TryReadMoneyCents(
        string? text,
        double value,
        long minimumCents,
        long maximumCents,
        out long cents)
    {
        cents = 0;
        if (minimumCents < 0 || maximumCents < minimumCents)
            return false;

        var raw = text?.Trim() ?? string.Empty;
        if (raw.Length > MaximumNumericTextLength)
            return false;

        if (raw.Length > 0)
        {
            if (!TryParseDecimal(raw, out var parsed))
                return false;
            return TryConvertDecimalToCents(parsed, minimumCents, maximumCents, out cents);
        }

        if (double.IsNaN(value) ||
            double.IsInfinity(value) ||
            value < minimumCents / 100d ||
            value > maximumCents / 100d)
        {
            return false;
        }

        var scaled = Math.Round(value * 100d, MidpointRounding.AwayFromZero);
        if (scaled < minimumCents || scaled > maximumCents)
            return false;

        cents = (long)scaled;
        return true;
    }

    public static bool IsPermittedNumberBoxText(
        string? text,
        double minimum,
        double maximum,
        int decimalPlaces)
    {
        var raw = text?.Trim() ?? string.Empty;
        if (raw.Length == 0)
            return true;
        if (raw.Length > MaximumNumericTextLength || decimalPlaces < 0)
            return false;

        if (!TryParseDecimal(raw, out var parsed))
            return IsIncompleteNumericEntry(raw, decimalPlaces);

        if (parsed < (decimal)minimum || parsed > (decimal)maximum)
            return false;

        return decimal.Round(parsed, decimalPlaces, MidpointRounding.AwayFromZero) == parsed;
    }

    public static bool TryCalculateExpectedCashCents(
        long instantTicketSalesCents,
        long onlineSaleCents,
        long instantCashoutCents,
        long onlineCashoutCents,
        out long expectedCashCents)
    {
        try
        {
            expectedCashCents = checked(
                instantTicketSalesCents +
                onlineSaleCents -
                instantCashoutCents -
                onlineCashoutCents);
            return true;
        }
        catch (OverflowException)
        {
            expectedCashCents = 0;
            return false;
        }
    }

    public static bool TryConvertAmountToCents(decimal amount, out long cents)
    {
        cents = 0;
        decimal scaled;
        try
        {
            scaled = checked(decimal.Round(
                amount * 100m,
                0,
                MidpointRounding.AwayFromZero));
        }
        catch (OverflowException)
        {
            return false;
        }

        if (scaled < long.MinValue || scaled > long.MaxValue)
            return false;

        cents = decimal.ToInt64(scaled);
        return true;
    }

    private static bool TryConvertDecimalToCents(
        decimal value,
        long minimumCents,
        long maximumCents,
        out long cents)
    {
        cents = 0;
        if (value < minimumCents / 100m || value > maximumCents / 100m)
            return false;

        decimal scaled;
        try
        {
            scaled = checked(value * 100m);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (decimal.Truncate(scaled) != scaled ||
            scaled < minimumCents ||
            scaled > maximumCents)
        {
            return false;
        }

        cents = decimal.ToInt64(scaled);
        return true;
    }

    private static bool TryParseDecimal(string raw, out decimal value) =>
        decimal.TryParse(raw, NumberStyles.Number, CultureInfo.CurrentCulture, out value) ||
        decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    private static bool IsIncompleteNumericEntry(string raw, int decimalPlaces)
    {
        if (raw is "-" or "+")
            return true;

        var decimalSeparator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        return decimalPlaces > 0 &&
            (raw.EndsWith(decimalSeparator, StringComparison.Ordinal) ||
             (!string.Equals(decimalSeparator, ".", StringComparison.Ordinal) &&
              raw.EndsWith(".", StringComparison.Ordinal)));
    }
}

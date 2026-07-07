using System;

namespace SimpleLotto.App.Models;

public sealed class HidDeviceInfo
{
    public string Vid { get; init; } = string.Empty;

    public string Pid { get; init; } = string.Empty;

    public string Serial { get; init; } = string.Empty;

    public string Product { get; init; } = string.Empty;

    public string Manufacturer { get; init; } = string.Empty;

    public string DevicePath { get; init; } = string.Empty;

    public bool MatchesIdentity(string vid, string pid, string? serial)
    {
        if (!string.Equals(vid, Vid, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(pid, Pid, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(serial) && !string.IsNullOrEmpty(Serial))
            return string.Equals(serial, Serial, StringComparison.OrdinalIgnoreCase);
        return true;
    }

    public string DisplayLabel
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Product))
                return Product;
            if (!string.IsNullOrWhiteSpace(Manufacturer))
                return $"{Manufacturer} ({Vid}/{Pid})";
            return $"VID {Vid} / PID {Pid}";
        }
    }
}

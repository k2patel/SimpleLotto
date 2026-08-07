namespace SimpleLotto.App;

internal enum ActivationTicketPlacementKind
{
    FirstTicket,
    GapFill,
    PackagedLastTicket
}

internal readonly record struct ActivationTicketPlacement(
    ActivationTicketPlacementKind Kind,
    int CurrentAvailableSerial,
    int FirstSoldSerial,
    int LastSoldSerial)
{
    public bool HasGapFill => Kind == ActivationTicketPlacementKind.GapFill;

    public int Quantity => HasGapFill
        ? checked(LastSoldSerial - FirstSoldSerial + 1)
        : 0;

    public static bool TryCreate(
        int scannedSerial,
        int firstTicketSerial,
        int lastTicketSerial,
        out ActivationTicketPlacement placement,
        out string error)
    {
        placement = default;
        error = string.Empty;
        if (firstTicketSerial < 0 || lastTicketSerial < firstTicketSerial)
        {
            error = "The configured bundle range is invalid.";
            return false;
        }

        if (scannedSerial < firstTicketSerial || scannedSerial > lastTicketSerial)
        {
            error = "The scanned ticket is outside the configured bundle range.";
            return false;
        }

        if (scannedSerial == firstTicketSerial)
        {
            placement = new ActivationTicketPlacement(
                ActivationTicketPlacementKind.FirstTicket,
                firstTicketSerial,
                firstTicketSerial,
                firstTicketSerial);
            return true;
        }

        if (scannedSerial == lastTicketSerial)
        {
            placement = new ActivationTicketPlacement(
                ActivationTicketPlacementKind.PackagedLastTicket,
                firstTicketSerial,
                firstTicketSerial,
                firstTicketSerial);
            return true;
        }

        placement = new ActivationTicketPlacement(
            ActivationTicketPlacementKind.GapFill,
            scannedSerial,
            firstTicketSerial,
            scannedSerial - 1);
        return true;
    }
}

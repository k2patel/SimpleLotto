namespace SimpleLotto.App;

internal enum ClosingTicketReconciliationKind
{
    Match,
    ForwardSale,
    BackwardCorrection
}

internal readonly record struct ClosingTicketReconciliation(
    ClosingTicketReconciliationKind Kind,
    int FirstAffectedSerial,
    int LastAffectedSerial)
{
    public int Quantity => Kind == ClosingTicketReconciliationKind.Match
        ? 0
        : checked(LastAffectedSerial - FirstAffectedSerial + 1);

    public static bool TryCreate(
        int storedCurrentSerial,
        int scannedAvailableSerial,
        int firstTicketSerial,
        int lastTicketSerial,
        bool isSoldOut,
        out ClosingTicketReconciliation reconciliation,
        out string error)
    {
        reconciliation = default;
        error = string.Empty;
        if (firstTicketSerial < 0 || lastTicketSerial < firstTicketSerial)
        {
            error = "The configured bundle range is invalid.";
            return false;
        }

        if (storedCurrentSerial < firstTicketSerial || storedCurrentSerial > lastTicketSerial)
        {
            error = "The stored current ticket is outside the configured bundle range.";
            return false;
        }

        if (scannedAvailableSerial < firstTicketSerial || scannedAvailableSerial > lastTicketSerial)
        {
            error = "The scanned available ticket is outside the configured bundle range.";
            return false;
        }

        var effectiveCurrentSerial = isSoldOut
            ? (long)lastTicketSerial + 1
            : storedCurrentSerial;
        if (scannedAvailableSerial == effectiveCurrentSerial)
        {
            reconciliation = new ClosingTicketReconciliation(
                ClosingTicketReconciliationKind.Match,
                scannedAvailableSerial,
                scannedAvailableSerial);
            return true;
        }

        if (scannedAvailableSerial > effectiveCurrentSerial)
        {
            reconciliation = new ClosingTicketReconciliation(
                ClosingTicketReconciliationKind.ForwardSale,
                checked((int)effectiveCurrentSerial),
                scannedAvailableSerial - 1);
            return true;
        }

        reconciliation = new ClosingTicketReconciliation(
            ClosingTicketReconciliationKind.BackwardCorrection,
            scannedAvailableSerial,
            checked((int)(effectiveCurrentSerial - 1)));
        return true;
    }
}

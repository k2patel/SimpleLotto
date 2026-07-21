using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SimpleLotto.App.Services;

public sealed class LocalStore
{
    public const int RecentAuditLogLimit = 200;

    private const int SchemaVersion = 18;
    private const string SystemActorId = "system";
    private const string LegacyActorId = "legacy-migration";
    private static readonly object SchemaLock = new();
    private static readonly HashSet<string> SchemaReadyPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _databasePath;

    public LocalStore(string? databasePath = null)
    {
        _databasePath = string.IsNullOrWhiteSpace(databasePath)
            ? DbPath
            : Path.GetFullPath(databasePath);
        var dataFolder = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(dataFolder))
            Directory.CreateDirectory(dataFolder);
    }

    public string DatabasePath => _databasePath;

    public static string DbPath
    {
        get
        {
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SimpleLotto");
            Directory.CreateDirectory(dataDir);
            return Path.Combine(dataDir, "simplelotto.db");
        }
    }

    public SqliteConnection Open()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };

        var conn = new SqliteConnection(builder.ToString());
        conn.Open();
        EnsureSchema(conn, DatabasePath);
        return conn;
    }

    public PersistedState Load()
    {
        using var conn = Open();
        var state = new PersistedState();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT key, value FROM settings";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                state.Settings[reader.GetString(0)] = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            }
        }

        state.Imports.AddRange(QueryImports(conn));
        state.ReceivedBundles.AddRange(QueryReceivedBundles(conn));
        state.Activations.AddRange(QueryActivations(conn));
        state.Sales.AddRange(QuerySales(conn));
        state.VoidedSaleKeys.AddRange(QueryVoidedSaleKeys(conn));
        state.VoidedSaleIds.AddRange(QueryVoidedSaleIds(conn));
        state.ManualGames.AddRange(QueryManualGames(conn));
        state.RdisplayDisplays.AddRange(QueryRdisplayDisplays(conn));
        state.ClosingHistory.AddRange(QueryClosingHistory(conn));
        state.PendingClosingReports.AddRange(QueryPendingClosingReports(conn));
        state.LedgerMigrationConflicts.AddRange(QueryLedgerMigrationConflicts(conn));
        state.AuditLog.AddRange(QueryAuditLog(conn));
        state.OpenIntervalId = QueryOpenIntervalId(conn);
        return state;
    }

    public void SaveSetup(StoreSetup setup)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var managerActorId = string.IsNullOrWhiteSpace(setup.ManagerActorId)
            ? GetOrCreateActorSetting(conn, tx, "manager_actor_id", "Manager", "manager")
            : setup.ManagerActorId;
        var clerkActorId = string.IsNullOrWhiteSpace(setup.ClerkActorId)
            ? GetOrCreateActorSetting(conn, tx, "clerk_actor_id", string.IsNullOrWhiteSpace(setup.ClerkName) ? "Clerk" : setup.ClerkName, "clerk")
            : setup.ClerkActorId;
        EnsureActor(conn, tx, managerActorId, "Manager", "manager");
        EnsureActor(conn, tx, clerkActorId, string.IsNullOrWhiteSpace(setup.ClerkName) ? "Clerk" : setup.ClerkName, "clerk");
        UpsertSetting(conn, tx, "schema_version", SchemaVersion.ToString(CultureInfo.InvariantCulture));
        UpsertSetting(conn, tx, "setup_complete", setup.SetupComplete ? "1" : "0");
        UpsertSetting(conn, tx, "initial_import_complete", setup.InitialImportComplete ? "1" : "0");
        UpsertSetting(conn, tx, "store_state", setup.StoreState);
        UpsertSetting(conn, tx, "store_barcode_layout", setup.StoreBarcodeLayout);
        UpsertSetting(conn, tx, "store_name", setup.StoreName);
        UpsertSetting(conn, tx, "store_street", setup.StoreStreet);
        UpsertSetting(conn, tx, "store_city", setup.StoreCity);
        UpsertSetting(conn, tx, "configured_bin_count", setup.ConfiguredBinCount.ToString(CultureInfo.InvariantCulture));
        UpsertSetting(conn, tx, "manager_password_hash", setup.ManagerPasswordHash);
        UpsertSetting(conn, tx, "manager_actor_id", managerActorId);
        UpsertSetting(conn, tx, "clerk_name", setup.ClerkName);
        UpsertSetting(conn, tx, "clerk_password_hash", setup.ClerkPasswordHash);
        UpsertSetting(conn, tx, "clerk_actor_id", clerkActorId);
        tx.Commit();
    }

    public void SaveSetting(string key, string value)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        UpsertSetting(conn, tx, key, value);
        tx.Commit();
    }

    public void SaveGlobalFirstTicketSerial(int firstTicketSerial)
    {
        if (firstTicketSerial is not 0 and not 1)
            throw new ArgumentOutOfRangeException(nameof(firstTicketSerial));

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        UpsertSetting(
            conn,
            tx,
            "global_first_ticket_serial",
            firstTicketSerial.ToString(CultureInfo.InvariantCulture));
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE manual_games SET first_ticket_serial = $first_ticket_serial";
        cmd.Parameters.AddWithValue("$first_ticket_serial", firstTicketSerial);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public void SaveClerkCredentials(string clerkName, string clerkPinHash)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        UpsertSetting(conn, tx, "clerk_name", clerkName);
        UpsertSetting(conn, tx, "clerk_password_hash", clerkPinHash);
        tx.Commit();
    }

    public void InsertImport(StoredImportLine line)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO imports (game_id, bundle_id, ticket, bin, source, is_sold_out, created_at_utc)
            VALUES ($game_id, $bundle_id, $ticket, $bin, $source, $is_sold_out, $created_at_utc)
            """;
        cmd.Parameters.AddWithValue("$game_id", line.GameId);
        cmd.Parameters.AddWithValue("$bundle_id", line.BundleId);
        cmd.Parameters.AddWithValue("$ticket", line.Ticket);
        cmd.Parameters.AddWithValue("$bin", line.Bin);
        cmd.Parameters.AddWithValue("$source", line.Source);
        cmd.Parameters.AddWithValue("$is_sold_out", line.IsSoldOut ? 1 : 0);
        cmd.Parameters.AddWithValue("$created_at_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    public StoredSaleLine InsertSale(StoredSaleLine line)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var inserted = InsertSaleRow(conn, tx, line);
        ClaimSaleTickets(conn, tx, inserted);
        tx.Commit();
        return inserted;
    }

    public StoredSaleLine InsertImportAndSale(StoredImportLine import, StoredSaleLine sale)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var importCmd = conn.CreateCommand())
        {
            importCmd.Transaction = tx;
            importCmd.CommandText = """
                INSERT INTO imports (game_id, bundle_id, ticket, bin, source, is_sold_out, created_at_utc)
                VALUES ($game_id, $bundle_id, $ticket, $bin, $source, $is_sold_out, $created_at_utc)
                """;
            importCmd.Parameters.AddWithValue("$game_id", import.GameId);
            importCmd.Parameters.AddWithValue("$bundle_id", import.BundleId);
            importCmd.Parameters.AddWithValue("$ticket", import.Ticket);
            importCmd.Parameters.AddWithValue("$bin", import.Bin);
            importCmd.Parameters.AddWithValue("$source", import.Source);
            importCmd.Parameters.AddWithValue("$is_sold_out", import.IsSoldOut ? 1 : 0);
            importCmd.Parameters.AddWithValue("$created_at_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            importCmd.ExecuteNonQuery();
        }

        var inserted = InsertSaleRow(conn, tx, sale);
        ClaimSaleTickets(conn, tx, inserted);

        using (var receivedCmd = conn.CreateCommand())
        {
            receivedCmd.Transaction = tx;
            receivedCmd.CommandText = """
                DELETE FROM received_inventory
                WHERE game_id = $game_id
                  AND bundle_id = $bundle_id
                """;
            receivedCmd.Parameters.AddWithValue("$game_id", import.GameId);
            receivedCmd.Parameters.AddWithValue("$bundle_id", import.BundleId);
            receivedCmd.ExecuteNonQuery();
        }

        if (string.Equals(import.Source, "activation", StringComparison.OrdinalIgnoreCase))
        {
            using var activationCmd = conn.CreateCommand();
            activationCmd.Transaction = tx;
            activationCmd.CommandText = """
                INSERT INTO activation_events (
                    activated_at_utc, game_id, bundle_id, bin, source, interval_id, actor_id, actor_name)
                VALUES (
                    $activated_at_utc, $game_id, $bundle_id, $bin, $source, $interval_id, $actor_id, $actor_name)
                """;
            activationCmd.Parameters.AddWithValue("$activated_at_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            activationCmd.Parameters.AddWithValue("$game_id", import.GameId);
            activationCmd.Parameters.AddWithValue("$bundle_id", import.BundleId);
            activationCmd.Parameters.AddWithValue("$bin", import.Bin);
            activationCmd.Parameters.AddWithValue("$source", import.Source);
            activationCmd.Parameters.AddWithValue("$interval_id", inserted.IntervalId);
            activationCmd.Parameters.AddWithValue("$actor_id", inserted.ActorId);
            activationCmd.Parameters.AddWithValue("$actor_name", inserted.ActorName);
            activationCmd.ExecuteNonQuery();
        }

        tx.Commit();
        return inserted;
    }

    public void InsertReceivedBundles(IEnumerable<StoredReceivedBundle> bundles)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var bundle in bundles)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO received_inventory (game_id, bundle_id, received_at_utc, source)
                VALUES ($game_id, $bundle_id, $received_at_utc, $source)
                """;
            cmd.Parameters.AddWithValue("$game_id", bundle.GameId);
            cmd.Parameters.AddWithValue("$bundle_id", bundle.BundleId);
            cmd.Parameters.AddWithValue("$received_at_utc", bundle.ReceivedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$source", bundle.Source);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void DeleteReceivedBundle(string gameId, string bundleId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM received_inventory
            WHERE game_id = $game_id
              AND bundle_id = $bundle_id
            """;
        cmd.Parameters.AddWithValue("$game_id", gameId);
        cmd.Parameters.AddWithValue("$bundle_id", bundleId);
        if (cmd.ExecuteNonQuery() == 0)
            throw new InvalidOperationException("Received bundle was not found.");
    }

    public void DeleteImport(string gameId, string bundleId, string bin)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM imports
            WHERE id = (
                SELECT id
                FROM imports
                WHERE game_id = $game_id
                  AND bundle_id = $bundle_id
                  AND bin = $bin
                ORDER BY id DESC
                LIMIT 1)
            """;
        cmd.Parameters.AddWithValue("$game_id", gameId);
        cmd.Parameters.AddWithValue("$bundle_id", bundleId);
        cmd.Parameters.AddWithValue("$bin", bin);
        if (cmd.ExecuteNonQuery() == 0)
            throw new InvalidOperationException("Active bundle was not found.");
    }

    public StoredSaleLine InsertSaleAndUpdateImportTicket(StoredSaleLine sale, StoredImportLine bundle)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        var inserted = InsertSaleRow(conn, tx, sale);
        ClaimSaleTickets(conn, tx, inserted);

        using (var importCmd = conn.CreateCommand())
        {
            importCmd.Transaction = tx;
            importCmd.CommandText = """
                UPDATE imports
                SET ticket = $ticket,
                    is_sold_out = $is_sold_out
                WHERE game_id = $game_id
                  AND bundle_id = $bundle_id
                  AND bin = $bin
                """;
            importCmd.Parameters.AddWithValue("$ticket", bundle.Ticket);
            importCmd.Parameters.AddWithValue("$is_sold_out", bundle.IsSoldOut ? 1 : 0);
            importCmd.Parameters.AddWithValue("$game_id", bundle.GameId);
            importCmd.Parameters.AddWithValue("$bundle_id", bundle.BundleId);
            importCmd.Parameters.AddWithValue("$bin", bundle.Bin);
            var updated = importCmd.ExecuteNonQuery();
            if (updated == 0)
                throw new InvalidOperationException("Active bundle ticket state was not found.");
        }

        tx.Commit();
        return inserted;
    }

    public void MarkImportSoldOut(StoredImportLine bundle)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE imports
            SET ticket = $ticket,
                is_sold_out = 1
            WHERE game_id = $game_id
              AND bundle_id = $bundle_id
              AND bin = $bin
            """;
        cmd.Parameters.AddWithValue("$ticket", bundle.Ticket);
        cmd.Parameters.AddWithValue("$game_id", bundle.GameId);
        cmd.Parameters.AddWithValue("$bundle_id", bundle.BundleId);
        cmd.Parameters.AddWithValue("$bin", bundle.Bin);
        if (cmd.ExecuteNonQuery() == 0)
            throw new InvalidOperationException("Active bundle was not found.");
    }

    public void UpdateImportState(StoredImportLine bundle)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE imports
            SET ticket = $ticket,
                is_sold_out = $is_sold_out
            WHERE game_id = $game_id
              AND bundle_id = $bundle_id
              AND bin = $bin
            """;
        cmd.Parameters.AddWithValue("$ticket", bundle.Ticket);
        cmd.Parameters.AddWithValue("$is_sold_out", bundle.IsSoldOut ? 1 : 0);
        cmd.Parameters.AddWithValue("$game_id", bundle.GameId);
        cmd.Parameters.AddWithValue("$bundle_id", bundle.BundleId);
        cmd.Parameters.AddWithValue("$bin", bundle.Bin);
        if (cmd.ExecuteNonQuery() == 0)
            throw new InvalidOperationException("Active bundle was not found.");
    }

    public void MoveImportBundle(string gameId, string bundleId, string currentBin, string newBin)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE imports
            SET bin = $new_bin
            WHERE id = (
                SELECT id
                FROM imports
                WHERE game_id = $game_id
                  AND bundle_id = $bundle_id
                  AND bin = $current_bin
                ORDER BY id DESC
                LIMIT 1)
            """;
        cmd.Parameters.AddWithValue("$new_bin", newBin);
        cmd.Parameters.AddWithValue("$game_id", gameId);
        cmd.Parameters.AddWithValue("$bundle_id", bundleId);
        cmd.Parameters.AddWithValue("$current_bin", currentBin);
        if (cmd.ExecuteNonQuery() == 0)
            throw new InvalidOperationException("Active bundle was not found in its current bin.");
    }

    public int? GetHighestClaimedTicketSerial(string gameId, string bundleId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT MAX(ticket_serial)
            FROM sale_ticket_claims
            WHERE game_id = $game_id
              AND bundle_id = $bundle_id
            """;
        cmd.Parameters.AddWithValue("$game_id", gameId);
        cmd.Parameters.AddWithValue("$bundle_id", bundleId);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull
            ? null
            : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    public CompleteClosingResult CompleteClosing(
        DateTime closedAtUtc,
        StoredClosingRecord closingRecord,
        StoredAuditRecord auditRecord,
        IEnumerable<StoredSaleLine> generatedSales,
        IEnumerable<StoredImportLine> closedBundles,
        IEnumerable<StoredImportLine> currentBundles,
        IEnumerable<StoredImportLine> resolvedBundles,
        IEnumerable<StoredClosingReverseCorrection> reverseCorrections,
        StoredClosingReportRequest reportRequest)
    {
        var generated = new List<StoredSaleLine>(generatedSales);
        var closed = new List<StoredImportLine>(closedBundles);
        var current = new List<StoredImportLine>(currentBundles);
        var resolved = new List<StoredImportLine>(resolvedBundles);
        var reversals = new List<StoredClosingReverseCorrection>(reverseCorrections);
        if (reportRequest.Closing != closingRecord)
            throw new InvalidOperationException("Closing report request does not match the closing record.");

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        if (closingRecord.IntervalId <= 0 ||
            string.IsNullOrWhiteSpace(closingRecord.ClosedByActorId) ||
            string.IsNullOrWhiteSpace(closingRecord.ClosedByActorName))
        {
            throw new InvalidOperationException("Closing requires an open interval and actor identity.");
        }

        using (var intervalCheck = conn.CreateCommand())
        {
            intervalCheck.Transaction = tx;
            intervalCheck.CommandText = "SELECT COUNT(*) FROM ledger_intervals WHERE id = $id AND status = 'open'";
            intervalCheck.Parameters.AddWithValue("$id", closingRecord.IntervalId);
            if (Convert.ToInt32(intervalCheck.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                throw new InvalidOperationException("The closing interval is no longer open.");
        }

        var reverseAuditRecords = ApplyClosingReverseCorrections(
            conn,
            tx,
            closingRecord,
            reversals);

        var insertedGeneratedSales = new List<StoredSaleLine>();
        foreach (var line in generated)
        {
            var inserted = InsertSaleRow(conn, tx, line with
            {
                IntervalId = closingRecord.IntervalId,
                ActorId = closingRecord.ClosedByActorId,
                ActorName = closingRecord.ClosedByActorName
            });
            ClaimSaleTickets(conn, tx, inserted);
            insertedGeneratedSales.Add(inserted);
        }

        long closingHistoryId;
        using (var closingCmd = conn.CreateCommand())
        {
            closingCmd.Transaction = tx;
            closingCmd.CommandText = """
                INSERT INTO closing_history (
                    closed_at_utc, interval_start_utc, business_date, shift_sequence, shift_label, report_folder, scanned_bins, active_bins, sales_count, ticket_count,
                    sales_cents, online_sale_cents, online_cashout_cents, instant_cashout_cents, expected_cash_cents,
                    closed_bundles, current_bundles, resolved_bundles, activated_bundles,
                    interval_id, closed_by_actor_id, closed_by_actor_name)
                VALUES (
                    $closed_at_utc, $interval_start_utc, $business_date, $shift_sequence, $shift_label, $report_folder, $scanned_bins, $active_bins, $sales_count, $ticket_count,
                    $sales_cents, $online_sale_cents, $online_cashout_cents, $instant_cashout_cents, $expected_cash_cents,
                    $closed_bundles, $current_bundles, $resolved_bundles, $activated_bundles,
                    $interval_id, $closed_by_actor_id, $closed_by_actor_name)
                """;
            closingCmd.Parameters.AddWithValue("$closed_at_utc", closingRecord.ClosedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            closingCmd.Parameters.AddWithValue("$interval_start_utc", closingRecord.IntervalStartUtc.ToString("O", CultureInfo.InvariantCulture));
            closingCmd.Parameters.AddWithValue("$business_date", closingRecord.BusinessDate);
            closingCmd.Parameters.AddWithValue("$shift_sequence", closingRecord.ShiftSequence);
            closingCmd.Parameters.AddWithValue("$shift_label", closingRecord.ShiftLabel);
            closingCmd.Parameters.AddWithValue("$report_folder", closingRecord.ReportFolder);
            closingCmd.Parameters.AddWithValue("$scanned_bins", closingRecord.ScannedBins);
            closingCmd.Parameters.AddWithValue("$active_bins", closingRecord.ActiveBins);
            closingCmd.Parameters.AddWithValue("$sales_count", closingRecord.SalesCount);
            closingCmd.Parameters.AddWithValue("$ticket_count", closingRecord.TicketCount);
            closingCmd.Parameters.AddWithValue("$sales_cents", closingRecord.SalesCents);
            closingCmd.Parameters.AddWithValue("$online_sale_cents", closingRecord.OnlineSaleCents);
            closingCmd.Parameters.AddWithValue("$online_cashout_cents", closingRecord.OnlineCashoutCents);
            closingCmd.Parameters.AddWithValue("$instant_cashout_cents", closingRecord.InstantCashoutCents);
            closingCmd.Parameters.AddWithValue("$expected_cash_cents", closingRecord.ExpectedCashCents);
            closingCmd.Parameters.AddWithValue("$closed_bundles", closingRecord.ClosedBundles);
            closingCmd.Parameters.AddWithValue("$current_bundles", closingRecord.CurrentBundles);
            closingCmd.Parameters.AddWithValue("$resolved_bundles", closingRecord.ResolvedBundles);
            closingCmd.Parameters.AddWithValue("$activated_bundles", closingRecord.ActivatedBundles);
            closingCmd.Parameters.AddWithValue("$interval_id", closingRecord.IntervalId);
            closingCmd.Parameters.AddWithValue("$closed_by_actor_id", closingRecord.ClosedByActorId);
            closingCmd.Parameters.AddWithValue("$closed_by_actor_name", closingRecord.ClosedByActorName);
            closingCmd.ExecuteNonQuery();
            using var closingIdCmd = conn.CreateCommand();
            closingIdCmd.Transaction = tx;
            closingIdCmd.CommandText = "SELECT last_insert_rowid()";
            closingHistoryId = Convert.ToInt64(closingIdCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        InsertAudit(conn, tx, auditRecord);

        foreach (var bundle in closed)
        {
            using var importCmd = conn.CreateCommand();
            importCmd.Transaction = tx;
            importCmd.CommandText = """
                DELETE FROM imports
                WHERE game_id = $game_id
                  AND bundle_id = $bundle_id
                  AND bin = $bin
                """;
            importCmd.Parameters.AddWithValue("$game_id", bundle.GameId);
            importCmd.Parameters.AddWithValue("$bundle_id", bundle.BundleId);
            importCmd.Parameters.AddWithValue("$bin", bundle.Bin);
            importCmd.ExecuteNonQuery();
        }

        foreach (var bundle in current)
        {
            using var importCmd = conn.CreateCommand();
            importCmd.Transaction = tx;
            importCmd.CommandText = """
                UPDATE imports
                SET ticket = $ticket,
                    is_sold_out = $is_sold_out
                WHERE game_id = $game_id
                  AND bundle_id = $bundle_id
                  AND bin = $bin
                """;
            importCmd.Parameters.AddWithValue("$ticket", bundle.Ticket);
            importCmd.Parameters.AddWithValue("$is_sold_out", bundle.IsSoldOut ? 1 : 0);
            importCmd.Parameters.AddWithValue("$game_id", bundle.GameId);
            importCmd.Parameters.AddWithValue("$bundle_id", bundle.BundleId);
            importCmd.Parameters.AddWithValue("$bin", bundle.Bin);
            importCmd.ExecuteNonQuery();
        }

        foreach (var bundle in resolved)
        {
            using var importCmd = conn.CreateCommand();
            importCmd.Transaction = tx;
            importCmd.CommandText = """
                INSERT INTO imports (game_id, bundle_id, ticket, bin, source, is_sold_out, created_at_utc)
                VALUES ($game_id, $bundle_id, $ticket, $bin, $source, $is_sold_out, $created_at_utc)
                """;
            importCmd.Parameters.AddWithValue("$game_id", bundle.GameId);
            importCmd.Parameters.AddWithValue("$bundle_id", bundle.BundleId);
            importCmd.Parameters.AddWithValue("$ticket", bundle.Ticket);
            importCmd.Parameters.AddWithValue("$bin", bundle.Bin);
            importCmd.Parameters.AddWithValue("$source", bundle.Source);
            importCmd.Parameters.AddWithValue("$is_sold_out", bundle.IsSoldOut ? 1 : 0);
            importCmd.Parameters.AddWithValue("$created_at_utc", closedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            importCmd.ExecuteNonQuery();

            using var receivedCmd = conn.CreateCommand();
            receivedCmd.Transaction = tx;
            receivedCmd.CommandText = """
                DELETE FROM received_inventory
                WHERE game_id = $game_id
                  AND bundle_id = $bundle_id
                """;
            receivedCmd.Parameters.AddWithValue("$game_id", bundle.GameId);
            receivedCmd.Parameters.AddWithValue("$bundle_id", bundle.BundleId);
            receivedCmd.ExecuteNonQuery();
        }

        UpsertSetting(conn, tx, "last_close_utc", closedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        UpsertSetting(conn, tx, "last_close_generated_sales", generated.Count.ToString(CultureInfo.InvariantCulture));
        UpsertSetting(conn, tx, "last_close_closed_bundles", closed.Count.ToString(CultureInfo.InvariantCulture));
        UpsertSetting(conn, tx, "last_close_current_bundles", current.Count.ToString(CultureInfo.InvariantCulture));
        UpsertSetting(conn, tx, "last_close_resolved_bundles", resolved.Count.ToString(CultureInfo.InvariantCulture));

        using (var closeIntervalCmd = conn.CreateCommand())
        {
            closeIntervalCmd.Transaction = tx;
            closeIntervalCmd.CommandText = """
                UPDATE ledger_intervals
                SET status = 'closed',
                    closed_at_utc = $closed_at_utc,
                    closed_by_actor_id = $closed_by_actor_id,
                    closing_history_id = $closing_history_id,
                    business_date = $business_date,
                    shift_sequence = $shift_sequence
                WHERE id = $id AND status = 'open'
                """;
            closeIntervalCmd.Parameters.AddWithValue("$closed_at_utc", closedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            closeIntervalCmd.Parameters.AddWithValue("$closed_by_actor_id", closingRecord.ClosedByActorId);
            closeIntervalCmd.Parameters.AddWithValue("$closing_history_id", closingHistoryId);
            closeIntervalCmd.Parameters.AddWithValue("$business_date", closingRecord.BusinessDate);
            closeIntervalCmd.Parameters.AddWithValue("$shift_sequence", closingRecord.ShiftSequence);
            closeIntervalCmd.Parameters.AddWithValue("$id", closingRecord.IntervalId);
            if (closeIntervalCmd.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The closing interval changed before it could be committed.");
        }

        var openIntervalId = InsertLedgerInterval(
            conn,
            tx,
            "open",
            closedAtUtc,
            null,
            closingRecord.ClosedByActorId,
            null,
            null,
            string.Empty,
            0,
            "Opened by completed closing");
        var persistedReportRequest = reportRequest with
        {
            Sales = AttachGeneratedSaleIds(reportRequest.Sales, insertedGeneratedSales)
        };
        var reportPayloadJson = JsonSerializer.Serialize(persistedReportRequest);

        using var reportCmd = conn.CreateCommand();
        reportCmd.Transaction = tx;
        reportCmd.CommandText = """
            INSERT INTO closing_report_outbox (
                business_date, shift_sequence, report_folder, status, payload_json,
                attempt_count, last_error, created_at_utc, updated_at_utc, completed_at_utc, interval_id)
            VALUES (
                $business_date, $shift_sequence, $report_folder, 'pending', $payload_json,
                0, '', $created_at_utc, $updated_at_utc, NULL, $interval_id)
            """;
        reportCmd.Parameters.AddWithValue("$business_date", closingRecord.BusinessDate);
        reportCmd.Parameters.AddWithValue("$shift_sequence", closingRecord.ShiftSequence);
        reportCmd.Parameters.AddWithValue("$report_folder", closingRecord.ReportFolder);
        reportCmd.Parameters.AddWithValue("$payload_json", reportPayloadJson);
        reportCmd.Parameters.AddWithValue("$created_at_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        reportCmd.Parameters.AddWithValue("$updated_at_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        reportCmd.Parameters.AddWithValue("$interval_id", closingRecord.IntervalId);
        reportCmd.ExecuteNonQuery();
        using var reportIdCmd = conn.CreateCommand();
        reportIdCmd.Transaction = tx;
        reportIdCmd.CommandText = "SELECT last_insert_rowid()";
        var reportJobId = Convert.ToInt64(reportIdCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        tx.Commit();
        return new CompleteClosingResult(
            reportJobId,
            closingRecord.IntervalId,
            openIntervalId,
            insertedGeneratedSales,
            persistedReportRequest,
            reverseAuditRecords);
    }

    private static List<StoredAuditRecord> ApplyClosingReverseCorrections(
        SqliteConnection conn,
        SqliteTransaction tx,
        StoredClosingRecord closing,
        IReadOnlyList<StoredClosingReverseCorrection> corrections)
    {
        var auditRecords = new List<StoredAuditRecord>();
        if (corrections.Count == 0)
            return auditRecords;

        using (var guardCmd = conn.CreateCommand())
        {
            guardCmd.Transaction = tx;
            guardCmd.CommandText = "INSERT INTO ledger_mutation_guard (id, purpose) VALUES (1, 'closing_reverse')";
            guardCmd.ExecuteNonQuery();
        }

        foreach (var correction in corrections)
        {
            if (string.IsNullOrWhiteSpace(correction.GameId) ||
                string.IsNullOrWhiteSpace(correction.BundleId) ||
                correction.FirstTicketSerial < 0 ||
                correction.LastTicketSerial < correction.FirstTicketSerial)
            {
                throw new InvalidOperationException("Closing contains an invalid reverse-correction range.");
            }

            var selectedClaims = new List<(int Serial, long SaleId)>();
            using (var claimsCmd = conn.CreateCommand())
            {
                claimsCmd.Transaction = tx;
                claimsCmd.CommandText = """
                    SELECT ticket_serial, sale_id
                    FROM sale_ticket_claims
                    WHERE trim(game_id) = trim($game_id) COLLATE NOCASE
                      AND trim(bundle_id) = trim($bundle_id) COLLATE NOCASE
                      AND ticket_serial BETWEEN $first_ticket AND $last_ticket
                    ORDER BY ticket_serial
                    """;
                claimsCmd.Parameters.AddWithValue("$game_id", correction.GameId);
                claimsCmd.Parameters.AddWithValue("$bundle_id", correction.BundleId);
                claimsCmd.Parameters.AddWithValue("$first_ticket", correction.FirstTicketSerial);
                claimsCmd.Parameters.AddWithValue("$last_ticket", correction.LastTicketSerial);
                using var reader = claimsCmd.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(1))
                        throw new InvalidOperationException("A ticket in the Closing reverse range has no ledger sale identity.");
                    selectedClaims.Add((reader.GetInt32(0), reader.GetInt64(1)));
                }
            }

            var expectedClaimCount = correction.LastTicketSerial - correction.FirstTicketSerial + 1;
            if (selectedClaims.Count != expectedClaimCount)
            {
                throw new InvalidOperationException(
                    $"Reverse range for game {correction.GameId}, bundle {correction.BundleId} expected {expectedClaimCount.ToString(CultureInfo.InvariantCulture)} recorded tickets but found {selectedClaims.Count.ToString(CultureInfo.InvariantCulture)}. Closing stopped without changing the ledger.");
            }

            long removedCents = 0;
            var removedRows = 0;
            var reshapedRows = 0;
            foreach (var saleGroup in selectedClaims.GroupBy(claim => claim.SaleId))
            {
                var sale = QuerySaleById(conn, tx, saleGroup.Key) ??
                    throw new InvalidOperationException("A ticket in the Closing reverse range references a missing sale.");
                if (!string.Equals(sale.GameId.Trim(), correction.GameId.Trim(), StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(sale.BundleId.Trim(), correction.BundleId.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("A ticket in the Closing reverse range references a different physical bundle.");
                }
                if (sale.IntervalId != closing.IntervalId)
                {
                    throw new InvalidOperationException(
                        $"Reverse range for game {correction.GameId}, bundle {correction.BundleId} reaches a closed shift. Prior closing history was not changed.");
                }

                var selectedSerials = saleGroup.Select(claim => claim.Serial).ToHashSet();
                var allSerials = QueryClaimedSerialsForSale(conn, tx, sale.Id);
                if (allSerials.Count != sale.Quantity || sale.Quantity <= 0 || sale.AmountCents <= 0)
                    throw new InvalidOperationException($"Sale {sale.Id.ToString(CultureInfo.InvariantCulture)} has an inconsistent ticket ledger and cannot be reversed safely.");

                var voidCorrection = QueryCorrectionForSale(conn, tx, sale.Id);
                if (voidCorrection is not null &&
                    (voidCorrection.Quantity != -sale.Quantity || voidCorrection.AmountCents != -sale.AmountCents))
                {
                    throw new InvalidOperationException($"Voided sale {sale.Id.ToString(CultureInfo.InvariantCulture)} has an inconsistent correction entry and cannot be reversed safely.");
                }

                DeleteTicketClaims(conn, tx, correction.GameId, correction.BundleId, selectedSerials);

                if (sale.AmountCents % sale.Quantity != 0)
                    throw new InvalidOperationException($"Sale {sale.Id.ToString(CultureInfo.InvariantCulture)} has a non-whole-cent ticket amount and cannot be reversed safely.");

                var remainingSerials = allSerials.Where(serial => !selectedSerials.Contains(serial)).ToList();
                if (voidCorrection is null)
                    removedCents += sale.AmountCents / sale.Quantity * selectedSerials.Count;
                if (remainingSerials.Count == 0)
                {
                    if (voidCorrection is not null)
                    {
                        using var deleteVoidCmd = conn.CreateCommand();
                        deleteVoidCmd.Transaction = tx;
                        deleteVoidCmd.CommandText = "DELETE FROM sale_voids WHERE original_sale_id = $original_sale_id";
                        deleteVoidCmd.Parameters.AddWithValue("$original_sale_id", sale.Id);
                        deleteVoidCmd.ExecuteNonQuery();

                        using var deleteCorrectionCmd = conn.CreateCommand();
                        deleteCorrectionCmd.Transaction = tx;
                        deleteCorrectionCmd.CommandText = "DELETE FROM sales WHERE id = $id AND interval_id = $interval_id";
                        deleteCorrectionCmd.Parameters.AddWithValue("$id", voidCorrection.Id);
                        deleteCorrectionCmd.Parameters.AddWithValue("$interval_id", closing.IntervalId);
                        if (deleteCorrectionCmd.ExecuteNonQuery() != 1)
                            throw new InvalidOperationException("Closing could not remove the reversed void entry.");
                    }

                    using var deleteSaleCmd = conn.CreateCommand();
                    deleteSaleCmd.Transaction = tx;
                    deleteSaleCmd.CommandText = "DELETE FROM sales WHERE id = $id AND interval_id = $interval_id";
                    deleteSaleCmd.Parameters.AddWithValue("$id", sale.Id);
                    deleteSaleCmd.Parameters.AddWithValue("$interval_id", closing.IntervalId);
                    if (deleteSaleCmd.ExecuteNonQuery() != 1)
                        throw new InvalidOperationException("Closing could not remove the reversed ledger entry.");
                    removedRows++;
                    continue;
                }

                if (remainingSerials[^1] - remainingSerials[0] + 1 != remainingSerials.Count)
                    throw new InvalidOperationException($"Reverse range would split sale {sale.Id.ToString(CultureInfo.InvariantCulture)} into disconnected ranges. Closing stopped without changing the ledger.");

                var width = SaleTicketSerialWidth(sale.Ticket);
                var remainingTicket = FormatSaleTicketRange(remainingSerials[0], remainingSerials[^1], width);
                using var updateSaleCmd = conn.CreateCommand();
                updateSaleCmd.Transaction = tx;
                updateSaleCmd.CommandText = """
                    UPDATE sales
                    SET ticket = $ticket,
                        quantity = $quantity,
                        amount_cents = $amount_cents
                    WHERE id = $id AND interval_id = $interval_id
                    """;
                updateSaleCmd.Parameters.AddWithValue("$ticket", remainingTicket);
                updateSaleCmd.Parameters.AddWithValue("$quantity", remainingSerials.Count);
                updateSaleCmd.Parameters.AddWithValue("$amount_cents", sale.AmountCents / sale.Quantity * remainingSerials.Count);
                updateSaleCmd.Parameters.AddWithValue("$id", sale.Id);
                updateSaleCmd.Parameters.AddWithValue("$interval_id", closing.IntervalId);
                if (updateSaleCmd.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("Closing could not retain the unaffected part of a reversed ledger entry.");
                if (voidCorrection is not null)
                {
                    using var updateCorrectionCmd = conn.CreateCommand();
                    updateCorrectionCmd.Transaction = tx;
                    updateCorrectionCmd.CommandText = """
                        UPDATE sales
                        SET ticket = $ticket,
                            quantity = $quantity,
                            amount_cents = $amount_cents
                        WHERE id = $id AND interval_id = $interval_id
                        """;
                    updateCorrectionCmd.Parameters.AddWithValue("$ticket", remainingTicket);
                    updateCorrectionCmd.Parameters.AddWithValue("$quantity", -remainingSerials.Count);
                    updateCorrectionCmd.Parameters.AddWithValue("$amount_cents", -(sale.AmountCents / sale.Quantity * remainingSerials.Count));
                    updateCorrectionCmd.Parameters.AddWithValue("$id", voidCorrection.Id);
                    updateCorrectionCmd.Parameters.AddWithValue("$interval_id", closing.IntervalId);
                    if (updateCorrectionCmd.ExecuteNonQuery() != 1)
                        throw new InvalidOperationException("Closing could not retain the unaffected part of a reversed void entry.");
                }
                reshapedRows++;
            }

            var widthForAudit = Math.Max(
                SaleTicketSerialWidth(correction.StoredCurrentTicket),
                SaleTicketSerialWidth(correction.ScannedTicket));
            var auditRecord = new StoredAuditRecord(
                closing.ClosedAtUtc,
                "closing",
                "Closing sale range reversed",
                closing.ClosedByActorName,
                $"Game {correction.GameId}, bundle {correction.BundleId}, bin {correction.Bin}, range {FormatSaleTicketRange(correction.FirstTicketSerial, correction.LastTicketSerial, widthForAudit)}, stored current {correction.StoredCurrentTicket}, scanned available {correction.ScannedTicket}; released claims {selectedClaims.Count.ToString(CultureInfo.InvariantCulture)}, removed {removedCents.ToString(CultureInfo.InvariantCulture)} cents, deleted rows {removedRows.ToString(CultureInfo.InvariantCulture)}, reshaped rows {reshapedRows.ToString(CultureInfo.InvariantCulture)}",
                closing.ClosedByActorId);
            InsertAudit(conn, tx, auditRecord);
            auditRecords.Add(auditRecord);
        }

        using var clearGuardCmd = conn.CreateCommand();
        clearGuardCmd.Transaction = tx;
        clearGuardCmd.CommandText = "DELETE FROM ledger_mutation_guard WHERE id = 1";
        clearGuardCmd.ExecuteNonQuery();
        return auditRecords;
    }

    private static StoredSaleLine? QuerySaleById(SqliteConnection conn, SqliteTransaction tx, long saleId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT id, sold_at_utc, game_id, bundle_id, bin, ticket, quantity, amount_cents, source,
                   interval_id, actor_id, actor_name, corrects_sale_id, migration_state
            FROM sales
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", saleId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        var soldAt = DateTime.TryParse(
            reader.GetString(1),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : DateTime.UtcNow;
        return new StoredSaleLine(
            soldAt,
            reader.GetString(2),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6),
            reader.GetInt64(7),
            reader.GetString(8),
            reader.GetString(3),
            reader.GetInt64(0),
            reader.IsDBNull(9) ? 0 : reader.GetInt64(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetInt64(12),
            reader.GetString(13));
    }

    private static List<int> QueryClaimedSerialsForSale(SqliteConnection conn, SqliteTransaction tx, long saleId)
    {
        var serials = new List<int>();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT ticket_serial FROM sale_ticket_claims WHERE sale_id = $sale_id ORDER BY ticket_serial";
        cmd.Parameters.AddWithValue("$sale_id", saleId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            serials.Add(reader.GetInt32(0));
        return serials;
    }

    private static StoredSaleLine? QueryCorrectionForSale(SqliteConnection conn, SqliteTransaction tx, long saleId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id FROM sales WHERE corrects_sale_id = $sale_id";
        cmd.Parameters.AddWithValue("$sale_id", saleId);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull
            ? null
            : QuerySaleById(conn, tx, Convert.ToInt64(result, CultureInfo.InvariantCulture));
    }

    private static void DeleteTicketClaims(
        SqliteConnection conn,
        SqliteTransaction tx,
        string gameId,
        string bundleId,
        IReadOnlySet<int> serials)
    {
        foreach (var serial in serials)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                DELETE FROM sale_ticket_claims
                WHERE trim(game_id) = trim($game_id) COLLATE NOCASE
                  AND trim(bundle_id) = trim($bundle_id) COLLATE NOCASE
                  AND ticket_serial = $ticket_serial
                """;
            cmd.Parameters.AddWithValue("$game_id", gameId);
            cmd.Parameters.AddWithValue("$bundle_id", bundleId);
            cmd.Parameters.AddWithValue("$ticket_serial", serial);
            if (cmd.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Closing could not release a reversed ticket claim.");
        }
    }

    private static int SaleTicketSerialWidth(string ticket)
    {
        var width = 1;
        foreach (var part in (ticket ?? string.Empty).Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            width = Math.Max(width, part.Count(char.IsAsciiDigit));
        return width;
    }

    private static string FormatSaleTicketRange(int first, int last, int width)
    {
        var firstText = first.ToString($"D{Math.Max(1, width).ToString(CultureInfo.InvariantCulture)}", CultureInfo.InvariantCulture);
        var lastText = last.ToString($"D{Math.Max(1, width).ToString(CultureInfo.InvariantCulture)}", CultureInfo.InvariantCulture);
        return first == last ? firstText : $"{firstText}-{lastText}";
    }

    public void MarkClosingReportCompleted(long reportJobId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE closing_report_outbox
            SET status = 'completed',
                attempt_count = attempt_count + 1,
                last_error = '',
                updated_at_utc = $updated_at_utc,
                completed_at_utc = $completed_at_utc
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$updated_at_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$completed_at_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$id", reportJobId);
        if (cmd.ExecuteNonQuery() == 0)
            throw new InvalidOperationException("Closing report job was not found.");
    }

    public void MarkClosingReportFailed(long reportJobId, string error)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE closing_report_outbox
            SET status = 'failed',
                attempt_count = attempt_count + 1,
                last_error = $last_error,
                updated_at_utc = $updated_at_utc,
                completed_at_utc = NULL
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$last_error", error ?? string.Empty);
        cmd.Parameters.AddWithValue("$updated_at_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$id", reportJobId);
        if (cmd.ExecuteNonQuery() == 0)
            throw new InvalidOperationException("Closing report job was not found.");
    }

    public void BackupDatabase(string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("Backup destination is required.", nameof(destinationPath));

        var fullDestinationPath = Path.GetFullPath(destinationPath);
        if (string.Equals(fullDestinationPath, Path.GetFullPath(DatabasePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Backup destination must be different from the active database.");
        if (File.Exists(fullDestinationPath))
            throw new IOException("Backup destination already exists.");

        var destinationFolder = Path.GetDirectoryName(fullDestinationPath);
        if (!string.IsNullOrWhiteSpace(destinationFolder))
            Directory.CreateDirectory(destinationFolder);

        var destinationBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = fullDestinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };
        using var source = Open();
        using var destination = new SqliteConnection(destinationBuilder.ToString());
        destination.Open();
        source.BackupDatabase(destination);
    }

    public void InsertAudit(StoredAuditRecord record)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        InsertAudit(conn, tx, record);
        tx.Commit();
    }

    public StoredSaleLine InsertVoid(StoredSaleLine original, StoredSaleLine correction, string saleKey)
    {
        if (original.Id <= 0)
            throw new InvalidOperationException("The original sale has no persistent ledger ID.");

        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var originalCmd = conn.CreateCommand())
        {
            originalCmd.Transaction = tx;
            originalCmd.CommandText = "SELECT COUNT(*) FROM sales WHERE id = $id AND quantity > 0 AND amount_cents > 0";
            originalCmd.Parameters.AddWithValue("$id", original.Id);
            if (Convert.ToInt32(originalCmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                throw new InvalidOperationException("The original sale was not found or is not voidable.");
        }

        var insertedCorrection = InsertSaleRow(conn, tx, correction with { CorrectsSaleId = original.Id });

        using (var voidCmd = conn.CreateCommand())
        {
            voidCmd.Transaction = tx;
            voidCmd.CommandText = """
                INSERT OR IGNORE INTO sale_voids (
                    sale_key, voided_at_utc, original_sale_id, correction_sale_id, actor_id)
                VALUES (
                    $sale_key, $voided_at_utc, $original_sale_id, $correction_sale_id, $actor_id)
                """;
            voidCmd.Parameters.AddWithValue("$sale_key", saleKey);
            voidCmd.Parameters.AddWithValue("$voided_at_utc", insertedCorrection.SoldAtUtc.ToString("O", CultureInfo.InvariantCulture));
            voidCmd.Parameters.AddWithValue("$original_sale_id", original.Id);
            voidCmd.Parameters.AddWithValue("$correction_sale_id", insertedCorrection.Id);
            voidCmd.Parameters.AddWithValue("$actor_id", insertedCorrection.ActorId);
            if (voidCmd.ExecuteNonQuery() == 0)
                throw new InvalidOperationException("This sale has already been voided.");
        }

        tx.Commit();
        return insertedCorrection;
    }

    public void UpsertManualGame(StoredGameRecord game)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO manual_games (game_id, name, price_cents, bundle_price_cents, first_ticket_serial, source, image_uri, image_status)
            VALUES ($game_id, $name, $price_cents, $bundle_price_cents, $first_ticket_serial, $source, $image_uri, $image_status)
            ON CONFLICT(game_id) DO UPDATE SET
                name = excluded.name,
                price_cents = excluded.price_cents,
                bundle_price_cents = excluded.bundle_price_cents,
                first_ticket_serial = excluded.first_ticket_serial,
                source = excluded.source,
                image_uri = excluded.image_uri,
                image_status = excluded.image_status
            """;
        cmd.Parameters.AddWithValue("$game_id", game.GameId);
        cmd.Parameters.AddWithValue("$name", game.Name);
        cmd.Parameters.AddWithValue("$price_cents", game.PriceCents);
        cmd.Parameters.AddWithValue("$bundle_price_cents", game.BundlePriceCents);
        cmd.Parameters.AddWithValue("$first_ticket_serial", game.FirstTicketSerial);
        cmd.Parameters.AddWithValue("$source", game.Source);
        cmd.Parameters.AddWithValue("$image_uri", game.ImageUri);
        cmd.Parameters.AddWithValue("$image_status", game.ImageStatus);
        cmd.ExecuteNonQuery();
    }

    public void UpsertRdisplayDisplay(StoredRdisplayDisplay display)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rdisplay_displays (
                id, slug, name, host, port, screen_order, is_active, active_screen_count,
                created_at_utc, last_seen_at_utc, auth_token, hardware_json, last_registered_at_utc,
                last_server_url)
            VALUES (
                $id, $slug, $name, $host, $port, $screen_order, $is_active, $active_screen_count,
                $created_at_utc, $last_seen_at_utc, $auth_token, $hardware_json, $last_registered_at_utc,
                $last_server_url)
            ON CONFLICT(id) DO UPDATE SET
                slug = excluded.slug,
                name = excluded.name,
                host = excluded.host,
                port = excluded.port,
                screen_order = excluded.screen_order,
                is_active = excluded.is_active,
                active_screen_count = excluded.active_screen_count,
                created_at_utc = excluded.created_at_utc,
                last_seen_at_utc = excluded.last_seen_at_utc,
                auth_token = excluded.auth_token,
                hardware_json = excluded.hardware_json,
                last_registered_at_utc = excluded.last_registered_at_utc,
                last_server_url = excluded.last_server_url
            """;
        cmd.Parameters.AddWithValue("$id", display.Id);
        cmd.Parameters.AddWithValue("$slug", display.Slug);
        cmd.Parameters.AddWithValue("$name", display.Name);
        cmd.Parameters.AddWithValue("$host", display.Host);
        cmd.Parameters.AddWithValue("$port", display.Port);
        cmd.Parameters.AddWithValue("$screen_order", display.ScreenOrder);
        cmd.Parameters.AddWithValue("$is_active", display.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("$active_screen_count", display.ActiveScreenCount);
        cmd.Parameters.AddWithValue("$created_at_utc", display.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$last_seen_at_utc", NullableDateTimeValue(display.LastSeenAtUtc));
        cmd.Parameters.AddWithValue("$auth_token", display.AuthToken ?? string.Empty);
        cmd.Parameters.AddWithValue("$hardware_json", display.HardwareJson ?? string.Empty);
        cmd.Parameters.AddWithValue("$last_registered_at_utc", NullableDateTimeValue(display.LastRegisteredAtUtc));
        cmd.Parameters.AddWithValue("$last_server_url", display.LastServerUrl ?? string.Empty);
        cmd.ExecuteNonQuery();
    }

    public void DeleteRdisplayDisplay(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM rdisplay_displays WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static void EnsureSchema(SqliteConnection conn, string databasePath)
    {
        lock (SchemaLock)
        {
            var fullDatabasePath = Path.GetFullPath(databasePath);
            if (SchemaReadyPaths.Contains(fullDatabasePath))
                return;

            Exec(conn, "PRAGMA journal_mode=WAL");
            Exec(conn, "PRAGMA foreign_keys=ON");
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                )
                """);
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INTEGER PRIMARY KEY,
                    applied_at_utc TEXT NOT NULL,
                    description TEXT NOT NULL
                )
                """);
            var previousSchemaVersion = ReadStoredSchemaVersion(conn);
            if (previousSchemaVersion is > 0 and < 17)
                CreatePreLedgerMigrationBackup(conn, fullDatabasePath, previousSchemaVersion);
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS imports (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    game_id TEXT NOT NULL,
                    bundle_id TEXT NOT NULL,
                    ticket TEXT NOT NULL,
                    bin TEXT NOT NULL,
                    source TEXT NOT NULL DEFAULT 'initial_import',
                    is_sold_out INTEGER NOT NULL DEFAULT 0,
                    created_at_utc TEXT NOT NULL
                )
                """);
            EnsureColumn(conn, "imports", "source", "TEXT NOT NULL DEFAULT 'initial_import'");
            EnsureColumn(conn, "imports", "is_sold_out", "INTEGER NOT NULL DEFAULT 0");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_imports_bundle ON imports(game_id, bundle_id)");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_imports_bin ON imports(bin)");
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS received_inventory (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    game_id TEXT NOT NULL,
                    bundle_id TEXT NOT NULL,
                    received_at_utc TEXT NOT NULL,
                    source TEXT NOT NULL DEFAULT 'receiving',
                    UNIQUE(game_id, bundle_id)
                )
                """);
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_received_inventory_game ON received_inventory(game_id)");
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS actors (
                    id TEXT PRIMARY KEY,
                    display_name TEXT NOT NULL,
                    role TEXT NOT NULL,
                    is_active INTEGER NOT NULL DEFAULT 1,
                    created_at_utc TEXT NOT NULL
                )
                """);
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS ledger_intervals (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    status TEXT NOT NULL CHECK (status IN ('open', 'closed', 'legacy_unresolved')),
                    opened_at_utc TEXT NOT NULL,
                    closed_at_utc TEXT NULL,
                    opened_by_actor_id TEXT NOT NULL,
                    closed_by_actor_id TEXT NULL,
                    closing_history_id INTEGER NULL,
                    business_date TEXT NOT NULL DEFAULT '',
                    shift_sequence INTEGER NOT NULL DEFAULT 0,
                    migration_note TEXT NOT NULL DEFAULT '',
                    FOREIGN KEY (opened_by_actor_id) REFERENCES actors(id),
                    FOREIGN KEY (closed_by_actor_id) REFERENCES actors(id)
                )
                """);
            Exec(conn, "CREATE UNIQUE INDEX IF NOT EXISTS idx_ledger_intervals_one_open ON ledger_intervals(status) WHERE status = 'open'");
            Exec(conn, "CREATE UNIQUE INDEX IF NOT EXISTS idx_ledger_intervals_closing ON ledger_intervals(closing_history_id) WHERE closing_history_id IS NOT NULL");
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS ledger_migration_conflicts (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    conflict_type TEXT NOT NULL,
                    severity TEXT NOT NULL,
                    sale_id INTEGER NULL,
                    conflicting_sale_id INTEGER NULL,
                    game_id TEXT NOT NULL DEFAULT '',
                    bundle_id TEXT NOT NULL DEFAULT '',
                    ticket_serial INTEGER NULL,
                    detail TEXT NOT NULL,
                    status TEXT NOT NULL DEFAULT 'unresolved',
                    detected_at_utc TEXT NOT NULL,
                    resolved_at_utc TEXT NULL,
                    resolved_by_actor_id TEXT NULL,
                    resolution TEXT NOT NULL DEFAULT ''
                )
                """);
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_ledger_conflicts_status ON ledger_migration_conflicts(status, severity)");
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS activation_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    activated_at_utc TEXT NOT NULL,
                    game_id TEXT NOT NULL,
                    bundle_id TEXT NOT NULL,
                    bin TEXT NOT NULL,
                    source TEXT NOT NULL DEFAULT 'activation'
                )
                """);
            EnsureColumn(conn, "activation_events", "interval_id", "INTEGER NULL");
            EnsureColumn(conn, "activation_events", "actor_id", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(conn, "activation_events", "actor_name", "TEXT NOT NULL DEFAULT ''");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_activation_events_activated_at ON activation_events(activated_at_utc)");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_activation_events_interval ON activation_events(interval_id)");
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS sales (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    sold_at_utc TEXT NOT NULL,
                    game_id TEXT NOT NULL,
                    bundle_id TEXT NOT NULL DEFAULT '',
                    bin TEXT NOT NULL,
                    ticket TEXT NOT NULL,
                    quantity INTEGER NOT NULL,
                    amount_cents INTEGER NOT NULL,
                    source TEXT NOT NULL DEFAULT 'normal_sale'
                )
                """);
            EnsureColumn(conn, "sales", "source", "TEXT NOT NULL DEFAULT 'normal_sale'");
            EnsureColumn(conn, "sales", "bundle_id", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(conn, "sales", "interval_id", "INTEGER NULL");
            EnsureColumn(conn, "sales", "actor_id", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(conn, "sales", "actor_name", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(conn, "sales", "corrects_sale_id", "INTEGER NULL");
            EnsureColumn(conn, "sales", "migration_state", "TEXT NOT NULL DEFAULT 'native'");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_sales_game ON sales(game_id)");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_sales_bin ON sales(bin)");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_sales_sold_at ON sales(sold_at_utc)");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_sales_bundle ON sales(game_id, bundle_id)");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_sales_interval ON sales(interval_id, id)");
            Exec(conn, "CREATE UNIQUE INDEX IF NOT EXISTS idx_sales_one_correction ON sales(corrects_sale_id) WHERE corrects_sale_id IS NOT NULL");
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS sale_voids (
                    sale_key TEXT PRIMARY KEY,
                    voided_at_utc TEXT NOT NULL
                )
                """);
            EnsureColumn(conn, "sale_voids", "original_sale_id", "INTEGER NULL");
            EnsureColumn(conn, "sale_voids", "correction_sale_id", "INTEGER NULL");
            EnsureColumn(conn, "sale_voids", "actor_id", "TEXT NOT NULL DEFAULT ''");
            Exec(conn, "CREATE UNIQUE INDEX IF NOT EXISTS idx_sale_voids_original_sale ON sale_voids(original_sale_id) WHERE original_sale_id IS NOT NULL");
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS sale_ticket_claims (
                    game_id TEXT NOT NULL,
                    bundle_id TEXT NOT NULL,
                    ticket_serial INTEGER NOT NULL,
                    claimed_at_utc TEXT NOT NULL,
                    PRIMARY KEY (game_id, bundle_id, ticket_serial)
                )
                """);
            EnsureColumn(conn, "sale_ticket_claims", "sale_id", "INTEGER NULL");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_sale_ticket_claims_sale ON sale_ticket_claims(sale_id)");
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS ledger_mutation_guard (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    purpose TEXT NOT NULL CHECK (purpose = 'closing_reverse')
                )
                """);
            Exec(conn, "DELETE FROM ledger_mutation_guard");
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS closing_history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    closed_at_utc TEXT NOT NULL,
                    interval_start_utc TEXT NOT NULL DEFAULT '',
                    business_date TEXT NOT NULL DEFAULT '',
                    shift_sequence INTEGER NOT NULL DEFAULT 0,
                    shift_label TEXT NOT NULL DEFAULT '',
                    report_folder TEXT NOT NULL DEFAULT '',
                    scanned_bins INTEGER NOT NULL,
                    active_bins INTEGER NOT NULL,
                    sales_count INTEGER NOT NULL,
                    ticket_count INTEGER NOT NULL,
                    sales_cents INTEGER NOT NULL,
                    online_sale_cents INTEGER NOT NULL DEFAULT 0,
                    online_cashout_cents INTEGER NOT NULL DEFAULT 0,
                    instant_cashout_cents INTEGER NOT NULL DEFAULT 0,
                    expected_cash_cents INTEGER NOT NULL DEFAULT 0,
                    closed_bundles INTEGER NOT NULL,
                    current_bundles INTEGER NOT NULL,
                    resolved_bundles INTEGER NOT NULL,
                    activated_bundles INTEGER NOT NULL DEFAULT 0
                )
                """);
            EnsureColumn(conn, "closing_history", "interval_start_utc", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(conn, "closing_history", "business_date", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(conn, "closing_history", "shift_sequence", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, "closing_history", "shift_label", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(conn, "closing_history", "report_folder", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(conn, "closing_history", "online_sale_cents", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, "closing_history", "online_cashout_cents", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, "closing_history", "instant_cashout_cents", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, "closing_history", "expected_cash_cents", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, "closing_history", "activated_bundles", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, "closing_history", "interval_id", "INTEGER NULL");
            EnsureColumn(conn, "closing_history", "closed_by_actor_id", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(conn, "closing_history", "closed_by_actor_name", "TEXT NOT NULL DEFAULT ''");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_closing_history_closed_at ON closing_history(closed_at_utc)");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_closing_history_business_date ON closing_history(business_date)");
            Exec(conn, "CREATE UNIQUE INDEX IF NOT EXISTS idx_closing_history_business_shift ON closing_history(business_date, shift_sequence) WHERE business_date <> '' AND shift_sequence > 0");
            Exec(conn, "CREATE UNIQUE INDEX IF NOT EXISTS idx_closing_history_interval ON closing_history(interval_id) WHERE interval_id IS NOT NULL");
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS closing_report_outbox (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    business_date TEXT NOT NULL,
                    shift_sequence INTEGER NOT NULL,
                    report_folder TEXT NOT NULL,
                    status TEXT NOT NULL DEFAULT 'pending',
                    payload_json TEXT NOT NULL,
                    attempt_count INTEGER NOT NULL DEFAULT 0,
                    last_error TEXT NOT NULL DEFAULT '',
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    completed_at_utc TEXT NULL,
                    UNIQUE(business_date, shift_sequence)
                )
                """);
            EnsureColumn(conn, "closing_report_outbox", "interval_id", "INTEGER NULL");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_closing_report_outbox_status ON closing_report_outbox(status, updated_at_utc)");
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS audit_log (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    occurred_at_utc TEXT NOT NULL,
                    category TEXT NOT NULL,
                    action TEXT NOT NULL,
                    actor TEXT NOT NULL,
                    detail TEXT NOT NULL
                )
                """);
            EnsureColumn(conn, "audit_log", "actor_id", "TEXT NOT NULL DEFAULT ''");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_audit_log_occurred_at ON audit_log(occurred_at_utc)");
            RepairDuplicateImports(conn);
            Exec(conn, "CREATE UNIQUE INDEX IF NOT EXISTS idx_imports_physical_bundle_unique ON imports(trim(game_id) COLLATE NOCASE, trim(bundle_id) COLLATE NOCASE)");
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS manual_games (
                    game_id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    source TEXT NOT NULL,
                    price_cents INTEGER NOT NULL DEFAULT 0,
                    bundle_price_cents INTEGER NOT NULL DEFAULT 0,
                    first_ticket_serial INTEGER NOT NULL DEFAULT 0,
                    image_uri TEXT NOT NULL,
                    image_status TEXT NOT NULL
                )
                """);
            EnsureColumn(conn, "manual_games", "price_cents", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, "manual_games", "bundle_price_cents", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, "manual_games", "first_ticket_serial", "INTEGER NOT NULL DEFAULT 0");
            Exec(conn, "UPDATE manual_games SET bundle_price_cents = 0 WHERE price_cents <= 0 AND bundle_price_cents <> 0");
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS rdisplay_displays (
                    id INTEGER PRIMARY KEY,
                    slug TEXT NOT NULL,
                    name TEXT NOT NULL,
                    host TEXT NOT NULL,
                    port INTEGER NOT NULL,
                    screen_order INTEGER NOT NULL,
                    is_active INTEGER NOT NULL,
                    active_screen_count INTEGER NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    last_seen_at_utc TEXT NULL,
                    auth_token TEXT NOT NULL,
                    hardware_json TEXT NOT NULL,
                    last_registered_at_utc TEXT NULL,
                    last_server_url TEXT NOT NULL DEFAULT ''
                )
                """);
            EnsureColumn(conn, "rdisplay_displays", "last_server_url", "TEXT NOT NULL DEFAULT ''");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_rdisplay_displays_token ON rdisplay_displays(auth_token)");
            if (previousSchemaVersion < 17)
            {
                DropLedgerIntegrityTriggers(conn);
                MigrateLedgerV17(conn);
            }
            else
                EnsureLedgerRuntimeState(conn);
            DropLedgerIntegrityTriggers(conn);
            CreateLedgerIntegrityTriggers(conn);
            RecordSchemaMigration(conn, SchemaVersion, previousSchemaVersion, "Allow audited open-interval Closing reversals while preserving closed ledger history");
            Exec(conn, $"INSERT OR REPLACE INTO settings (key, value) VALUES ('schema_version', '{SchemaVersion.ToString(CultureInfo.InvariantCulture)}')");
            SchemaReadyPaths.Add(fullDatabasePath);
        }
    }

    private static int ReadStoredSchemaVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = 'schema_version'";
        var value = cmd.ExecuteScalar() as string;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static void CreatePreLedgerMigrationBackup(
        SqliteConnection source,
        string databasePath,
        int previousSchemaVersion)
    {
        var backupFolder = Path.Combine(Path.GetDirectoryName(databasePath)!, "migration-backups");
        Directory.CreateDirectory(backupFolder);
        var backupPath = Path.Combine(
            backupFolder,
            $"simplelotto-pre-ledger-v17-from-v{previousSchemaVersion.ToString(CultureInfo.InvariantCulture)}.db");
        if (File.Exists(backupPath))
            return;

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };
        using var destination = new SqliteConnection(builder.ToString());
        destination.Open();
        source.BackupDatabase(destination);
    }

    private static void MigrateLedgerV17(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        Exec(conn, tx, "DELETE FROM audit_log WHERE category = 'migration' AND action IN ('Ledger migration conflict', 'Ledger identity migration completed')");
        Exec(conn, tx, "DELETE FROM ledger_migration_conflicts");
        Exec(conn, tx, "DELETE FROM ledger_intervals");
        Exec(conn, tx, "UPDATE sales SET interval_id = NULL, actor_id = '', actor_name = '', corrects_sale_id = NULL, migration_state = 'legacy_pending'");
        Exec(conn, tx, "UPDATE activation_events SET interval_id = NULL, actor_id = '', actor_name = ''");
        Exec(conn, tx, "UPDATE closing_history SET interval_id = NULL, closed_by_actor_id = '', closed_by_actor_name = ''");
        Exec(conn, tx, "UPDATE sale_voids SET original_sale_id = NULL, correction_sale_id = NULL, actor_id = ''");

        EnsureActor(conn, tx, SystemActorId, "System", "system");
        EnsureActor(conn, tx, LegacyActorId, "Legacy—actor unknown", "migration");
        var managerActorId = GetOrCreateActorSetting(conn, tx, "manager_actor_id", "Manager", "manager");
        var clerkName = ReadSetting(conn, tx, "clerk_name");
        var clerkActorId = GetOrCreateActorSetting(
            conn,
            tx,
            "clerk_actor_id",
            string.IsNullOrWhiteSpace(clerkName) ? "Clerk" : clerkName,
            "clerk");

        using (var actorAudit = conn.CreateCommand())
        {
            actorAudit.Transaction = tx;
            actorAudit.CommandText = """
                UPDATE audit_log
                SET actor_id = CASE
                    WHEN lower(trim(actor)) = 'manager' THEN $manager_actor_id
                    WHEN lower(trim(actor)) = lower(trim($clerk_name)) THEN $clerk_actor_id
                    WHEN lower(trim(actor)) = 'system' THEN $system_actor_id
                    ELSE $legacy_actor_id
                END
                """;
            actorAudit.Parameters.AddWithValue("$manager_actor_id", managerActorId);
            actorAudit.Parameters.AddWithValue("$clerk_name", string.IsNullOrWhiteSpace(clerkName) ? "Clerk" : clerkName);
            actorAudit.Parameters.AddWithValue("$clerk_actor_id", clerkActorId);
            actorAudit.Parameters.AddWithValue("$system_actor_id", SystemActorId);
            actorAudit.Parameters.AddWithValue("$legacy_actor_id", LegacyActorId);
            actorAudit.ExecuteNonQuery();
        }

        var closings = QueryMigrationClosings(conn, tx);
        var sales = QueryMigrationSales(conn, tx);
        var unassignedSaleIds = sales.Select(sale => sale.Id).ToHashSet();
        var intervalByClosingId = new Dictionary<long, long>();
        long unresolvedIntervalId = 0;

        foreach (var closing in closings)
        {
            var intervalId = InsertLedgerInterval(
                conn,
                tx,
                "closed",
                closing.IntervalStartUtc,
                closing.ClosedAtUtc,
                LegacyActorId,
                LegacyActorId,
                closing.Id,
                closing.BusinessDate,
                closing.ShiftSequence,
                "Backfilled from closing history");
            intervalByClosingId[closing.Id] = intervalId;
            UpdateClosingLedgerIdentity(conn, tx, closing.Id, intervalId, LegacyActorId, "Legacy—actor unknown");

            var candidates = sales
                .Where(sale => unassignedSaleIds.Contains(sale.Id) &&
                    sale.SoldAtUtc > closing.IntervalStartUtc &&
                    sale.SoldAtUtc <= closing.ClosedAtUtc)
                .ToList();
            var matchesSummary = candidates.Count == closing.SalesCount &&
                candidates.Sum(sale => sale.Quantity) == closing.TicketCount &&
                candidates.Sum(sale => sale.AmountCents) == closing.SalesCents;
            if (matchesSummary)
            {
                foreach (var sale in candidates)
                {
                    AssignSaleLedgerIdentity(conn, tx, sale.Id, intervalId, LegacyActorId, "Legacy—actor unknown", "legacy_verified");
                    unassignedSaleIds.Remove(sale.Id);
                }
            }
            else
            {
                unresolvedIntervalId = EnsureLegacyUnresolvedInterval(conn, tx, unresolvedIntervalId);
                foreach (var sale in candidates)
                {
                    AssignSaleLedgerIdentity(conn, tx, sale.Id, unresolvedIntervalId, LegacyActorId, "Legacy—actor unknown", "legacy_conflict");
                    unassignedSaleIds.Remove(sale.Id);
                }

                InsertLedgerConflict(
                    conn,
                    tx,
                    "interval_summary_mismatch",
                    "blocking",
                    null,
                    null,
                    string.Empty,
                    string.Empty,
                    null,
                    $"Closing {closing.BusinessDate} #{closing.ShiftSequence.ToString(CultureInfo.InvariantCulture)} expected rows/tickets/cents {closing.SalesCount.ToString(CultureInfo.InvariantCulture)}/{closing.TicketCount.ToString(CultureInfo.InvariantCulture)}/{closing.SalesCents.ToString(CultureInfo.InvariantCulture)} but timestamp inference found {candidates.Count.ToString(CultureInfo.InvariantCulture)}/{candidates.Sum(sale => sale.Quantity).ToString(CultureInfo.InvariantCulture)}/{candidates.Sum(sale => sale.AmountCents).ToString(CultureInfo.InvariantCulture)}. Candidate sales were quarantined instead of assigned to a closed interval.");
            }
        }

        var lastClosedAtUtc = closings.Count == 0 ? DateTime.MinValue : closings[^1].ClosedAtUtc;
        var openIntervalId = InsertLedgerInterval(
            conn,
            tx,
            "open",
            lastClosedAtUtc,
            null,
            SystemActorId,
            null,
            null,
            string.Empty,
            0,
            "Open interval created by ledger migration");

        foreach (var sale in sales.Where(sale => unassignedSaleIds.Contains(sale.Id)))
        {
            if (closings.Count == 0 || sale.SoldAtUtc > lastClosedAtUtc)
            {
                AssignSaleLedgerIdentity(conn, tx, sale.Id, openIntervalId, LegacyActorId, "Legacy—actor unknown", "legacy_open");
            }
            else
            {
                unresolvedIntervalId = EnsureLegacyUnresolvedInterval(conn, tx, unresolvedIntervalId);
                AssignSaleLedgerIdentity(conn, tx, sale.Id, unresolvedIntervalId, LegacyActorId, "Legacy—actor unknown", "legacy_conflict");
                InsertLedgerConflict(
                    conn,
                    tx,
                    "unassigned_historical_sale",
                    "blocking",
                    sale.Id,
                    null,
                    sale.GameId,
                    sale.BundleId,
                    null,
                    "Sale could not be reconciled to a verified closed interval and was quarantined.");
            }
        }

        MigrateActivationIntervals(conn, tx, closings, intervalByClosingId, openIntervalId, ref unresolvedIntervalId);
        RebuildHistoricalTicketClaims(conn, tx, sales);
        BackfillHistoricalVoids(conn, tx, sales);

        Exec(conn, tx, """
            INSERT INTO audit_log (occurred_at_utc, category, action, actor, actor_id, detail)
            SELECT detected_at_utc,
                   'migration',
                   'Ledger migration conflict',
                   'System',
                   'system',
                   conflict_type || ' [' || severity || '] ' || detail
            FROM ledger_migration_conflicts
            WHERE status = 'unresolved'
            ORDER BY id
            """);

        using (var audit = conn.CreateCommand())
        {
            audit.Transaction = tx;
            audit.CommandText = """
                INSERT INTO audit_log (occurred_at_utc, category, action, actor, actor_id, detail)
                VALUES ($occurred_at_utc, 'migration', 'Ledger identity migration completed', 'System', $actor_id, $detail)
                """;
            audit.Parameters.AddWithValue("$occurred_at_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            audit.Parameters.AddWithValue("$actor_id", SystemActorId);
            audit.Parameters.AddWithValue("$detail", $"Assigned explicit ledger identities to {sales.Count.ToString(CultureInfo.InvariantCulture)} historical sale row(s). Unresolved conflicts: {CountUnresolvedLedgerConflicts(conn, tx).ToString(CultureInfo.InvariantCulture)}.");
            audit.ExecuteNonQuery();
        }

        using (var migrationRecord = conn.CreateCommand())
        {
            migrationRecord.Transaction = tx;
            migrationRecord.CommandText = """
                INSERT OR IGNORE INTO schema_migrations (version, applied_at_utc, description)
                VALUES (17, $applied_at_utc, $description)
                """;
            migrationRecord.Parameters.AddWithValue("$applied_at_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            migrationRecord.Parameters.AddWithValue("$description", "Assign immutable ledger intervals, actors, sale IDs, and historical claim conflicts");
            migrationRecord.ExecuteNonQuery();
        }
        UpsertSetting(conn, tx, "schema_version", "17");

        tx.Commit();
    }

    private static void EnsureLedgerRuntimeState(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        EnsureActor(conn, tx, SystemActorId, "System", "system");
        EnsureActor(conn, tx, LegacyActorId, "Legacy—actor unknown", "migration");
        _ = GetOrCreateActorSetting(conn, tx, "manager_actor_id", "Manager", "manager");
        var clerkName = ReadSetting(conn, tx, "clerk_name");
        _ = GetOrCreateActorSetting(conn, tx, "clerk_actor_id", string.IsNullOrWhiteSpace(clerkName) ? "Clerk" : clerkName, "clerk");
        if (QueryOpenIntervalId(conn, tx) == 0)
        {
            InsertLedgerInterval(
                conn,
                tx,
                "open",
                DateTime.UtcNow,
                null,
                SystemActorId,
                null,
                null,
                string.Empty,
                0,
                "Recovered missing open interval");
        }
        tx.Commit();
    }

    private static void CreateLedgerIntegrityTriggers(SqliteConnection conn)
    {
        Exec(conn, """
            CREATE TRIGGER IF NOT EXISTS trg_sales_require_ledger_identity
            BEFORE INSERT ON sales
            WHEN NEW.interval_id IS NULL OR NEW.interval_id <= 0 OR
                 trim(NEW.actor_id) = '' OR trim(NEW.actor_name) = ''
            BEGIN
                SELECT RAISE(ABORT, 'Sale requires an open interval and actor identity');
            END
            """);
        Exec(conn, """
            CREATE TRIGGER IF NOT EXISTS trg_sales_require_open_interval
            BEFORE INSERT ON sales
            WHEN NOT EXISTS (
                SELECT 1 FROM ledger_intervals WHERE id = NEW.interval_id AND status = 'open')
            BEGIN
                SELECT RAISE(ABORT, 'Sale interval is not open');
            END
            """);
        Exec(conn, """
            CREATE TRIGGER IF NOT EXISTS trg_sales_require_financial_shape
            BEFORE INSERT ON sales
            WHEN (NEW.corrects_sale_id IS NULL AND (NEW.quantity <= 0 OR NEW.amount_cents <= 0)) OR
                 (NEW.corrects_sale_id IS NOT NULL AND (
                     NEW.quantity >= 0 OR NEW.amount_cents >= 0 OR lower(NEW.source) <> 'undo' OR
                     NOT EXISTS (
                         SELECT 1 FROM sales
                         WHERE id = NEW.corrects_sale_id AND quantity > 0 AND amount_cents > 0)))
            BEGIN
                SELECT RAISE(ABORT, 'Sale or correction has an invalid financial shape');
            END
            """);
        Exec(conn, """
            CREATE TRIGGER IF NOT EXISTS trg_sales_require_known_actor
            BEFORE INSERT ON sales
            WHEN NOT EXISTS (SELECT 1 FROM actors WHERE id = NEW.actor_id AND is_active = 1)
            BEGIN
                SELECT RAISE(ABORT, 'Sale actor is not active');
            END
            """);
        Exec(conn, """
            CREATE TRIGGER IF NOT EXISTS trg_sales_append_only_update
            BEFORE UPDATE ON sales
            WHEN NOT EXISTS (
                SELECT 1 FROM ledger_mutation_guard
                WHERE id = 1 AND purpose = 'closing_reverse')
            BEGIN
                SELECT RAISE(ABORT, 'Sales ledger rows are immutable');
            END
            """);
        Exec(conn, """
            CREATE TRIGGER IF NOT EXISTS trg_sales_append_only_delete
            BEFORE DELETE ON sales
            WHEN NOT EXISTS (
                SELECT 1 FROM ledger_mutation_guard
                WHERE id = 1 AND purpose = 'closing_reverse')
            BEGIN
                SELECT RAISE(ABORT, 'Sales ledger rows are immutable');
            END
            """);
        Exec(conn, """
            CREATE TRIGGER IF NOT EXISTS trg_claims_require_sale
            BEFORE INSERT ON sale_ticket_claims
            WHEN NEW.sale_id IS NULL OR NEW.sale_id <= 0 OR
                 NOT EXISTS (SELECT 1 FROM sales WHERE id = NEW.sale_id)
            BEGIN
                SELECT RAISE(ABORT, 'Ticket claim requires a persistent sale ID');
            END
            """);
        Exec(conn, """
            CREATE TRIGGER IF NOT EXISTS trg_claims_append_only_update
            BEFORE UPDATE ON sale_ticket_claims
            WHEN NOT EXISTS (
                SELECT 1 FROM ledger_mutation_guard
                WHERE id = 1 AND purpose = 'closing_reverse')
            BEGIN
                SELECT RAISE(ABORT, 'Ticket claims are immutable');
            END
            """);
        Exec(conn, """
            CREATE TRIGGER IF NOT EXISTS trg_claims_append_only_delete
            BEFORE DELETE ON sale_ticket_claims
            WHEN NOT EXISTS (
                SELECT 1 FROM ledger_mutation_guard
                WHERE id = 1 AND purpose = 'closing_reverse')
            BEGIN
                SELECT RAISE(ABORT, 'Ticket claims are immutable');
            END
            """);
        Exec(conn, """
            CREATE TRIGGER IF NOT EXISTS trg_activation_require_open_interval
            BEFORE INSERT ON activation_events
            WHEN NEW.interval_id IS NULL OR NEW.interval_id <= 0 OR trim(NEW.actor_id) = '' OR
                 NOT EXISTS (SELECT 1 FROM ledger_intervals WHERE id = NEW.interval_id AND status = 'open') OR
                 NOT EXISTS (SELECT 1 FROM actors WHERE id = NEW.actor_id AND is_active = 1)
            BEGIN
                SELECT RAISE(ABORT, 'Activation requires an open interval and actor identity');
            END
            """);
        Exec(conn, """
            CREATE TRIGGER IF NOT EXISTS trg_activation_append_only_update
            BEFORE UPDATE ON activation_events
            BEGIN
                SELECT RAISE(ABORT, 'Activation ledger rows are immutable');
            END
            """);
        Exec(conn, """
            CREATE TRIGGER IF NOT EXISTS trg_activation_append_only_delete
            BEFORE DELETE ON activation_events
            BEGIN
                SELECT RAISE(ABORT, 'Activation ledger rows are immutable');
            END
            """);
        Exec(conn, """
            CREATE TRIGGER IF NOT EXISTS trg_closed_intervals_immutable
            BEFORE UPDATE ON ledger_intervals
            WHEN OLD.status = 'closed'
            BEGIN
                SELECT RAISE(ABORT, 'Closed ledger intervals are immutable');
            END
            """);
        Exec(conn, """
            CREATE TRIGGER IF NOT EXISTS trg_closed_intervals_no_delete
            BEFORE DELETE ON ledger_intervals
            WHEN OLD.status = 'closed'
            BEGIN
                SELECT RAISE(ABORT, 'Closed ledger intervals are immutable');
            END
            """);
        Exec(conn, """
            CREATE TRIGGER IF NOT EXISTS trg_closing_history_append_only_update
            BEFORE UPDATE ON closing_history
            BEGIN
                SELECT RAISE(ABORT, 'Closing history is immutable');
            END
            """);
        Exec(conn, """
            CREATE TRIGGER IF NOT EXISTS trg_closing_history_append_only_delete
            BEFORE DELETE ON closing_history
            BEGIN
                SELECT RAISE(ABORT, 'Closing history is immutable');
            END
            """);
    }

    private static void DropLedgerIntegrityTriggers(SqliteConnection conn)
    {
        var triggerNames = new[]
        {
            "trg_sales_require_ledger_identity",
            "trg_sales_require_open_interval",
            "trg_sales_require_known_actor",
            "trg_sales_require_financial_shape",
            "trg_sales_append_only_update",
            "trg_sales_append_only_delete",
            "trg_claims_require_sale",
            "trg_claims_append_only_update",
            "trg_claims_append_only_delete",
            "trg_activation_require_open_interval",
            "trg_activation_append_only_update",
            "trg_activation_append_only_delete",
            "trg_closed_intervals_immutable",
            "trg_closed_intervals_no_delete",
            "trg_closing_history_append_only_update",
            "trg_closing_history_append_only_delete"
        };
        foreach (var triggerName in triggerNames)
            Exec(conn, $"DROP TRIGGER IF EXISTS {triggerName}");
    }

    private static void EnsureActor(
        SqliteConnection conn,
        SqliteTransaction tx,
        string actorId,
        string displayName,
        string role)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO actors (id, display_name, role, is_active, created_at_utc)
            VALUES ($id, $display_name, $role, 1, $created_at_utc)
            ON CONFLICT(id) DO UPDATE SET
                display_name = excluded.display_name,
                role = excluded.role,
                is_active = 1
            """;
        cmd.Parameters.AddWithValue("$id", actorId);
        cmd.Parameters.AddWithValue("$display_name", displayName);
        cmd.Parameters.AddWithValue("$role", role);
        cmd.Parameters.AddWithValue("$created_at_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    private static string GetOrCreateActorSetting(
        SqliteConnection conn,
        SqliteTransaction tx,
        string settingKey,
        string displayName,
        string role)
    {
        var actorId = ReadSetting(conn, tx, settingKey);
        if (string.IsNullOrWhiteSpace(actorId))
        {
            actorId = $"actor-{Guid.NewGuid():N}";
            UpsertSetting(conn, tx, settingKey, actorId);
        }

        EnsureActor(conn, tx, actorId, displayName, role);
        return actorId;
    }

    private static string ReadSetting(SqliteConnection conn, SqliteTransaction tx, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT value FROM settings WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string ?? string.Empty;
    }

    private static List<MigrationClosing> QueryMigrationClosings(SqliteConnection conn, SqliteTransaction tx)
    {
        var rows = new List<MigrationClosing>();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT id, closed_at_utc, interval_start_utc, business_date, shift_sequence,
                   sales_count, ticket_count, sales_cents, activated_bundles
            FROM closing_history
            ORDER BY id
            """;
        using var reader = cmd.ExecuteReader();
        var previousClosedAtUtc = DateTime.MinValue;
        while (reader.Read())
        {
            var closedAtUtc = TryReadLedgerDate(reader.GetString(1), out var parsedClosedAtUtc)
                ? parsedClosedAtUtc
                : previousClosedAtUtc;
            var intervalStartUtc = !reader.IsDBNull(2) && TryReadLedgerDate(reader.GetString(2), out var parsedStartUtc)
                ? parsedStartUtc
                : previousClosedAtUtc;
            rows.Add(new MigrationClosing(
                reader.GetInt64(0),
                closedAtUtc,
                intervalStartUtc,
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt64(7),
                reader.GetInt32(8)));
            previousClosedAtUtc = closedAtUtc;
        }
        return rows;
    }

    private static List<MigrationSale> QueryMigrationSales(SqliteConnection conn, SqliteTransaction tx)
    {
        var rows = new List<MigrationSale>();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT id, sold_at_utc, game_id, bundle_id, bin, ticket, quantity, amount_cents, source
            FROM sales
            ORDER BY id
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var soldAtUtc = TryReadLedgerDate(reader.GetString(1), out var parsedSoldAtUtc)
                ? parsedSoldAtUtc
                : DateTime.MinValue;
            rows.Add(new MigrationSale(
                reader.GetInt64(0),
                soldAtUtc,
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt32(6),
                reader.GetInt64(7),
                reader.GetString(8)));
        }
        return rows;
    }

    private static long InsertLedgerInterval(
        SqliteConnection conn,
        SqliteTransaction tx,
        string status,
        DateTime openedAtUtc,
        DateTime? closedAtUtc,
        string openedByActorId,
        string? closedByActorId,
        long? closingHistoryId,
        string businessDate,
        int shiftSequence,
        string migrationNote)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO ledger_intervals (
                status, opened_at_utc, closed_at_utc, opened_by_actor_id, closed_by_actor_id,
                closing_history_id, business_date, shift_sequence, migration_note)
            VALUES (
                $status, $opened_at_utc, $closed_at_utc, $opened_by_actor_id, $closed_by_actor_id,
                $closing_history_id, $business_date, $shift_sequence, $migration_note)
            """;
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$opened_at_utc", openedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$closed_at_utc", closedAtUtc is null
            ? DBNull.Value
            : closedAtUtc.Value.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$opened_by_actor_id", openedByActorId);
        cmd.Parameters.AddWithValue("$closed_by_actor_id", closedByActorId is null ? DBNull.Value : closedByActorId);
        cmd.Parameters.AddWithValue("$closing_history_id", closingHistoryId is null ? DBNull.Value : closingHistoryId.Value);
        cmd.Parameters.AddWithValue("$business_date", businessDate);
        cmd.Parameters.AddWithValue("$shift_sequence", shiftSequence);
        cmd.Parameters.AddWithValue("$migration_note", migrationNote);
        cmd.ExecuteNonQuery();
        using var idCmd = conn.CreateCommand();
        idCmd.Transaction = tx;
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return Convert.ToInt64(idCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void UpdateClosingLedgerIdentity(
        SqliteConnection conn,
        SqliteTransaction tx,
        long closingId,
        long intervalId,
        string actorId,
        string actorName)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE closing_history
            SET interval_id = $interval_id,
                closed_by_actor_id = $actor_id,
                closed_by_actor_name = $actor_name
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$interval_id", intervalId);
        cmd.Parameters.AddWithValue("$actor_id", actorId);
        cmd.Parameters.AddWithValue("$actor_name", actorName);
        cmd.Parameters.AddWithValue("$id", closingId);
        cmd.ExecuteNonQuery();
    }

    private static void AssignSaleLedgerIdentity(
        SqliteConnection conn,
        SqliteTransaction tx,
        long saleId,
        long intervalId,
        string actorId,
        string actorName,
        string migrationState)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE sales
            SET interval_id = $interval_id,
                actor_id = $actor_id,
                actor_name = $actor_name,
                migration_state = $migration_state
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$interval_id", intervalId);
        cmd.Parameters.AddWithValue("$actor_id", actorId);
        cmd.Parameters.AddWithValue("$actor_name", actorName);
        cmd.Parameters.AddWithValue("$migration_state", migrationState);
        cmd.Parameters.AddWithValue("$id", saleId);
        cmd.ExecuteNonQuery();
    }

    private static long EnsureLegacyUnresolvedInterval(
        SqliteConnection conn,
        SqliteTransaction tx,
        long currentIntervalId)
    {
        if (currentIntervalId > 0)
            return currentIntervalId;

        return InsertLedgerInterval(
            conn,
            tx,
            "legacy_unresolved",
            DateTime.MinValue,
            null,
            LegacyActorId,
            null,
            null,
            string.Empty,
            0,
            "Historical rows requiring manager review");
    }

    private static void InsertLedgerConflict(
        SqliteConnection conn,
        SqliteTransaction tx,
        string conflictType,
        string severity,
        long? saleId,
        long? conflictingSaleId,
        string gameId,
        string bundleId,
        int? ticketSerial,
        string detail)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO ledger_migration_conflicts (
                conflict_type, severity, sale_id, conflicting_sale_id, game_id, bundle_id,
                ticket_serial, detail, status, detected_at_utc)
            VALUES (
                $conflict_type, $severity, $sale_id, $conflicting_sale_id, $game_id, $bundle_id,
                $ticket_serial, $detail, 'unresolved', $detected_at_utc)
            """;
        cmd.Parameters.AddWithValue("$conflict_type", conflictType);
        cmd.Parameters.AddWithValue("$severity", severity);
        cmd.Parameters.AddWithValue("$sale_id", saleId is null ? DBNull.Value : saleId.Value);
        cmd.Parameters.AddWithValue("$conflicting_sale_id", conflictingSaleId is null ? DBNull.Value : conflictingSaleId.Value);
        cmd.Parameters.AddWithValue("$game_id", gameId);
        cmd.Parameters.AddWithValue("$bundle_id", bundleId);
        cmd.Parameters.AddWithValue("$ticket_serial", ticketSerial is null ? DBNull.Value : ticketSerial.Value);
        cmd.Parameters.AddWithValue("$detail", detail);
        cmd.Parameters.AddWithValue("$detected_at_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    private static void MigrateActivationIntervals(
        SqliteConnection conn,
        SqliteTransaction tx,
        IReadOnlyList<MigrationClosing> closings,
        IReadOnlyDictionary<long, long> intervalByClosingId,
        long openIntervalId,
        ref long unresolvedIntervalId)
    {
        var events = new List<MigrationActivation>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT id, activated_at_utc, game_id, bundle_id FROM activation_events ORDER BY id";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                events.Add(new MigrationActivation(
                    reader.GetInt64(0),
                    TryReadLedgerDate(reader.GetString(1), out var activatedAtUtc) ? activatedAtUtc : DateTime.MinValue,
                    reader.GetString(2),
                    reader.GetString(3)));
            }
        }

        var unassigned = events.Select(row => row.Id).ToHashSet();
        foreach (var closing in closings)
        {
            var candidates = events
                .Where(row => unassigned.Contains(row.Id) && row.ActivatedAtUtc > closing.IntervalStartUtc && row.ActivatedAtUtc <= closing.ClosedAtUtc)
                .ToList();
            var intervalId = candidates.Count == closing.ActivatedBundles
                ? intervalByClosingId[closing.Id]
                : EnsureLegacyUnresolvedInterval(conn, tx, unresolvedIntervalId);
            if (candidates.Count != closing.ActivatedBundles)
            {
                unresolvedIntervalId = intervalId;
                InsertLedgerConflict(
                    conn,
                    tx,
                    "activation_interval_mismatch",
                    "warning",
                    null,
                    null,
                    string.Empty,
                    string.Empty,
                    null,
                    $"Closing {closing.BusinessDate} #{closing.ShiftSequence.ToString(CultureInfo.InvariantCulture)} expected {closing.ActivatedBundles.ToString(CultureInfo.InvariantCulture)} activation event(s), but timestamp inference found {candidates.Count.ToString(CultureInfo.InvariantCulture)}.");
            }

            foreach (var activation in candidates)
            {
                AssignActivationLedgerIdentity(conn, tx, activation.Id, intervalId);
                unassigned.Remove(activation.Id);
            }
        }

        var lastClosedAtUtc = closings.Count == 0 ? DateTime.MinValue : closings[^1].ClosedAtUtc;
        foreach (var activation in events.Where(row => unassigned.Contains(row.Id)))
        {
            var intervalId = closings.Count == 0 || activation.ActivatedAtUtc > lastClosedAtUtc
                ? openIntervalId
                : EnsureLegacyUnresolvedInterval(conn, tx, unresolvedIntervalId);
            if (intervalId != openIntervalId)
                unresolvedIntervalId = intervalId;
            AssignActivationLedgerIdentity(conn, tx, activation.Id, intervalId);
        }
    }

    private static void AssignActivationLedgerIdentity(
        SqliteConnection conn,
        SqliteTransaction tx,
        long activationId,
        long intervalId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE activation_events
            SET interval_id = $interval_id,
                actor_id = $actor_id,
                actor_name = $actor_name
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$interval_id", intervalId);
        cmd.Parameters.AddWithValue("$actor_id", LegacyActorId);
        cmd.Parameters.AddWithValue("$actor_name", "Legacy—actor unknown");
        cmd.Parameters.AddWithValue("$id", activationId);
        cmd.ExecuteNonQuery();
    }

    private static void RebuildHistoricalTicketClaims(
        SqliteConnection conn,
        SqliteTransaction tx,
        IReadOnlyList<MigrationSale> sales)
    {
        Exec(conn, tx, "CREATE TABLE IF NOT EXISTS sale_ticket_claims_legacy_v15 AS SELECT game_id, bundle_id, ticket_serial, claimed_at_utc FROM sale_ticket_claims");
        var legacyClaims = new HashSet<string>(StringComparer.Ordinal);
        using (var legacyCmd = conn.CreateCommand())
        {
            legacyCmd.Transaction = tx;
            legacyCmd.CommandText = "SELECT game_id, bundle_id, ticket_serial FROM sale_ticket_claims_legacy_v15";
            using var reader = legacyCmd.ExecuteReader();
            while (reader.Read())
                legacyClaims.Add(TicketClaimKey(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        }

        Exec(conn, tx, "DELETE FROM sale_ticket_claims");
        var rebuiltClaims = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sale in sales.Where(row => row.Quantity > 0 && row.AmountCents > 0))
        {
            if (string.IsNullOrWhiteSpace(sale.BundleId))
            {
                InsertLedgerConflict(conn, tx, "historical_claim_missing_bundle", "warning", sale.Id, null, sale.GameId, sale.BundleId, null, "Historical sale has no Bundle ID, so its physical ticket claims could not be reconstructed.");
                continue;
            }

            List<int> serials;
            try
            {
                serials = ParseSaleTicketSerials(sale.Ticket);
            }
            catch (Exception ex)
            {
                InsertLedgerConflict(conn, tx, "historical_claim_parse_failed", "warning", sale.Id, null, sale.GameId, sale.BundleId, null, ex.Message);
                continue;
            }

            if (serials.Count != sale.Quantity)
            {
                InsertLedgerConflict(conn, tx, "historical_claim_quantity_mismatch", "warning", sale.Id, null, sale.GameId, sale.BundleId, null, $"Ticket range contains {serials.Count.ToString(CultureInfo.InvariantCulture)} ticket(s), but sale quantity is {sale.Quantity.ToString(CultureInfo.InvariantCulture)}.");
                continue;
            }

            foreach (var serial in serials)
            {
                using var claim = conn.CreateCommand();
                claim.Transaction = tx;
                claim.CommandText = """
                    INSERT OR IGNORE INTO sale_ticket_claims (
                        game_id, bundle_id, ticket_serial, claimed_at_utc, sale_id)
                    VALUES ($game_id, $bundle_id, $ticket_serial, $claimed_at_utc, $sale_id)
                    """;
                claim.Parameters.AddWithValue("$game_id", NormalizeLedgerKey(sale.GameId));
                claim.Parameters.AddWithValue("$bundle_id", NormalizeLedgerKey(sale.BundleId));
                claim.Parameters.AddWithValue("$ticket_serial", serial);
                claim.Parameters.AddWithValue("$claimed_at_utc", sale.SoldAtUtc.ToString("O", CultureInfo.InvariantCulture));
                claim.Parameters.AddWithValue("$sale_id", sale.Id);
                if (claim.ExecuteNonQuery() == 0)
                {
                    var ownerSaleId = QueryTicketClaimOwner(conn, tx, sale.GameId, sale.BundleId, serial);
                    InsertLedgerConflict(
                        conn,
                        tx,
                        "duplicate_historical_ticket_claim",
                        "warning",
                        sale.Id,
                        ownerSaleId,
                        sale.GameId,
                        sale.BundleId,
                        serial,
                        $"Historical sales {ownerSaleId.ToString(CultureInfo.InvariantCulture)} and {sale.Id.ToString(CultureInfo.InvariantCulture)} both claim the same physical ticket. The earlier sale retains the protective claim; neither financial row was changed.");
                    continue;
                }

                rebuiltClaims.Add(TicketClaimKey(sale.GameId, sale.BundleId, serial));
            }
        }

        foreach (var orphan in legacyClaims.Where(key => !rebuiltClaims.Contains(key)))
        {
            InsertLedgerConflict(
                conn,
                tx,
                "orphan_v15_ticket_claim",
                "warning",
                null,
                null,
                string.Empty,
                string.Empty,
                null,
                $"A schema-v15 ticket claim could not be linked to a historical sale: {orphan}.");
        }
    }

    private static void BackfillHistoricalVoids(
        SqliteConnection conn,
        SqliteTransaction tx,
        IReadOnlyList<MigrationSale> sales)
    {
        var salesByLegacyKey = sales
            .Where(sale => sale.Quantity > 0 && sale.AmountCents > 0)
            .GroupBy(LegacySaleIdentity, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var availableCorrections = sales
            .Where(sale => sale.Quantity < 0 && sale.AmountCents < 0 && string.Equals(sale.Source, "undo", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var linkedCorrections = new HashSet<long>();

        using var select = conn.CreateCommand();
        select.Transaction = tx;
        select.CommandText = "SELECT sale_key FROM sale_voids WHERE original_sale_id IS NULL ORDER BY voided_at_utc";
        using var reader = select.ExecuteReader();
        var voidKeys = new List<string>();
        while (reader.Read())
            voidKeys.Add(reader.GetString(0));
        reader.Close();

        foreach (var saleKey in voidKeys)
        {
            if (!salesByLegacyKey.TryGetValue(saleKey, out var originals) || originals.Count != 1)
            {
                InsertLedgerConflict(conn, tx, "legacy_void_original_ambiguous", "warning", null, null, string.Empty, string.Empty, null, $"Legacy void key could not be linked to exactly one sale: {saleKey}.");
                continue;
            }

            var original = originals[0];
            var corrections = availableCorrections
                .Where(correction => !linkedCorrections.Contains(correction.Id) &&
                    string.Equals(correction.GameId, original.GameId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(correction.BundleId, original.BundleId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(correction.Bin, original.Bin, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(correction.Ticket, original.Ticket, StringComparison.OrdinalIgnoreCase) &&
                    correction.Quantity == -original.Quantity &&
                    correction.AmountCents == -original.AmountCents)
                .ToList();
            if (corrections.Count != 1)
            {
                InsertLedgerConflict(conn, tx, "legacy_void_correction_ambiguous", "warning", original.Id, null, original.GameId, original.BundleId, null, "Legacy void could not be linked to exactly one correction row.");
                continue;
            }

            var correction = corrections[0];
            using (var updateSale = conn.CreateCommand())
            {
                updateSale.Transaction = tx;
                updateSale.CommandText = "UPDATE sales SET corrects_sale_id = $original_sale_id WHERE id = $correction_sale_id";
                updateSale.Parameters.AddWithValue("$original_sale_id", original.Id);
                updateSale.Parameters.AddWithValue("$correction_sale_id", correction.Id);
                updateSale.ExecuteNonQuery();
            }
            using (var updateVoid = conn.CreateCommand())
            {
                updateVoid.Transaction = tx;
                updateVoid.CommandText = """
                    UPDATE sale_voids
                    SET original_sale_id = $original_sale_id,
                        correction_sale_id = $correction_sale_id,
                        actor_id = $actor_id
                    WHERE sale_key = $sale_key
                    """;
                updateVoid.Parameters.AddWithValue("$original_sale_id", original.Id);
                updateVoid.Parameters.AddWithValue("$correction_sale_id", correction.Id);
                updateVoid.Parameters.AddWithValue("$actor_id", LegacyActorId);
                updateVoid.Parameters.AddWithValue("$sale_key", saleKey);
                updateVoid.ExecuteNonQuery();
            }
            linkedCorrections.Add(correction.Id);
        }

        foreach (var correction in availableCorrections.Where(row => !linkedCorrections.Contains(row.Id)))
        {
            InsertLedgerConflict(conn, tx, "orphan_historical_correction", "warning", correction.Id, null, correction.GameId, correction.BundleId, null, "Historical correction could not be linked to an original sale ID.");
        }
    }

    private static long QueryTicketClaimOwner(
        SqliteConnection conn,
        SqliteTransaction tx,
        string gameId,
        string bundleId,
        int serial)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT sale_id
            FROM sale_ticket_claims
            WHERE game_id = $game_id AND bundle_id = $bundle_id AND ticket_serial = $ticket_serial
            """;
        cmd.Parameters.AddWithValue("$game_id", NormalizeLedgerKey(gameId));
        cmd.Parameters.AddWithValue("$bundle_id", NormalizeLedgerKey(bundleId));
        cmd.Parameters.AddWithValue("$ticket_serial", serial);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static int CountUnresolvedLedgerConflicts(SqliteConnection conn, SqliteTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM ledger_migration_conflicts WHERE status = 'unresolved'";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static long QueryOpenIntervalId(SqliteConnection conn, SqliteTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id FROM ledger_intervals WHERE status = 'open' ORDER BY id DESC LIMIT 1";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    private static long QueryOpenIntervalId(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM ledger_intervals WHERE status = 'open' ORDER BY id DESC LIMIT 1";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    private static string NormalizeLedgerKey(string value) => (value ?? string.Empty).Trim().ToUpperInvariant();

    private static string TicketClaimKey(string gameId, string bundleId, int serial) =>
        $"{NormalizeLedgerKey(gameId)}|{NormalizeLedgerKey(bundleId)}|{serial.ToString(CultureInfo.InvariantCulture)}";

    private static string LegacySaleIdentity(MigrationSale sale) =>
        string.Join("|",
            sale.SoldAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            sale.GameId,
            sale.Bin,
            sale.Ticket,
            sale.Quantity.ToString(CultureInfo.InvariantCulture),
            sale.AmountCents.ToString(CultureInfo.InvariantCulture),
            sale.Source);

    private static bool TryReadLedgerDate(string value, out DateTime parsedUtc)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            parsedUtc = parsed.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
                : parsed.ToUniversalTime();
            return true;
        }

        parsedUtc = DateTime.MinValue;
        return false;
    }

    private static void RepairDuplicateImports(SqliteConnection conn)
    {
        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = """
            SELECT COALESCE(SUM(bundle_count - 1), 0)
            FROM (
                SELECT COUNT(*) AS bundle_count
                FROM imports
                GROUP BY lower(trim(game_id)), lower(trim(bundle_id))
                HAVING COUNT(*) > 1)
            """;
        var duplicateCount = Convert.ToInt32(countCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (duplicateCount <= 0)
            return;

        Exec(conn, """
            DELETE FROM imports
            WHERE id NOT IN (
                SELECT MAX(id)
                FROM imports
                GROUP BY lower(trim(game_id)), lower(trim(bundle_id)))
            """);

        using var auditCmd = conn.CreateCommand();
        auditCmd.CommandText = """
            INSERT INTO audit_log (occurred_at_utc, category, action, actor, actor_id, detail)
            VALUES ($occurred_at_utc, 'inventory', 'Duplicate active bundles repaired', 'System', $actor_id, $detail)
            """;
        auditCmd.Parameters.AddWithValue("$occurred_at_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        auditCmd.Parameters.AddWithValue("$actor_id", SystemActorId);
        auditCmd.Parameters.AddWithValue("$detail", $"Removed {duplicateCount.ToString(CultureInfo.InvariantCulture)} duplicate active bundle record(s); kept the most recently recorded placement for each Game ID + Bundle ID.");
        auditCmd.ExecuteNonQuery();
    }

    private static void RecordSchemaMigration(SqliteConnection conn, int version, int previousVersion, string description)
    {
        if (previousVersion >= version)
            return;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO schema_migrations (version, applied_at_utc, description)
            VALUES ($version, $applied_at_utc, $description)
            """;
        cmd.Parameters.AddWithValue("$version", version);
        cmd.Parameters.AddWithValue("$applied_at_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$description", description);
        cmd.ExecuteNonQuery();
    }

    private static List<StoredImportLine> QueryImports(SqliteConnection conn)
    {
        var rows = new List<StoredImportLine>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT game_id, bundle_id, ticket, bin, source, is_sold_out FROM imports ORDER BY id DESC";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(new StoredImportLine(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5) != 0));
        return rows;
    }

    private static List<StoredReceivedBundle> QueryReceivedBundles(SqliteConnection conn)
    {
        var rows = new List<StoredReceivedBundle>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT game_id, bundle_id, received_at_utc, source FROM received_inventory ORDER BY id DESC";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var receivedAt = DateTime.TryParse(
                reader.GetString(2),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed)
                ? parsed
                : DateTime.UtcNow;
            rows.Add(new StoredReceivedBundle(reader.GetString(0), reader.GetString(1), receivedAt, reader.GetString(3)));
        }

        return rows;
    }

    private static List<StoredActivationRecord> QueryActivations(SqliteConnection conn)
    {
        var rows = new List<StoredActivationRecord>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT activated_at_utc, game_id, bundle_id, bin, source, interval_id, actor_id, actor_name FROM activation_events ORDER BY id DESC";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new StoredActivationRecord(
                ReadDateTime(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                reader.GetString(6),
                reader.GetString(7)));
        }

        return rows;
    }

    private static List<StoredSaleLine> QuerySales(SqliteConnection conn)
    {
        var rows = new List<StoredSaleLine>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, sold_at_utc, game_id, bundle_id, bin, ticket, quantity, amount_cents, source,
                   interval_id, actor_id, actor_name, corrects_sale_id, migration_state
            FROM sales
            ORDER BY id DESC
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var soldAt = DateTime.TryParse(
                reader.GetString(1),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed)
                ? parsed
                : DateTime.UtcNow;
            rows.Add(new StoredSaleLine(
                soldAt,
                reader.GetString(2),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt32(6),
                reader.GetInt64(7),
                reader.GetString(8),
                reader.GetString(3),
                reader.GetInt64(0),
                reader.IsDBNull(9) ? 0 : reader.GetInt64(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetInt64(12),
                reader.GetString(13)));
        }
        return rows;
    }

    private static StoredSaleLine InsertSaleRow(
        SqliteConnection conn,
        SqliteTransaction tx,
        StoredSaleLine sale)
    {
        if (string.IsNullOrWhiteSpace(sale.ActorId) || string.IsNullOrWhiteSpace(sale.ActorName))
            throw new InvalidOperationException("A sale must identify the logged-in actor.");

        var intervalId = sale.IntervalId > 0 ? sale.IntervalId : QueryOpenIntervalId(conn, tx);
        if (intervalId <= 0)
            throw new InvalidOperationException("No open ledger interval is available for this sale.");

        using (var intervalCmd = conn.CreateCommand())
        {
            intervalCmd.Transaction = tx;
            intervalCmd.CommandText = "SELECT COUNT(*) FROM ledger_intervals WHERE id = $id AND status = 'open'";
            intervalCmd.Parameters.AddWithValue("$id", intervalId);
            if (Convert.ToInt32(intervalCmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                throw new InvalidOperationException("The selected ledger interval is no longer open.");
        }

        if (sale.CorrectsSaleId is null)
        {
            if (sale.Quantity <= 0 || sale.AmountCents <= 0)
                throw new InvalidOperationException("A recorded sale must have a positive quantity and amount.");
        }
        else if (sale.Quantity >= 0 || sale.AmountCents >= 0 || !string.Equals(sale.Source, "undo", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A sale correction must be a negative undo row.");
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO sales (
                sold_at_utc, game_id, bundle_id, bin, ticket, quantity, amount_cents, source,
                interval_id, actor_id, actor_name, corrects_sale_id, migration_state)
            VALUES (
                $sold_at_utc, $game_id, $bundle_id, $bin, $ticket, $quantity, $amount_cents, $source,
                $interval_id, $actor_id, $actor_name, $corrects_sale_id, 'native')
            """;
        cmd.Parameters.AddWithValue("$sold_at_utc", sale.SoldAtUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$game_id", sale.GameId);
        cmd.Parameters.AddWithValue("$bundle_id", sale.BundleId);
        cmd.Parameters.AddWithValue("$bin", sale.Bin);
        cmd.Parameters.AddWithValue("$ticket", sale.Ticket);
        cmd.Parameters.AddWithValue("$quantity", sale.Quantity);
        cmd.Parameters.AddWithValue("$amount_cents", sale.AmountCents);
        cmd.Parameters.AddWithValue("$source", sale.Source);
        cmd.Parameters.AddWithValue("$interval_id", intervalId);
        cmd.Parameters.AddWithValue("$actor_id", sale.ActorId);
        cmd.Parameters.AddWithValue("$actor_name", sale.ActorName);
        cmd.Parameters.AddWithValue("$corrects_sale_id", sale.CorrectsSaleId is null ? DBNull.Value : sale.CorrectsSaleId.Value);
        try
        {
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19 && sale.CorrectsSaleId is not null)
        {
            throw new InvalidOperationException("This sale has already been voided.", ex);
        }

        using var idCmd = conn.CreateCommand();
        idCmd.Transaction = tx;
        idCmd.CommandText = "SELECT last_insert_rowid()";
        var saleId = Convert.ToInt64(idCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        return sale with
        {
            Id = saleId,
            IntervalId = intervalId,
            MigrationState = "native"
        };
    }

    private static List<StoredSaleLine> AttachGeneratedSaleIds(
        IReadOnlyList<StoredSaleLine> reportSales,
        IReadOnlyList<StoredSaleLine> generatedSales)
    {
        var unmatchedGenerated = generatedSales.ToList();
        var persisted = new List<StoredSaleLine>(reportSales.Count);
        foreach (var reportSale in reportSales)
        {
            if (reportSale.Id > 0)
            {
                persisted.Add(reportSale);
                continue;
            }

            var matchIndex = unmatchedGenerated.FindIndex(generated =>
                generated.SoldAtUtc == reportSale.SoldAtUtc &&
                string.Equals(generated.GameId, reportSale.GameId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(generated.BundleId, reportSale.BundleId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(generated.Bin, reportSale.Bin, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(generated.Ticket, reportSale.Ticket, StringComparison.OrdinalIgnoreCase) &&
                generated.Quantity == reportSale.Quantity &&
                generated.AmountCents == reportSale.AmountCents &&
                string.Equals(generated.Source, reportSale.Source, StringComparison.OrdinalIgnoreCase));
            if (matchIndex < 0)
                throw new InvalidOperationException("Closing report contains a sale without a persistent ledger ID.");

            persisted.Add(unmatchedGenerated[matchIndex]);
            unmatchedGenerated.RemoveAt(matchIndex);
        }

        if (unmatchedGenerated.Count > 0)
            throw new InvalidOperationException("Closing generated sales do not match the report snapshot.");
        return persisted;
    }

    private static void ClaimSaleTickets(SqliteConnection conn, SqliteTransaction tx, StoredSaleLine sale)
    {
        if (sale.Quantity <= 0)
            throw new InvalidOperationException("A recorded sale must have a positive ticket quantity.");

        if (sale.AmountCents <= 0)
            throw new InvalidOperationException("A recorded sale must have a positive amount.");

        if (string.IsNullOrWhiteSpace(sale.BundleId))
            throw new InvalidOperationException("A sale must identify its bundle before it can be recorded.");

        var serials = ParseSaleTicketSerials(sale.Ticket);
        if (serials.Count != sale.Quantity)
            throw new InvalidOperationException("Sale ticket range does not match its ticket quantity.");

        foreach (var serial in serials)
        {
            using var claimCmd = conn.CreateCommand();
            claimCmd.Transaction = tx;
            claimCmd.CommandText = """
                INSERT INTO sale_ticket_claims (game_id, bundle_id, ticket_serial, claimed_at_utc, sale_id)
                VALUES ($game_id, $bundle_id, $ticket_serial, $claimed_at_utc, $sale_id)
                """;
            claimCmd.Parameters.AddWithValue("$game_id", NormalizeLedgerKey(sale.GameId));
            claimCmd.Parameters.AddWithValue("$bundle_id", NormalizeLedgerKey(sale.BundleId));
            claimCmd.Parameters.AddWithValue("$ticket_serial", serial);
            claimCmd.Parameters.AddWithValue("$claimed_at_utc", sale.SoldAtUtc.ToString("O", CultureInfo.InvariantCulture));
            claimCmd.Parameters.AddWithValue("$sale_id", sale.Id);
            try
            {
                claimCmd.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                throw new InvalidOperationException($"Ticket {serial.ToString(CultureInfo.InvariantCulture)} was already recorded for this bundle.", ex);
            }
        }
    }

    private static List<int> ParseSaleTicketSerials(string ticket)
    {
        var parts = (ticket ?? string.Empty).Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2 || !TryParseTicketSerial(parts[0], out var first))
            throw new InvalidOperationException("Sale ticket is not a valid ticket serial or serial range.");

        var last = first;
        if (parts.Length == 2 && !TryParseTicketSerial(parts[1], out last))
            throw new InvalidOperationException("Sale ticket range is not valid.");
        if (last < first)
            throw new InvalidOperationException("Sale ticket range is reversed.");
        if ((long)last - first + 1 > 10000)
            throw new InvalidOperationException("Sale ticket range is unreasonably large.");

        var serials = new List<int>();
        for (var serial = (long)first; serial <= last; serial++)
            serials.Add((int)serial);
        return serials;
    }

    private static bool TryParseTicketSerial(string value, out int serial)
    {
        serial = 0;
        var ticket = (value ?? string.Empty).Trim();
        return ticket.Length > 0 &&
            ticket.All(char.IsAsciiDigit) &&
            int.TryParse(ticket, NumberStyles.None, CultureInfo.InvariantCulture, out serial);
    }

    private static List<StoredGameRecord> QueryManualGames(SqliteConnection conn)
    {
        var rows = new List<StoredGameRecord>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT game_id, name, source, image_uri, image_status, price_cents, bundle_price_cents, first_ticket_serial FROM manual_games ORDER BY game_id";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(new StoredGameRecord(reader.GetString(0), reader.GetString(1), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt32(7), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        return rows;
    }

    private static List<string> QueryVoidedSaleKeys(SqliteConnection conn)
    {
        var rows = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sale_key FROM sale_voids";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(reader.GetString(0));
        return rows;
    }

    private static List<long> QueryVoidedSaleIds(SqliteConnection conn)
    {
        var rows = new List<long>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT original_sale_id FROM sale_voids WHERE original_sale_id IS NOT NULL";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(reader.GetInt64(0));
        return rows;
    }

    private static List<StoredClosingRecord> QueryClosingHistory(SqliteConnection conn)
    {
        var rows = new List<StoredClosingRecord>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT closed_at_utc, interval_start_utc, business_date, shift_sequence, shift_label, report_folder,
                   scanned_bins, active_bins, sales_count, ticket_count,
                   sales_cents, online_sale_cents, online_cashout_cents, instant_cashout_cents, expected_cash_cents,
                   closed_bundles, current_bundles, resolved_bundles, activated_bundles,
                   interval_id, closed_by_actor_id, closed_by_actor_name
            FROM closing_history
            ORDER BY id DESC
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var closedAt = ReadDateTime(reader.GetString(0));
            var intervalStart = string.IsNullOrWhiteSpace(reader.GetString(1))
                ? DateTime.MinValue
                : ReadDateTime(reader.GetString(1));
            rows.Add(new StoredClosingRecord(
                closedAt,
                intervalStart,
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.GetInt64(10),
                reader.GetInt64(11),
                reader.GetInt64(12),
                reader.GetInt64(13),
                reader.GetInt64(14),
                reader.GetInt32(15),
                reader.GetInt32(16),
                reader.GetInt32(17),
                reader.GetInt32(18),
                reader.IsDBNull(19) ? 0 : reader.GetInt64(19),
                reader.GetString(20),
                reader.GetString(21)));
        }

        return rows;
    }

    private static List<StoredClosingReportJob> QueryPendingClosingReports(SqliteConnection conn)
    {
        var rows = new List<StoredClosingReportJob>();
        var invalidRows = new List<(long Id, string Error)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, status, attempt_count, last_error, payload_json
                FROM closing_report_outbox
                WHERE status IN ('pending', 'failed')
                ORDER BY id
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                try
                {
                    var request = JsonSerializer.Deserialize<StoredClosingReportRequest>(reader.GetString(4)) ??
                        throw new InvalidOperationException("Closing report payload is empty.");
                    rows.Add(new StoredClosingReportJob(
                        id,
                        reader.GetString(1),
                        reader.GetInt32(2),
                        reader.GetString(3),
                        request));
                }
                catch (Exception ex)
                {
                    invalidRows.Add((id, ex.Message));
                    AppLog.Error($"Closing report job {id.ToString(CultureInfo.InvariantCulture)} has an invalid payload.", ex);
                }
            }
        }

        foreach (var invalid in invalidRows)
        {
            using var update = conn.CreateCommand();
            update.CommandText = """
                UPDATE closing_report_outbox
                SET status = 'invalid',
                    last_error = $last_error,
                    updated_at_utc = $updated_at_utc
                WHERE id = $id
                """;
            update.Parameters.AddWithValue("$last_error", invalid.Error);
            update.Parameters.AddWithValue("$updated_at_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$id", invalid.Id);
            update.ExecuteNonQuery();
        }

        return rows;
    }

    private static List<StoredAuditRecord> QueryAuditLog(SqliteConnection conn)
    {
        var rows = new List<StoredAuditRecord>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT occurred_at_utc, category, action, actor, detail, actor_id
            FROM audit_log
            ORDER BY occurred_at_utc DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", RecentAuditLogLimit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new StoredAuditRecord(
                ReadDateTime(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }

        return rows;
    }

    private static List<LedgerMigrationConflict> QueryLedgerMigrationConflicts(SqliteConnection conn)
    {
        var rows = new List<LedgerMigrationConflict>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, conflict_type, severity, sale_id, conflicting_sale_id, game_id, bundle_id,
                   ticket_serial, detail, status, detected_at_utc
            FROM ledger_migration_conflicts
            WHERE status = 'unresolved'
            ORDER BY CASE severity WHEN 'blocking' THEN 0 ELSE 1 END, id
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new LedgerMigrationConflict(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.GetString(8),
                reader.GetString(9),
                ReadDateTime(reader.GetString(10))));
        }
        return rows;
    }

    private static List<StoredRdisplayDisplay> QueryRdisplayDisplays(SqliteConnection conn)
    {
        var rows = new List<StoredRdisplayDisplay>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, slug, name, host, port, screen_order, is_active, active_screen_count,
                   created_at_utc, last_seen_at_utc, auth_token, hardware_json, last_registered_at_utc,
                   last_server_url
            FROM rdisplay_displays
            ORDER BY screen_order, id
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new StoredRdisplayDisplay(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6) != 0,
                reader.GetInt32(7),
                ReadDateTime(reader.GetString(8)),
                reader.IsDBNull(9) ? null : ReadNullableDateTime(reader.GetString(9)),
                reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
                reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
                reader.IsDBNull(12) ? null : ReadNullableDateTime(reader.GetString(12)),
                reader.IsDBNull(13) ? string.Empty : reader.GetString(13)));
        }
        return rows;
    }

    private static void EnsureColumn(SqliteConnection conn, string table, string column, string definition)
    {
        using (var check = conn.CreateCommand())
        {
            check.CommandText = $"PRAGMA table_info({table})";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }

        Exec(conn, $"ALTER TABLE {table} ADD COLUMN {column} {definition}");
    }

    private static void UpsertSetting(SqliteConnection conn, SqliteTransaction tx, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO settings (key, value)
            VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    private static void InsertAudit(SqliteConnection conn, SqliteTransaction tx, StoredAuditRecord record)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO audit_log (occurred_at_utc, category, action, actor, detail, actor_id)
            VALUES ($occurred_at_utc, $category, $action, $actor, $detail, $actor_id)
            """;
        cmd.Parameters.AddWithValue("$occurred_at_utc", record.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$category", record.Category);
        cmd.Parameters.AddWithValue("$action", record.Action);
        cmd.Parameters.AddWithValue("$actor", record.Actor);
        cmd.Parameters.AddWithValue("$detail", record.Detail);
        cmd.Parameters.AddWithValue("$actor_id", string.IsNullOrWhiteSpace(record.ActorId) ? SystemActorId : record.ActorId);
        cmd.ExecuteNonQuery();
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object NullableDateTimeValue(DateTime? value) =>
        value is null
            ? DBNull.Value
            : value.Value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ReadDateTime(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTime.UtcNow;

    private static DateTime? ReadNullableDateTime(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private sealed record MigrationClosing(
        long Id,
        DateTime ClosedAtUtc,
        DateTime IntervalStartUtc,
        string BusinessDate,
        int ShiftSequence,
        int SalesCount,
        int TicketCount,
        long SalesCents,
        int ActivatedBundles);

    private sealed record MigrationSale(
        long Id,
        DateTime SoldAtUtc,
        string GameId,
        string BundleId,
        string Bin,
        string Ticket,
        int Quantity,
        long AmountCents,
        string Source);

    private sealed record MigrationActivation(
        long Id,
        DateTime ActivatedAtUtc,
        string GameId,
        string BundleId);
}

public sealed class PersistedState
{
    public Dictionary<string, string> Settings { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<StoredImportLine> Imports { get; } = new();
    public List<StoredReceivedBundle> ReceivedBundles { get; } = new();
    public List<StoredActivationRecord> Activations { get; } = new();
    public List<StoredSaleLine> Sales { get; } = new();
    public List<string> VoidedSaleKeys { get; } = new();
    public List<long> VoidedSaleIds { get; } = new();
    public List<StoredGameRecord> ManualGames { get; } = new();
    public List<StoredRdisplayDisplay> RdisplayDisplays { get; } = new();
    public List<StoredClosingRecord> ClosingHistory { get; } = new();
    public List<StoredClosingReportJob> PendingClosingReports { get; } = new();
    public List<StoredAuditRecord> AuditLog { get; } = new();
    public List<LedgerMigrationConflict> LedgerMigrationConflicts { get; } = new();
    public long OpenIntervalId { get; set; }
}

public sealed record StoreSetup(
    bool SetupComplete,
    bool InitialImportComplete,
    string StoreState,
    string StoreBarcodeLayout,
    string StoreName,
    string StoreStreet,
    string StoreCity,
    int ConfiguredBinCount,
    string ManagerPasswordHash,
    string ClerkName,
    string ClerkPasswordHash,
    string ManagerActorId = "",
    string ClerkActorId = "");

public sealed record StoredImportLine(string GameId, string BundleId, string Ticket, string Bin, string Source, bool IsSoldOut = false);

public sealed record StoredReceivedBundle(string GameId, string BundleId, DateTime ReceivedAtUtc, string Source = "receiving");

public sealed record StoredActivationRecord(
    DateTime ActivatedAtUtc,
    string GameId,
    string BundleId,
    string Bin,
    string Source = "activation",
    long IntervalId = 0,
    string ActorId = "",
    string ActorName = "");

public sealed record StoredSaleLine(
    DateTime SoldAtUtc,
    string GameId,
    string Bin,
    string Ticket,
    int Quantity,
    long AmountCents,
    string Source = "normal_sale",
    string BundleId = "",
    long Id = 0,
    long IntervalId = 0,
    string ActorId = "",
    string ActorName = "",
    long? CorrectsSaleId = null,
    string MigrationState = "native");

public sealed record StoredClosingRecord(
    DateTime ClosedAtUtc,
    DateTime IntervalStartUtc,
    string BusinessDate,
    int ShiftSequence,
    string ShiftLabel,
    string ReportFolder,
    int ScannedBins,
    int ActiveBins,
    int SalesCount,
    int TicketCount,
    long SalesCents,
    long OnlineSaleCents,
    long OnlineCashoutCents,
    long InstantCashoutCents,
    long ExpectedCashCents,
    int ClosedBundles,
    int CurrentBundles,
    int ResolvedBundles,
    int ActivatedBundles,
    long IntervalId = 0,
    string ClosedByActorId = "",
    string ClosedByActorName = "");

public sealed record StoredClosingReportRequest(
    StoredClosingRecord Closing,
    List<StoredSaleLine> Sales,
    List<StoredImportLine> ClosedBundles,
    List<StoredImportLine> CurrentBundles,
    List<StoredImportLine> ResolvedBundles,
    List<string> SelectedEmailAttachments);

public sealed record StoredClosingReverseCorrection(
    string GameId,
    string BundleId,
    string Bin,
    int FirstTicketSerial,
    int LastTicketSerial,
    string StoredCurrentTicket,
    string ScannedTicket);

public sealed record StoredClosingReportJob(
    long Id,
    string Status,
    int AttemptCount,
    string LastError,
    StoredClosingReportRequest Request);

public sealed record StoredAuditRecord(
    DateTime OccurredAtUtc,
    string Category,
    string Action,
    string Actor,
    string Detail,
    string ActorId = "");

public sealed record LedgerMigrationConflict(
    long Id,
    string ConflictType,
    string Severity,
    long? SaleId,
    long? ConflictingSaleId,
    string GameId,
    string BundleId,
    int? TicketSerial,
    string Detail,
    string Status,
    DateTime DetectedAtUtc);

public sealed record CompleteClosingResult(
    long ReportJobId,
    long ClosedIntervalId,
    long OpenIntervalId,
    List<StoredSaleLine> GeneratedSales,
    StoredClosingReportRequest ReportRequest,
    List<StoredAuditRecord> ReverseAuditRecords);

public sealed record StoredGameRecord(
    string GameId,
    string Name,
    long PriceCents,
    long BundlePriceCents,
    int FirstTicketSerial,
    string Source,
    string ImageUri,
    string ImageStatus);

public sealed record StoredRdisplayDisplay(
    long Id,
    string Slug,
    string Name,
    string Host,
    int Port,
    int ScreenOrder,
    bool IsActive,
    int ActiveScreenCount,
    DateTime CreatedAtUtc,
    DateTime? LastSeenAtUtc,
    string? AuthToken,
    string? HardwareJson,
    DateTime? LastRegisteredAtUtc,
    string? LastServerUrl);

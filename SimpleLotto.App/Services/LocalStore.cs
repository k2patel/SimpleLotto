using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace SimpleLotto.App.Services;

public sealed class LocalStore
{
    private const int SchemaVersion = 15;
    private static readonly object SchemaLock = new();
    private static bool _schemaReady;

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
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };

        var conn = new SqliteConnection(builder.ToString());
        conn.Open();
        EnsureSchema(conn);
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
        state.ManualGames.AddRange(QueryManualGames(conn));
        state.RdisplayDisplays.AddRange(QueryRdisplayDisplays(conn));
        state.ClosingHistory.AddRange(QueryClosingHistory(conn));
        state.AuditLog.AddRange(QueryAuditLog(conn));
        return state;
    }

    public void SaveSetup(StoreSetup setup)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
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
        UpsertSetting(conn, tx, "clerk_name", setup.ClerkName);
        UpsertSetting(conn, tx, "clerk_password_hash", setup.ClerkPasswordHash);
        tx.Commit();
    }

    public void SaveSetting(string key, string value)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        UpsertSetting(conn, tx, key, value);
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

    public void InsertSale(StoredSaleLine line)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        ClaimSaleTickets(conn, tx, line);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO sales (sold_at_utc, game_id, bundle_id, bin, ticket, quantity, amount_cents, source)
            VALUES ($sold_at_utc, $game_id, $bundle_id, $bin, $ticket, $quantity, $amount_cents, $source)
            """;
        cmd.Parameters.AddWithValue("$sold_at_utc", line.SoldAtUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$game_id", line.GameId);
        cmd.Parameters.AddWithValue("$bundle_id", line.BundleId);
        cmd.Parameters.AddWithValue("$bin", line.Bin);
        cmd.Parameters.AddWithValue("$ticket", line.Ticket);
        cmd.Parameters.AddWithValue("$quantity", line.Quantity);
        cmd.Parameters.AddWithValue("$amount_cents", line.AmountCents);
        cmd.Parameters.AddWithValue("$source", line.Source);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public void InsertImportAndSale(StoredImportLine import, StoredSaleLine sale)
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

        ClaimSaleTickets(conn, tx, sale);
        using (var saleCmd = conn.CreateCommand())
        {
            saleCmd.Transaction = tx;
            saleCmd.CommandText = """
                INSERT INTO sales (sold_at_utc, game_id, bundle_id, bin, ticket, quantity, amount_cents, source)
                VALUES ($sold_at_utc, $game_id, $bundle_id, $bin, $ticket, $quantity, $amount_cents, $source)
                """;
            saleCmd.Parameters.AddWithValue("$sold_at_utc", sale.SoldAtUtc.ToString("O", CultureInfo.InvariantCulture));
            saleCmd.Parameters.AddWithValue("$game_id", sale.GameId);
            saleCmd.Parameters.AddWithValue("$bundle_id", sale.BundleId);
            saleCmd.Parameters.AddWithValue("$bin", sale.Bin);
            saleCmd.Parameters.AddWithValue("$ticket", sale.Ticket);
            saleCmd.Parameters.AddWithValue("$quantity", sale.Quantity);
            saleCmd.Parameters.AddWithValue("$amount_cents", sale.AmountCents);
            saleCmd.Parameters.AddWithValue("$source", sale.Source);
            saleCmd.ExecuteNonQuery();
        }

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
                INSERT INTO activation_events (activated_at_utc, game_id, bundle_id, bin, source)
                VALUES ($activated_at_utc, $game_id, $bundle_id, $bin, $source)
                """;
            activationCmd.Parameters.AddWithValue("$activated_at_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            activationCmd.Parameters.AddWithValue("$game_id", import.GameId);
            activationCmd.Parameters.AddWithValue("$bundle_id", import.BundleId);
            activationCmd.Parameters.AddWithValue("$bin", import.Bin);
            activationCmd.Parameters.AddWithValue("$source", import.Source);
            activationCmd.ExecuteNonQuery();
        }

        tx.Commit();
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

    public void InsertSaleAndUpdateImportTicket(StoredSaleLine sale, StoredImportLine bundle)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        ClaimSaleTickets(conn, tx, sale);
        using (var saleCmd = conn.CreateCommand())
        {
            saleCmd.Transaction = tx;
            saleCmd.CommandText = """
                INSERT INTO sales (sold_at_utc, game_id, bundle_id, bin, ticket, quantity, amount_cents, source)
                VALUES ($sold_at_utc, $game_id, $bundle_id, $bin, $ticket, $quantity, $amount_cents, $source)
                """;
            saleCmd.Parameters.AddWithValue("$sold_at_utc", sale.SoldAtUtc.ToString("O", CultureInfo.InvariantCulture));
            saleCmd.Parameters.AddWithValue("$game_id", sale.GameId);
            saleCmd.Parameters.AddWithValue("$bundle_id", sale.BundleId);
            saleCmd.Parameters.AddWithValue("$bin", sale.Bin);
            saleCmd.Parameters.AddWithValue("$ticket", sale.Ticket);
            saleCmd.Parameters.AddWithValue("$quantity", sale.Quantity);
            saleCmd.Parameters.AddWithValue("$amount_cents", sale.AmountCents);
            saleCmd.Parameters.AddWithValue("$source", sale.Source);
            saleCmd.ExecuteNonQuery();
        }

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

    public void CompleteClosing(
        DateTime closedAtUtc,
        StoredClosingRecord closingRecord,
        StoredAuditRecord auditRecord,
        IEnumerable<StoredSaleLine> generatedSales,
        IEnumerable<StoredImportLine> closedBundles,
        IEnumerable<StoredImportLine> currentBundles,
        IEnumerable<StoredImportLine> resolvedBundles)
    {
        var generated = new List<StoredSaleLine>(generatedSales);
        var closed = new List<StoredImportLine>(closedBundles);
        var current = new List<StoredImportLine>(currentBundles);
        var resolved = new List<StoredImportLine>(resolvedBundles);
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var closingCmd = conn.CreateCommand())
        {
            closingCmd.Transaction = tx;
            closingCmd.CommandText = """
                INSERT INTO closing_history (
                    closed_at_utc, interval_start_utc, business_date, shift_sequence, shift_label, report_folder, scanned_bins, active_bins, sales_count, ticket_count,
                    sales_cents, online_sale_cents, online_cashout_cents, instant_cashout_cents, expected_cash_cents,
                    closed_bundles, current_bundles, resolved_bundles, activated_bundles)
                VALUES (
                    $closed_at_utc, $interval_start_utc, $business_date, $shift_sequence, $shift_label, $report_folder, $scanned_bins, $active_bins, $sales_count, $ticket_count,
                    $sales_cents, $online_sale_cents, $online_cashout_cents, $instant_cashout_cents, $expected_cash_cents,
                    $closed_bundles, $current_bundles, $resolved_bundles, $activated_bundles)
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
            closingCmd.ExecuteNonQuery();
        }

        InsertAudit(conn, tx, auditRecord);

        foreach (var line in generated)
        {
            ClaimSaleTickets(conn, tx, line);
            using var saleCmd = conn.CreateCommand();
            saleCmd.Transaction = tx;
            saleCmd.CommandText = """
                INSERT INTO sales (sold_at_utc, game_id, bundle_id, bin, ticket, quantity, amount_cents, source)
                VALUES ($sold_at_utc, $game_id, $bundle_id, $bin, $ticket, $quantity, $amount_cents, $source)
                """;
            saleCmd.Parameters.AddWithValue("$sold_at_utc", line.SoldAtUtc.ToString("O", CultureInfo.InvariantCulture));
            saleCmd.Parameters.AddWithValue("$game_id", line.GameId);
            saleCmd.Parameters.AddWithValue("$bundle_id", line.BundleId);
            saleCmd.Parameters.AddWithValue("$bin", line.Bin);
            saleCmd.Parameters.AddWithValue("$ticket", line.Ticket);
            saleCmd.Parameters.AddWithValue("$quantity", line.Quantity);
            saleCmd.Parameters.AddWithValue("$amount_cents", line.AmountCents);
            saleCmd.Parameters.AddWithValue("$source", line.Source);
            saleCmd.ExecuteNonQuery();
        }

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
        tx.Commit();
    }

    public void InsertAudit(StoredAuditRecord record)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        InsertAudit(conn, tx, record);
        tx.Commit();
    }

    public void InsertVoid(StoredSaleLine original, StoredSaleLine correction, string saleKey)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var voidCmd = conn.CreateCommand())
        {
            voidCmd.Transaction = tx;
            voidCmd.CommandText = """
                INSERT OR IGNORE INTO sale_voids (sale_key, voided_at_utc)
                VALUES ($sale_key, $voided_at_utc)
                """;
            voidCmd.Parameters.AddWithValue("$sale_key", saleKey);
            voidCmd.Parameters.AddWithValue("$voided_at_utc", correction.SoldAtUtc.ToString("O", CultureInfo.InvariantCulture));
            if (voidCmd.ExecuteNonQuery() == 0)
                throw new InvalidOperationException("This sale has already been voided.");
        }

        using (var saleCmd = conn.CreateCommand())
        {
            saleCmd.Transaction = tx;
            saleCmd.CommandText = """
                INSERT INTO sales (sold_at_utc, game_id, bundle_id, bin, ticket, quantity, amount_cents, source)
                VALUES ($sold_at_utc, $game_id, $bundle_id, $bin, $ticket, $quantity, $amount_cents, $source)
                """;
            saleCmd.Parameters.AddWithValue("$sold_at_utc", correction.SoldAtUtc.ToString("O", CultureInfo.InvariantCulture));
            saleCmd.Parameters.AddWithValue("$game_id", correction.GameId);
            saleCmd.Parameters.AddWithValue("$bundle_id", correction.BundleId);
            saleCmd.Parameters.AddWithValue("$bin", correction.Bin);
            saleCmd.Parameters.AddWithValue("$ticket", correction.Ticket);
            saleCmd.Parameters.AddWithValue("$quantity", correction.Quantity);
            saleCmd.Parameters.AddWithValue("$amount_cents", correction.AmountCents);
            saleCmd.Parameters.AddWithValue("$source", correction.Source);
            saleCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void DeleteSale(StoredSaleLine line)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM sales
            WHERE rowid = (
                SELECT rowid
                FROM sales
                WHERE sold_at_utc = $sold_at_utc
                  AND game_id = $game_id
                  AND bin = $bin
                  AND ticket = $ticket
                ORDER BY rowid DESC
                LIMIT 1)
            """;
        cmd.Parameters.AddWithValue("$sold_at_utc", line.SoldAtUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$game_id", line.GameId);
        cmd.Parameters.AddWithValue("$bin", line.Bin);
        cmd.Parameters.AddWithValue("$ticket", line.Ticket);
        cmd.ExecuteNonQuery();
    }

    public void ClearSales()
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM sales";
            cmd.ExecuteNonQuery();
        }
        using (var voidCmd = conn.CreateCommand())
        {
            voidCmd.Transaction = tx;
            voidCmd.CommandText = "DELETE FROM sale_voids";
            voidCmd.ExecuteNonQuery();
        }
        tx.Commit();
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

    private static void EnsureSchema(SqliteConnection conn)
    {
        lock (SchemaLock)
        {
            if (_schemaReady)
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
                CREATE TABLE IF NOT EXISTS activation_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    activated_at_utc TEXT NOT NULL,
                    game_id TEXT NOT NULL,
                    bundle_id TEXT NOT NULL,
                    bin TEXT NOT NULL,
                    source TEXT NOT NULL DEFAULT 'activation'
                )
                """);
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_activation_events_activated_at ON activation_events(activated_at_utc)");
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
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_sales_game ON sales(game_id)");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_sales_bin ON sales(bin)");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_sales_sold_at ON sales(sold_at_utc)");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_sales_bundle ON sales(game_id, bundle_id)");
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS sale_voids (
                    sale_key TEXT PRIMARY KEY,
                    voided_at_utc TEXT NOT NULL
                )
                """);
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS sale_ticket_claims (
                    game_id TEXT NOT NULL,
                    bundle_id TEXT NOT NULL,
                    ticket_serial INTEGER NOT NULL,
                    claimed_at_utc TEXT NOT NULL,
                    PRIMARY KEY (game_id, bundle_id, ticket_serial)
                )
                """);
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
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_closing_history_closed_at ON closing_history(closed_at_utc)");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_closing_history_business_date ON closing_history(business_date)");
            Exec(conn, "CREATE UNIQUE INDEX IF NOT EXISTS idx_closing_history_business_shift ON closing_history(business_date, shift_sequence) WHERE business_date <> '' AND shift_sequence > 0");
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
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_audit_log_occurred_at ON audit_log(occurred_at_utc)");
            RepairDuplicateImports(conn);
            Exec(conn, "CREATE UNIQUE INDEX IF NOT EXISTS idx_imports_physical_bundle_unique ON imports(trim(game_id) COLLATE NOCASE, trim(bundle_id) COLLATE NOCASE)");
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS manual_games (
                    game_id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    source TEXT NOT NULL,
                    price_cents INTEGER NOT NULL DEFAULT 0,
                    bundle_price_cents INTEGER NOT NULL DEFAULT 30000,
                    first_ticket_serial INTEGER NOT NULL DEFAULT 0,
                    image_uri TEXT NOT NULL,
                    image_status TEXT NOT NULL
                )
                """);
            EnsureColumn(conn, "manual_games", "price_cents", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, "manual_games", "bundle_price_cents", "INTEGER NOT NULL DEFAULT 30000");
            EnsureColumn(conn, "manual_games", "first_ticket_serial", "INTEGER NOT NULL DEFAULT 0");
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
            RecordSchemaMigration(conn, SchemaVersion, previousSchemaVersion, "Enforce one accounting claim per bundle ticket and persist one-time voids");
            Exec(conn, $"INSERT OR REPLACE INTO settings (key, value) VALUES ('schema_version', '{SchemaVersion.ToString(CultureInfo.InvariantCulture)}')");
            _schemaReady = true;
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
            INSERT INTO audit_log (occurred_at_utc, category, action, actor, detail)
            VALUES ($occurred_at_utc, 'inventory', 'Duplicate active bundles repaired', 'System', $detail)
            """;
        auditCmd.Parameters.AddWithValue("$occurred_at_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
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
        cmd.CommandText = "SELECT activated_at_utc, game_id, bundle_id, bin, source FROM activation_events ORDER BY id DESC";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new StoredActivationRecord(
                ReadDateTime(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return rows;
    }

    private static List<StoredSaleLine> QuerySales(SqliteConnection conn)
    {
        var rows = new List<StoredSaleLine>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sold_at_utc, game_id, bundle_id, bin, ticket, quantity, amount_cents, source FROM sales ORDER BY id DESC";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var soldAt = DateTime.TryParse(
                reader.GetString(0),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed)
                ? parsed
                : DateTime.UtcNow;
            rows.Add(new StoredSaleLine(
                soldAt,
                reader.GetString(1),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt64(6),
                reader.GetString(7),
                reader.GetString(2)));
        }
        return rows;
    }

    private static void ClaimSaleTickets(SqliteConnection conn, SqliteTransaction tx, StoredSaleLine sale)
    {
        if (sale.Quantity <= 0)
            return;

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
                INSERT INTO sale_ticket_claims (game_id, bundle_id, ticket_serial, claimed_at_utc)
                VALUES ($game_id, $bundle_id, $ticket_serial, $claimed_at_utc)
                """;
            claimCmd.Parameters.AddWithValue("$game_id", sale.GameId);
            claimCmd.Parameters.AddWithValue("$bundle_id", sale.BundleId);
            claimCmd.Parameters.AddWithValue("$ticket_serial", serial);
            claimCmd.Parameters.AddWithValue("$claimed_at_utc", sale.SoldAtUtc.ToString("O", CultureInfo.InvariantCulture));
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

        var serials = new List<int>();
        for (var serial = first; serial <= last; serial++)
            serials.Add(serial);
        return serials;
    }

    private static bool TryParseTicketSerial(string value, out int serial)
    {
        var digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out serial);
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

    private static List<StoredClosingRecord> QueryClosingHistory(SqliteConnection conn)
    {
        var rows = new List<StoredClosingRecord>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT closed_at_utc, interval_start_utc, business_date, shift_sequence, shift_label, report_folder,
                   scanned_bins, active_bins, sales_count, ticket_count,
                   sales_cents, online_sale_cents, online_cashout_cents, instant_cashout_cents, expected_cash_cents,
                   closed_bundles, current_bundles, resolved_bundles, activated_bundles
            FROM closing_history
            ORDER BY closed_at_utc DESC
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
                reader.GetInt32(18)));
        }

        return rows;
    }

    private static List<StoredAuditRecord> QueryAuditLog(SqliteConnection conn)
    {
        var rows = new List<StoredAuditRecord>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT occurred_at_utc, category, action, actor, detail
            FROM audit_log
            ORDER BY occurred_at_utc DESC
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new StoredAuditRecord(
                ReadDateTime(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
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
            INSERT INTO audit_log (occurred_at_utc, category, action, actor, detail)
            VALUES ($occurred_at_utc, $category, $action, $actor, $detail)
            """;
        cmd.Parameters.AddWithValue("$occurred_at_utc", record.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$category", record.Category);
        cmd.Parameters.AddWithValue("$action", record.Action);
        cmd.Parameters.AddWithValue("$actor", record.Actor);
        cmd.Parameters.AddWithValue("$detail", record.Detail);
        cmd.ExecuteNonQuery();
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
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
}

public sealed class PersistedState
{
    public Dictionary<string, string> Settings { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<StoredImportLine> Imports { get; } = new();
    public List<StoredReceivedBundle> ReceivedBundles { get; } = new();
    public List<StoredActivationRecord> Activations { get; } = new();
    public List<StoredSaleLine> Sales { get; } = new();
    public List<string> VoidedSaleKeys { get; } = new();
    public List<StoredGameRecord> ManualGames { get; } = new();
    public List<StoredRdisplayDisplay> RdisplayDisplays { get; } = new();
    public List<StoredClosingRecord> ClosingHistory { get; } = new();
    public List<StoredAuditRecord> AuditLog { get; } = new();
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
    string ClerkPasswordHash);

public sealed record StoredImportLine(string GameId, string BundleId, string Ticket, string Bin, string Source, bool IsSoldOut = false);

public sealed record StoredReceivedBundle(string GameId, string BundleId, DateTime ReceivedAtUtc, string Source = "receiving");

public sealed record StoredActivationRecord(DateTime ActivatedAtUtc, string GameId, string BundleId, string Bin, string Source = "activation");

public sealed record StoredSaleLine(
    DateTime SoldAtUtc,
    string GameId,
    string Bin,
    string Ticket,
    int Quantity,
    long AmountCents,
    string Source = "normal_sale",
    string BundleId = "");

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
    int ActivatedBundles);

public sealed record StoredAuditRecord(
    DateTime OccurredAtUtc,
    string Category,
    string Action,
    string Actor,
    string Detail);

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

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace SimpleLotto.App.Services;

public sealed class LocalStore
{
    private const int SchemaVersion = 1;
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
        state.Sales.AddRange(QuerySales(conn));
        state.ManualGames.AddRange(QueryManualGames(conn));
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

    public void InsertImport(StoredImportLine line)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO imports (game_id, bundle_id, ticket, bin, created_at_utc)
            VALUES ($game_id, $bundle_id, $ticket, $bin, $created_at_utc)
            """;
        cmd.Parameters.AddWithValue("$game_id", line.GameId);
        cmd.Parameters.AddWithValue("$bundle_id", line.BundleId);
        cmd.Parameters.AddWithValue("$ticket", line.Ticket);
        cmd.Parameters.AddWithValue("$bin", line.Bin);
        cmd.Parameters.AddWithValue("$created_at_utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    public void InsertSale(StoredSaleLine line)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sales (sold_at_utc, game_id, bin, ticket, quantity, amount_cents)
            VALUES ($sold_at_utc, $game_id, $bin, $ticket, $quantity, $amount_cents)
            """;
        cmd.Parameters.AddWithValue("$sold_at_utc", line.SoldAtUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$game_id", line.GameId);
        cmd.Parameters.AddWithValue("$bin", line.Bin);
        cmd.Parameters.AddWithValue("$ticket", line.Ticket);
        cmd.Parameters.AddWithValue("$quantity", line.Quantity);
        cmd.Parameters.AddWithValue("$amount_cents", line.AmountCents);
        cmd.ExecuteNonQuery();
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
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sales";
        cmd.ExecuteNonQuery();
    }

    public void UpsertManualGame(StoredGameRecord game)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO manual_games (game_id, name, price_cents, source, image_uri, image_status)
            VALUES ($game_id, $name, $price_cents, $source, $image_uri, $image_status)
            ON CONFLICT(game_id) DO UPDATE SET
                name = excluded.name,
                price_cents = excluded.price_cents,
                source = excluded.source,
                image_uri = excluded.image_uri,
                image_status = excluded.image_status
            """;
        cmd.Parameters.AddWithValue("$game_id", game.GameId);
        cmd.Parameters.AddWithValue("$name", game.Name);
        cmd.Parameters.AddWithValue("$price_cents", game.PriceCents);
        cmd.Parameters.AddWithValue("$source", game.Source);
        cmd.Parameters.AddWithValue("$image_uri", game.ImageUri);
        cmd.Parameters.AddWithValue("$image_status", game.ImageStatus);
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
                CREATE TABLE IF NOT EXISTS imports (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    game_id TEXT NOT NULL,
                    bundle_id TEXT NOT NULL,
                    ticket TEXT NOT NULL,
                    bin TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL
                )
                """);
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_imports_bundle ON imports(game_id, bundle_id)");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_imports_bin ON imports(bin)");
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS sales (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    sold_at_utc TEXT NOT NULL,
                    game_id TEXT NOT NULL,
                    bin TEXT NOT NULL,
                    ticket TEXT NOT NULL,
                    quantity INTEGER NOT NULL,
                    amount_cents INTEGER NOT NULL
                )
                """);
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_sales_game ON sales(game_id)");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_sales_bin ON sales(bin)");
            Exec(conn, "CREATE INDEX IF NOT EXISTS idx_sales_sold_at ON sales(sold_at_utc)");
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS manual_games (
                    game_id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    source TEXT NOT NULL,
                    price_cents INTEGER NOT NULL DEFAULT 0,
                    image_uri TEXT NOT NULL,
                    image_status TEXT NOT NULL
                )
                """);
            EnsureColumn(conn, "manual_games", "price_cents", "INTEGER NOT NULL DEFAULT 0");
            Exec(conn, "INSERT OR IGNORE INTO settings (key, value) VALUES ('schema_version', '1')");
            _schemaReady = true;
        }
    }

    private static List<StoredImportLine> QueryImports(SqliteConnection conn)
    {
        var rows = new List<StoredImportLine>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT game_id, bundle_id, ticket, bin FROM imports ORDER BY id DESC";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(new StoredImportLine(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return rows;
    }

    private static List<StoredSaleLine> QuerySales(SqliteConnection conn)
    {
        var rows = new List<StoredSaleLine>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sold_at_utc, game_id, bin, ticket, quantity, amount_cents FROM sales ORDER BY id DESC";
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
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt64(5)));
        }
        return rows;
    }

    private static List<StoredGameRecord> QueryManualGames(SqliteConnection conn)
    {
        var rows = new List<StoredGameRecord>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT game_id, name, source, image_uri, image_status, price_cents FROM manual_games ORDER BY game_id";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(new StoredGameRecord(reader.GetString(0), reader.GetString(1), reader.GetInt64(5), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
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

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

public sealed class PersistedState
{
    public Dictionary<string, string> Settings { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<StoredImportLine> Imports { get; } = new();
    public List<StoredSaleLine> Sales { get; } = new();
    public List<StoredGameRecord> ManualGames { get; } = new();
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

public sealed record StoredImportLine(string GameId, string BundleId, string Ticket, string Bin);

public sealed record StoredSaleLine(
    DateTime SoldAtUtc,
    string GameId,
    string Bin,
    string Ticket,
    int Quantity,
    long AmountCents);

public sealed record StoredGameRecord(
    string GameId,
    string Name,
    long PriceCents,
    string Source,
    string ImageUri,
    string ImageStatus);

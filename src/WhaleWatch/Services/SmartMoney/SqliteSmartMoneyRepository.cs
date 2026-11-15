using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using WhaleWatch.Abstractions.SmartMoney;
using WhaleWatch.Models.SmartMoney;
using Microsoft.Data.Sqlite;

namespace WhaleWatch.Services.SmartMoney;

public sealed class SqliteSmartMoneyRepository(string connectionString = "Data Source=smart_money.db")
    : ISmartMoneyRepository, IDisposable
{
    private const string LeaderboardRefreshKey = "last_leaderboard_refresh_utc";

    private readonly string _connectionString = NormalizeConnectionString(connectionString);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _initialized;

    private static string NormalizeConnectionString(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "Data Source=smart_money.db";

        var trimmed = input.Trim();
        if (trimmed.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("DataSource=", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Filename=", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return $"Data Source={trimmed}";
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS wallets (
                    address TEXT PRIMARY KEY,
                    account_value_usd TEXT NOT NULL,
                    month_pnl_usd TEXT NOT NULL,
                    month_roi TEXT NOT NULL,
                    all_time_pnl_usd TEXT NOT NULL,
                    all_time_volume_usd TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS position_events (
                    event_key TEXT PRIMARY KEY,
                    wallet_address TEXT NOT NULL,
                    symbol TEXT NOT NULL,
                    coin TEXT NOT NULL,
                    direction TEXT NOT NULL,
                    action TEXT NOT NULL DEFAULT 'Open',
                    size TEXT NOT NULL,
                    price TEXT NOT NULL,
                    closed_pnl_usd TEXT NOT NULL DEFAULT '0',
                    opened_at_utc TEXT NOT NULL,
                    source_kind TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS wallet_position_snapshots (
                    wallet_address TEXT NOT NULL,
                    coin TEXT NOT NULL,
                    direction TEXT NOT NULL,
                    size TEXT NOT NULL,
                    entry_price TEXT NOT NULL,
                    position_value_usd TEXT NOT NULL,
                    unrealized_pnl_usd TEXT NOT NULL,
                    leverage TEXT NOT NULL,
                    PRIMARY KEY (wallet_address, coin, direction)
                );

                CREATE TABLE IF NOT EXISTS wallet_protective_orders (
                    wallet_address TEXT NOT NULL,
                    order_id INTEGER NOT NULL,
                    symbol TEXT NOT NULL,
                    coin TEXT NOT NULL,
                    order_kind TEXT NOT NULL,
                    side TEXT NOT NULL,
                    price TEXT NOT NULL,
                    size TEXT NOT NULL,
                    is_trigger INTEGER NOT NULL,
                    trigger_condition TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    PRIMARY KEY (wallet_address, order_id)
                );

                CREATE TABLE IF NOT EXISTS protective_order_history (
                    history_key TEXT PRIMARY KEY,
                    wallet_address TEXT NOT NULL,
                    order_id INTEGER NOT NULL,
                    symbol TEXT NOT NULL,
                    order_kind TEXT NOT NULL,
                    side TEXT NOT NULL,
                    price TEXT NOT NULL,
                    size TEXT NOT NULL,
                    trigger_condition TEXT NOT NULL,
                    recorded_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS shadow_outcomes (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    symbol TEXT NOT NULL,
                    engine_direction TEXT NOT NULL,
                    smart_money_stance TEXT NOT NULL,
                    decision_at_utc TEXT NOT NULL,
                    outcome_net_r TEXT NULL
                );

                CREATE TABLE IF NOT EXISTS smart_money_meta (
                    meta_key TEXT PRIMARY KEY,
                    meta_value TEXT NOT NULL
                );";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            await EnsurePositionEventColumnsAsync(connection, cancellationToken);
            _initialized = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpsertWalletsAsync(
        IReadOnlyList<TrackedWallet> wallets,
        DateTimeOffset refreshedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            using var tx = connection.BeginTransaction();

            using (var clearCmd = connection.CreateCommand())
            {
                clearCmd.Transaction = tx;
                clearCmd.CommandText = "DELETE FROM wallets;";
                await clearCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var w in wallets)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO wallets (
                        address, account_value_usd, month_pnl_usd, month_roi, all_time_pnl_usd, all_time_volume_usd, updated_at_utc
                    ) VALUES (
                        $addr, $val, $mpnl, $mroi, $apnl, $avol, $upd
                    );";
                cmd.Parameters.AddWithValue("$addr", w.Address);
                cmd.Parameters.AddWithValue("$val", w.AccountValueUsd.ToString(CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$mpnl", w.MonthPnlUsd.ToString(CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$mroi", w.MonthRoi.ToString(CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$apnl", w.AllTimePnlUsd.ToString(CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$avol", w.AllTimeVolumeUsd.ToString(CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$upd", w.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            using (var metaCmd = connection.CreateCommand())
            {
                metaCmd.Transaction = tx;
                metaCmd.CommandText = @"
                    INSERT INTO smart_money_meta (meta_key, meta_value)
                    VALUES ($k, $v)
                    ON CONFLICT(meta_key) DO UPDATE SET meta_value = excluded.meta_value;";
                metaCmd.Parameters.AddWithValue("$k", LeaderboardRefreshKey);
                metaCmd.Parameters.AddWithValue("$v", refreshedAtUtc.ToString("O", CultureInfo.InvariantCulture));
                await metaCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            tx.Commit();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<TrackedWallet>> GetTrackedWalletsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT address, account_value_usd, month_pnl_usd, month_roi, all_time_pnl_usd, all_time_volume_usd, updated_at_utc FROM wallets;";
            var list = new List<TrackedWallet>();
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                list.Add(new TrackedWallet(
                    reader.GetString(0),
                    ParseDecimal(reader.GetString(1)),
                    ParseDecimal(reader.GetString(2)),
                    ParseDecimal(reader.GetString(3)),
                    ParseDecimal(reader.GetString(4)),
                    ParseDecimal(reader.GetString(5)),
                    DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture)));
            }

            return list;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<DateTimeOffset?> GetLastLeaderboardRefreshUtcAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT meta_value FROM smart_money_meta WHERE meta_key = $k;";
            cmd.Parameters.AddWithValue("$k", LeaderboardRefreshKey);
            object? raw = await cmd.ExecuteScalarAsync(cancellationToken);
            if (raw is string text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                return dt;

            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<int> RecordPositionEventsAsync(
        IReadOnlyList<SmartMoneyPositionEvent> events,
        CancellationToken cancellationToken = default)
    {
        var insertedList = await RecordAndReturnNewPositionEventsAsync(events, cancellationToken);
        return insertedList.Count;
    }

    public async Task<IReadOnlyList<SmartMoneyPositionEvent>> RecordAndReturnNewPositionEventsAsync(
        IReadOnlyList<SmartMoneyPositionEvent> events,
        CancellationToken cancellationToken = default)
    {
        if (events.Count == 0) return Array.Empty<SmartMoneyPositionEvent>();

        await InitializeAsync(cancellationToken);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            using var tx = connection.BeginTransaction();
            var newlyInserted = new List<SmartMoneyPositionEvent>();

            foreach (var ev in events)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT OR IGNORE INTO position_events (
                        event_key, wallet_address, symbol, coin, direction, action, size, price, closed_pnl_usd, opened_at_utc, source_kind
                    ) VALUES (
                        $key, $addr, $sym, $coin, $dir, $act, $sz, $px, $pnl, $at, $src
                    );";
                cmd.Parameters.AddWithValue("$key", ev.EventKey);
                cmd.Parameters.AddWithValue("$addr", ev.WalletAddress);
                cmd.Parameters.AddWithValue("$sym", ev.Symbol);
                cmd.Parameters.AddWithValue("$coin", ev.Coin);
                cmd.Parameters.AddWithValue("$dir", ev.Direction);
                cmd.Parameters.AddWithValue("$act", ev.Action);
                cmd.Parameters.AddWithValue("$sz", ev.Size.ToString(CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$px", ev.Price.ToString(CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$pnl", ev.ClosedPnlUsd.ToString(CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$at", ev.OpenedAtUtc.ToString("O", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$src", ev.SourceKind);
                int rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
                if (rows > 0)
                {
                    newlyInserted.Add(ev);
                }
            }

            tx.Commit();
            return newlyInserted;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<SmartMoneyPositionEvent>> GetRecentEventsAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT event_key, wallet_address, symbol, coin, direction, action, size, price, closed_pnl_usd, opened_at_utc, source_kind
                FROM position_events
                ORDER BY opened_at_utc DESC
                LIMIT $lim;";
            cmd.Parameters.AddWithValue("$lim", Math.Max(1, limit));

            var list = new List<SmartMoneyPositionEvent>();
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                list.Add(new SmartMoneyPositionEvent(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    ParseDecimal(reader.GetString(6)),
                    ParseDecimal(reader.GetString(7)),
                    ParseDecimal(reader.GetString(8)),
                    DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture),
                    reader.GetString(10)));
            }

            return list;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<WalletPositionSnapshot>> GetSnapshotPositionsAsync(
        string walletAddress,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT wallet_address, coin, direction, size, entry_price, position_value_usd, unrealized_pnl_usd, leverage
                FROM wallet_position_snapshots
                WHERE wallet_address = $addr;";
            cmd.Parameters.AddWithValue("$addr", walletAddress);

            var list = new List<WalletPositionSnapshot>();
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                list.Add(new WalletPositionSnapshot(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    ParseDecimal(reader.GetString(3)),
                    ParseDecimal(reader.GetString(4)),
                    ParseDecimal(reader.GetString(5)),
                    ParseDecimal(reader.GetString(6)),
                    ParseDecimal(reader.GetString(7))));
            }

            return list;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<WalletPositionSnapshot>> GetAllSnapshotPositionsAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT wallet_address, coin, direction, size, entry_price, position_value_usd, unrealized_pnl_usd, leverage
                FROM wallet_position_snapshots
                ORDER BY CAST(position_value_usd AS REAL) DESC;";

            var list = new List<WalletPositionSnapshot>();
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                list.Add(new WalletPositionSnapshot(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    ParseDecimal(reader.GetString(3)),
                    ParseDecimal(reader.GetString(4)),
                    ParseDecimal(reader.GetString(5)),
                    ParseDecimal(reader.GetString(6)),
                    ParseDecimal(reader.GetString(7))));
            }

            return list;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveSnapshotPositionsAsync(
        string walletAddress,
        IReadOnlyList<WalletPositionSnapshot> positions,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            using var tx = connection.BeginTransaction();

            using (var del = connection.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM wallet_position_snapshots WHERE wallet_address = $addr;";
                del.Parameters.AddWithValue("$addr", walletAddress);
                await del.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var p in positions)
            {
                using var ins = connection.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = @"
                    INSERT INTO wallet_position_snapshots (
                        wallet_address, coin, direction, size, entry_price, position_value_usd, unrealized_pnl_usd, leverage
                    ) VALUES (
                        $addr, $coin, $dir, $sz, $epx, $pval, $upnl, $lev
                    );";
                ins.Parameters.AddWithValue("$addr", walletAddress);
                ins.Parameters.AddWithValue("$coin", p.Coin);
                ins.Parameters.AddWithValue("$dir", p.Direction);
                ins.Parameters.AddWithValue("$sz", p.Size.ToString(CultureInfo.InvariantCulture));
                ins.Parameters.AddWithValue("$epx", p.EntryPrice.ToString(CultureInfo.InvariantCulture));
                ins.Parameters.AddWithValue("$pval", p.PositionValueUsd.ToString(CultureInfo.InvariantCulture));
                ins.Parameters.AddWithValue("$upnl", p.UnrealizedPnlUsd.ToString(CultureInfo.InvariantCulture));
                ins.Parameters.AddWithValue("$lev", p.Leverage.ToString(CultureInfo.InvariantCulture));
                await ins.ExecuteNonQueryAsync(cancellationToken);
            }

            tx.Commit();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveProtectiveOrdersAsync(
        string walletAddress,
        IReadOnlyList<WalletProtectiveOrder> orders,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            using var tx = connection.BeginTransaction();

            using (var del = connection.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM wallet_protective_orders WHERE wallet_address = $addr;";
                del.Parameters.AddWithValue("$addr", walletAddress);
                await del.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var o in orders)
            {
                string px = o.Price.ToString(CultureInfo.InvariantCulture);
                string sz = o.Size.ToString(CultureInfo.InvariantCulture);
                string ts = o.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture);

                using (var ins = connection.CreateCommand())
                {
                    ins.Transaction = tx;
                    ins.CommandText = @"
                        INSERT INTO wallet_protective_orders (
                            wallet_address, order_id, symbol, coin, order_kind, side, price, size, is_trigger, trigger_condition, updated_at_utc
                        ) VALUES (
                            $addr, $oid, $sym, $coin, $kind, $side, $px, $sz, $trig, $cond, $upd
                        );";
                    ins.Parameters.AddWithValue("$addr", o.WalletAddress);
                    ins.Parameters.AddWithValue("$oid", o.OrderId);
                    ins.Parameters.AddWithValue("$sym", o.Symbol);
                    ins.Parameters.AddWithValue("$coin", o.Coin);
                    ins.Parameters.AddWithValue("$kind", o.OrderKind);
                    ins.Parameters.AddWithValue("$side", o.Side);
                    ins.Parameters.AddWithValue("$px", px);
                    ins.Parameters.AddWithValue("$sz", sz);
                    ins.Parameters.AddWithValue("$trig", o.IsTrigger ? 1 : 0);
                    ins.Parameters.AddWithValue("$cond", o.TriggerCondition);
                    ins.Parameters.AddWithValue("$upd", ts);
                    await ins.ExecuteNonQueryAsync(cancellationToken);
                }

                // Append-only history deduplicated by wallet + order_id + price + size.
                using (var hist = connection.CreateCommand())
                {
                    hist.Transaction = tx;
                    hist.CommandText = @"
                        INSERT OR IGNORE INTO protective_order_history (
                            history_key, wallet_address, order_id, symbol, order_kind, side, price, size, trigger_condition, recorded_at_utc
                        ) VALUES (
                            $hkey, $addr, $oid, $sym, $kind, $side, $px, $sz, $cond, $upd
                        );";
                    hist.Parameters.AddWithValue("$hkey", $"{o.WalletAddress}:{o.OrderId}:{px}:{sz}");
                    hist.Parameters.AddWithValue("$addr", o.WalletAddress);
                    hist.Parameters.AddWithValue("$oid", o.OrderId);
                    hist.Parameters.AddWithValue("$sym", o.Symbol);
                    hist.Parameters.AddWithValue("$kind", o.OrderKind);
                    hist.Parameters.AddWithValue("$side", o.Side);
                    hist.Parameters.AddWithValue("$px", px);
                    hist.Parameters.AddWithValue("$sz", sz);
                    hist.Parameters.AddWithValue("$cond", o.TriggerCondition);
                    hist.Parameters.AddWithValue("$upd", ts);
                    await hist.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            tx.Commit();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<WalletProtectiveOrder>> GetProtectiveOrdersAsync(
        string? symbol = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = string.IsNullOrWhiteSpace(symbol)
                ? "SELECT wallet_address, order_id, symbol, coin, order_kind, side, price, size, is_trigger, trigger_condition, updated_at_utc FROM wallet_protective_orders;"
                : "SELECT wallet_address, order_id, symbol, coin, order_kind, side, price, size, is_trigger, trigger_condition, updated_at_utc FROM wallet_protective_orders WHERE symbol = $sym;";
            if (!string.IsNullOrWhiteSpace(symbol))
                cmd.Parameters.AddWithValue("$sym", symbol);

            var list = new List<WalletProtectiveOrder>();
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                list.Add(new WalletProtectiveOrder(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    ParseDecimal(reader.GetString(6)),
                    ParseDecimal(reader.GetString(7)),
                    reader.GetInt64(8) != 0,
                    reader.GetString(9),
                    DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture)));
            }

            return list;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<long> RecordShadowOutcomeAsync(
        ShadowOutcomeRecord record,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO shadow_outcomes (symbol, engine_direction, smart_money_stance, decision_at_utc, outcome_net_r)
                VALUES ($sym, $dir, $stance, $at, $r);
                SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$sym", record.Symbol);
            cmd.Parameters.AddWithValue("$dir", record.EngineDirection);
            cmd.Parameters.AddWithValue("$stance", record.SmartMoneyStance);
            cmd.Parameters.AddWithValue("$at", record.DecisionAtUtc.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$r", record.OutcomeNetR.HasValue
                ? record.OutcomeNetR.Value.ToString(CultureInfo.InvariantCulture)
                : DBNull.Value);

            object? scalar = await cmd.ExecuteScalarAsync(cancellationToken);
            return scalar is long id ? id : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<ShadowOutcomeRecord>> GetShadowOutcomesAsync(
        string? symbol = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = string.IsNullOrWhiteSpace(symbol)
                ? @"SELECT id, symbol, engine_direction, smart_money_stance, decision_at_utc, outcome_net_r
                    FROM shadow_outcomes
                    ORDER BY decision_at_utc DESC
                    LIMIT $lim;"
                : @"SELECT id, symbol, engine_direction, smart_money_stance, decision_at_utc, outcome_net_r
                    FROM shadow_outcomes
                    WHERE symbol = $sym
                    ORDER BY decision_at_utc DESC
                    LIMIT $lim;";
            if (!string.IsNullOrWhiteSpace(symbol))
                cmd.Parameters.AddWithValue("$sym", symbol);
            cmd.Parameters.AddWithValue("$lim", Math.Max(1, limit));

            var list = new List<ShadowOutcomeRecord>();
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                list.Add(new ShadowOutcomeRecord(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                    reader.IsDBNull(5) ? null : ParseDecimal(reader.GetString(5))));
            }

            return list;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<int> UpdateShadowOutcomeResultAsync(
        long id,
        decimal outcomeNetR,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE shadow_outcomes
                SET outcome_net_r = $r
                WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$r", outcomeNetR.ToString(CultureInfo.InvariantCulture));
            return await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();

    private static async Task EnsurePositionEventColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(position_events);";
            using var reader = await pragma.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                columns.Add(reader.GetString(1));
        }

        if (!columns.Contains("action"))
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE position_events ADD COLUMN action TEXT NOT NULL DEFAULT 'Open';";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!columns.Contains("closed_pnl_usd"))
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE position_events ADD COLUMN closed_pnl_usd TEXT NOT NULL DEFAULT '0';";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static decimal ParseDecimal(string value) =>
        decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d) ? d : 0m;
}

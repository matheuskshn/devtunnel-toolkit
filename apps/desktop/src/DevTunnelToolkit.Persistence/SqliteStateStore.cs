using System.Text.Json;
using DevTunnelToolkit.Core;
using DevTunnelToolkit.Platform;
using Microsoft.Data.Sqlite;

namespace DevTunnelToolkit.Persistence;

public sealed class SqliteStateStore : IStateStore
{
    private readonly SqliteConnection connection;
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public SqliteStateStore(LocalPaths paths)
    {
        paths.Initialize();
        LocalPaths.EnsurePrivateFile(paths.DatabasePath);
        LocalPaths.EnsurePrivateFile(paths.DatabasePath + "-wal");
        LocalPaths.EnsurePrivateFile(paths.DatabasePath + "-shm");
        LocalPaths.EnsurePrivateFile(paths.DatabasePath + "-journal");
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS logs (sequence INTEGER PRIMARY KEY, timestamp TEXT NOT NULL, payload TEXT NOT NULL);
            INSERT OR IGNORE INTO metadata(key,value) VALUES ('schema','3');
            """;
        command.ExecuteNonQuery();
        command.CommandText = "SELECT value FROM metadata WHERE key='schema'";
        if ((string?)command.ExecuteScalar() is not ("1" or "2" or "3")) throw new ToolkitException("UNSUPPORTED_DATABASE_VERSION");
        LocalPaths.EnsurePrivateFile(paths.DatabasePath);
    }

    public async Task<ToolkitState> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM metadata WHERE key='state'";
            var json = await command.ExecuteScalarAsync(cancellationToken) as string;
            if (json is null) return ToolkitState.Empty;
            var state = JsonSerializer.Deserialize<ToolkitState>(json, Json) ?? throw new ToolkitException("INVALID_SAVED_STATE");
            foreach (var account in state.Accounts) Validation.Account(account);
            foreach (var profile in state.Tunnels) Validation.Tunnel(profile);
            Validation.Settings(state.Settings);
            if (state.Accounts.Length > 50 || state.Tunnels.Length > 200 || state.Accounts.Select(a => a.Id).Distinct().Count() != state.Accounts.Length || state.Tunnels.Select(t => t.Id).Distinct().Count() != state.Tunnels.Length || state.Tunnels.Any(t => !state.Accounts.Any(a => a.Id == t.AccountId)) || state.Settings.Pac.Rules.Any(r => !state.Tunnels.Any(t => t.Id == r.ProfileId)) || state.Settings.Proxy.Rules.Any(r => r.Action == RouteAction.Tunnel && !state.Tunnels.Any(t => t.Id == r.ProfileId)))
                throw new ToolkitException("INVALID_SAVED_STATE");
            return state;
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(ToolkitState state, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var command = connection.CreateCommand();
            using var transaction = connection.BeginTransaction();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO metadata(key,value) VALUES('state',$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value; UPDATE metadata SET value='3' WHERE key='schema';";
            command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(state, Json));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally { gate.Release(); }
    }

    public async Task AppendLogAsync(LogEntry entry, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO logs(sequence,timestamp,payload) VALUES($sequence,$time,$payload)";
            command.Parameters.AddWithValue("$sequence", entry.Sequence);
            command.Parameters.AddWithValue("$time", entry.Timestamp.ToString("O"));
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(entry, Json));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { gate.Release(); }
    }

    public async Task<LogEntry[]> ReadLogsAsync(int limit, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM logs ORDER BY sequence DESC LIMIT $limit";
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var entries = new List<LogEntry>();
            while (await reader.ReadAsync(cancellationToken)) entries.Add(JsonSerializer.Deserialize<LogEntry>(reader.GetString(0), Json)!);
            return [.. entries.AsEnumerable().Reverse()];
        }
        finally { gate.Release(); }
    }

    public async Task PruneLogsAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM logs WHERE timestamp < $before OR sequence NOT IN (SELECT sequence FROM logs ORDER BY sequence DESC LIMIT 10000)";
            command.Parameters.AddWithValue("$before", DateTimeOffset.UtcNow.AddDays(-retentionDays).ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await connection.DisposeAsync();
        gate.Dispose();
    }
}

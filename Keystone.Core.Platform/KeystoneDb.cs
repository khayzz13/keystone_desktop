// KeystoneDb - SQLite persistence for workspaces, layouts, settings, and app-defined tables
// DB: ~/.keystone/{appId}/keystone.db

using Microsoft.Data.Sqlite;

namespace Keystone.Core.Platform;

public static class KeystoneDb
{
    private static string _dbPath = "";
    private static SqliteConnection? _conn;

    /// <summary>
    /// Initialize with app-scoped database path.
    /// Each app gets its own database at ~/.keystone/{appId}/keystone.db
    /// </summary>
    public static void Init(string appId = "default")
    {
        var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".keystone", appId);
        Directory.CreateDirectory(baseDir);
        _dbPath = Path.Combine(baseDir, "keystone.db");

        _conn = new SqliteConnection($"Data Source={_dbPath}");
        _conn.Open();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;

            CREATE TABLE IF NOT EXISTS workspaces (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                created_at TEXT NOT NULL,
                is_active INTEGER DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS workspace_windows (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                workspace_id TEXT NOT NULL REFERENCES workspaces(id) ON DELETE CASCADE,
                window_type TEXT NOT NULL,
                x REAL, y REAL, width REAL, height REAL,
                config TEXT
            );

            CREATE TABLE IF NOT EXISTS layouts (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                window_type TEXT NOT NULL,
                config TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS migrations (
                id TEXT PRIMARY KEY,
                applied_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
        Console.WriteLine($"[KeystoneDb] Initialized: {_dbPath}");
    }

    /// <summary>
    /// Run an idempotent migration. Skips if already applied.
    /// Apps define custom tables in ICorePlugin.Initialize() via this method.
    /// </summary>
    public static void Migrate(string migrationId, string sql)
    {
        if (_conn == null) return;

        using var check = _conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM migrations WHERE id = $id";
        check.Parameters.AddWithValue("$id", migrationId);
        if ((long)check.ExecuteScalar()! > 0) return;

        using var tx = _conn.BeginTransaction();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO migrations (id, applied_at) VALUES ($id, $ts)";
            cmd.Parameters.AddWithValue("$id", migrationId);
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        Console.WriteLine($"[KeystoneDb] Applied migration: {migrationId}");
    }

    // === Workspaces ===

    public static string SaveWorkspace(string name, List<WindowSnapshotDto> windows)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        using var tx = _conn!.BeginTransaction();

        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO workspaces (id, name, created_at) VALUES ($id, $name, $ts)";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }

        foreach (var w in windows)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO workspace_windows (workspace_id, window_type, x, y, width, height, config) VALUES ($wid, $type, $x, $y, $w, $h, $cfg)";
            cmd.Parameters.AddWithValue("$wid", id);
            cmd.Parameters.AddWithValue("$type", w.WindowType);
            cmd.Parameters.AddWithValue("$x", w.X);
            cmd.Parameters.AddWithValue("$y", w.Y);
            cmd.Parameters.AddWithValue("$w", w.Width);
            cmd.Parameters.AddWithValue("$h", w.Height);
            cmd.Parameters.AddWithValue("$cfg", (object?)w.ConfigJson ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        SetActiveWorkspace(id);
        Console.WriteLine($"[KeystoneDb] Saved workspace '{name}' ({id}) with {windows.Count} windows");
        return id;
    }

    public static List<(string Id, string Name, bool IsActive)> GetWorkspaces()
    {
        var result = new List<(string, string, bool)>();
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "SELECT id, name, is_active FROM workspaces ORDER BY created_at DESC";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2) == 1));
        return result;
    }

    public static (string Name, List<WindowSnapshotDto> Windows)? LoadWorkspace(string id)
    {
        string? name;
        using (var cmd = _conn!.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM workspaces WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            name = cmd.ExecuteScalar() as string;
        }
        if (name == null) return null;

        var windows = new List<WindowSnapshotDto>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT window_type, x, y, width, height, config FROM workspace_windows WHERE workspace_id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                windows.Add(new WindowSnapshotDto
                {
                    WindowType = reader.GetString(0),
                    X = reader.GetDouble(1),
                    Y = reader.GetDouble(2),
                    Width = reader.GetDouble(3),
                    Height = reader.GetDouble(4),
                    ConfigJson = reader.IsDBNull(5) ? null : reader.GetString(5)
                });
            }
        }

        SetActiveWorkspace(id);
        return (name, windows);
    }

    public static void DeleteWorkspace(string id)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "DELETE FROM workspaces WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public static void SetActiveWorkspace(string id)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "UPDATE workspaces SET is_active = CASE WHEN id = $id THEN 1 ELSE 0 END";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public static string? GetActiveWorkspaceId()
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "SELECT id FROM workspaces WHERE is_active = 1 LIMIT 1";
        return cmd.ExecuteScalar() as string;
    }

    // === Layouts ===

    public static string SaveLayout(string name, string windowType, string configJson)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "INSERT INTO layouts (id, name, window_type, config) VALUES ($id, $name, $type, $cfg)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$type", windowType);
        cmd.Parameters.AddWithValue("$cfg", configJson);
        cmd.ExecuteNonQuery();
        Console.WriteLine($"[KeystoneDb] Saved layout '{name}' for {windowType}");
        return id;
    }

    public static List<(string Id, string Name)> GetLayouts(string windowType)
    {
        var result = new List<(string, string)>();
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "SELECT id, name FROM layouts WHERE window_type = $type ORDER BY name";
        cmd.Parameters.AddWithValue("$type", windowType);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add((reader.GetString(0), reader.GetString(1)));
        return result;
    }

    public static (string Name, string WindowType, string ConfigJson)? LoadLayout(string id)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "SELECT name, window_type, config FROM layouts WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    public static void DeleteLayout(string id)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "DELETE FROM layouts WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public static void RenameLayout(string id, string newName)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "UPDATE layouts SET name = $name WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", newName);
        cmd.ExecuteNonQuery();
    }

    // === Settings ===

    public static string? GetSetting(string key)
    {
        if (_conn == null) return null;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public static void SetSetting(string key, string value)
    {
        if (_conn == null) return;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO settings (key, value) VALUES ($k, $v)";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public static float GetFloat(string key, float fallback = 0f)
        => float.TryParse(GetSetting(key), out var v) ? v : fallback;

    public static int GetInt(string key, int fallback = 0)
        => int.TryParse(GetSetting(key), out var v) ? v : fallback;

    public static bool GetBool(string key, bool fallback = false)
        => GetSetting(key) is string s ? s == "1" : fallback;

    public static void SetFloat(string key, float value) => SetSetting(key, value.ToString("G"));
    public static void SetInt(string key, int value) => SetSetting(key, value.ToString());
    public static void SetBool(string key, bool value) => SetSetting(key, value ? "1" : "0");

    // === Workspace Tab Groups ===

    public static void SaveWorkspaceTabGroups(string workspaceId, List<TabGroupSnapshotDto> groups)
    {
        if (_conn == null || groups.Count == 0) return;

        // Run migration for new tables (idempotent)
        Migrate("001_workspace_groups", """
            CREATE TABLE IF NOT EXISTS workspace_tab_groups (
                workspace_id TEXT NOT NULL REFERENCES workspaces(id) ON DELETE CASCADE,
                group_id TEXT NOT NULL,
                window_types TEXT NOT NULL,
                active_index INTEGER DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS workspace_bind_containers (
                workspace_id TEXT NOT NULL REFERENCES workspaces(id) ON DELETE CASCADE,
                container_id TEXT NOT NULL,
                layout_type TEXT NOT NULL,
                orientation TEXT NOT NULL,
                ratios TEXT NOT NULL,
                window_types TEXT NOT NULL
            );
            """);

        foreach (var g in groups)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO workspace_tab_groups (workspace_id, group_id, window_types, active_index) VALUES ($wid, $gid, $types, $idx)";
            cmd.Parameters.AddWithValue("$wid", workspaceId);
            cmd.Parameters.AddWithValue("$gid", g.GroupId);
            cmd.Parameters.AddWithValue("$types", System.Text.Json.JsonSerializer.Serialize(g.WindowTypes));
            cmd.Parameters.AddWithValue("$idx", g.ActiveIndex);
            cmd.ExecuteNonQuery();
        }
    }

    public static List<TabGroupSnapshotDto> LoadWorkspaceTabGroups(string workspaceId)
    {
        var result = new List<TabGroupSnapshotDto>();
        if (_conn == null) return result;

        // Check if table exists
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT group_id, window_types, active_index FROM workspace_tab_groups WHERE workspace_id = $wid";
            cmd.Parameters.AddWithValue("$wid", workspaceId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new TabGroupSnapshotDto
                {
                    GroupId = reader.GetString(0),
                    WindowTypes = System.Text.Json.JsonSerializer.Deserialize<List<string>>(reader.GetString(1)) ?? new(),
                    ActiveIndex = reader.GetInt32(2)
                });
            }
        }
        catch { /* table doesn't exist yet */ }
        return result;
    }

    // === Workspace Bind Containers ===

    public static void SaveWorkspaceBindContainers(string workspaceId, List<BindContainerSnapshotDto> containers)
    {
        if (_conn == null || containers.Count == 0) return;

        // Ensure migration has run
        Migrate("001_workspace_groups", """
            CREATE TABLE IF NOT EXISTS workspace_tab_groups (
                workspace_id TEXT NOT NULL REFERENCES workspaces(id) ON DELETE CASCADE,
                group_id TEXT NOT NULL,
                window_types TEXT NOT NULL,
                active_index INTEGER DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS workspace_bind_containers (
                workspace_id TEXT NOT NULL REFERENCES workspaces(id) ON DELETE CASCADE,
                container_id TEXT NOT NULL,
                layout_type TEXT NOT NULL,
                orientation TEXT NOT NULL,
                ratios TEXT NOT NULL,
                window_types TEXT NOT NULL
            );
            """);

        foreach (var c in containers)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO workspace_bind_containers (workspace_id, container_id, layout_type, orientation, ratios, window_types) VALUES ($wid, $cid, $lt, $or, $ratios, $types)";
            cmd.Parameters.AddWithValue("$wid", workspaceId);
            cmd.Parameters.AddWithValue("$cid", c.ContainerId);
            cmd.Parameters.AddWithValue("$lt", c.LayoutType);
            cmd.Parameters.AddWithValue("$or", c.Orientation);
            cmd.Parameters.AddWithValue("$ratios", System.Text.Json.JsonSerializer.Serialize(c.Ratios));
            cmd.Parameters.AddWithValue("$types", System.Text.Json.JsonSerializer.Serialize(c.WindowTypes));
            cmd.ExecuteNonQuery();
        }
    }

    public static List<BindContainerSnapshotDto> LoadWorkspaceBindContainers(string workspaceId)
    {
        var result = new List<BindContainerSnapshotDto>();
        if (_conn == null) return result;

        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT container_id, layout_type, orientation, ratios, window_types FROM workspace_bind_containers WHERE workspace_id = $wid";
            cmd.Parameters.AddWithValue("$wid", workspaceId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new BindContainerSnapshotDto
                {
                    ContainerId = reader.GetString(0),
                    LayoutType = reader.GetString(1),
                    Orientation = reader.GetString(2),
                    Ratios = System.Text.Json.JsonSerializer.Deserialize<List<float>>(reader.GetString(3)) ?? new(),
                    WindowTypes = System.Text.Json.JsonSerializer.Deserialize<List<string>>(reader.GetString(4)) ?? new()
                });
            }
        }
        catch { /* table doesn't exist yet */ }
        return result;
    }
}

// DTO for workspace tab group snapshots
public class TabGroupSnapshotDto
{
    public string GroupId { get; set; } = "";
    public List<string> WindowTypes { get; set; } = new();
    public int ActiveIndex { get; set; }
}

// DTO for workspace bind container snapshots
public class BindContainerSnapshotDto
{
    public string ContainerId { get; set; } = "";
    public string LayoutType { get; set; } = "Split";
    public string Orientation { get; set; } = "Horizontal";
    public List<float> Ratios { get; set; } = new();
    public List<string> WindowTypes { get; set; } = new();
}

// DTO for workspace window snapshots (no dependency on Runtime types)
public class WindowSnapshotDto
{
    public string WindowType { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string? ConfigJson { get; set; }
}

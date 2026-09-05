using Microsoft.Data.Sqlite;
using MeuralManager.Core.Api;
using MeuralManager.Core.Models;

namespace MeuralManager.Core.Data;

// Local SQLite cache of playlists (galleries) and uploads (items), so
// PlaylistManagerControl can browse instantly instead of hitting the
// (slow) Meural API on every click. Mutations always go through
// MeuralApiClient first; the cache is only ever updated afterward, either
// via a full FullRefreshAsync or via the small per-gallery/per-item
// upsert methods below so a single mutation doesn't require re-fetching
// the whole account.
public sealed class PlaylistCacheStore
{
    private readonly string _connectionString;

    public PlaylistCacheStore(string? dbPath = null)
    {
        dbPath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeuralManager", "playlist-cache.db");

        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = $"Data Source={dbPath}";
    }

    // Generic key/value settings storage (AI provider/API keys/rename style, etc.) - separate
    // from CacheMeta, which is exclusively for this store's own cache bookkeeping. Values are
    // opaque strings; encrypting anything sensitive (like an API key) before it reaches here is
    // the caller's responsibility - this store has no knowledge of ASP.NET Data Protection.
    public async Task<string?> GetSettingAsync(string key, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM Settings WHERE Key = @key";
        cmd.Parameters.AddWithValue("@key", key);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task SetSettingAsync(string key, string? value, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        if (value is null)
        {
            cmd.CommandText = "DELETE FROM Settings WHERE Key = @key";
            cmd.Parameters.AddWithValue("@key", key);
        }
        else
        {
            cmd.CommandText = """
                INSERT INTO Settings (Key, Value) VALUES (@key, @value)
                ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value
                """;
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@value", value);
        }
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Favorited playlist ids - kept in their own table, untouched by FullRefreshAsync, so
    // marking a playlist a favorite survives a rescan even though rescanning wipes and
    // re-inserts every row in Galleries.
    public async Task<HashSet<long>> GetFavoriteGalleryIdsAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT GalleryId FROM Favorites";

        var result = new HashSet<long>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetInt64(0));
        return result;
    }

    public async Task SetGalleryFavoriteAsync(long galleryId, bool favorite, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = favorite
            ? "INSERT OR IGNORE INTO Favorites (GalleryId) VALUES (@id)"
            : "DELETE FROM Favorites WHERE GalleryId = @id";
        cmd.Parameters.AddWithValue("@id", galleryId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<DateTime?> GetLastRefreshedUtcAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM CacheMeta WHERE Key = 'LastRefreshedUtc'";
        var value = await cmd.ExecuteScalarAsync(ct) as string;
        return value is null ? null : DateTime.Parse(value).ToUniversalTime();
    }

    public async Task<List<MeuralGallery>> GetGalleriesAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);

        var itemIdsByGallery = new Dictionary<long, List<long>>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT GalleryId, ItemId FROM GalleryItems";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var galleryId = reader.GetInt64(0);
                if (!itemIdsByGallery.TryGetValue(galleryId, out var list))
                    itemIdsByGallery[galleryId] = list = new List<long>();
                list.Add(reader.GetInt64(1));
            }
        }

        var galleries = new List<MeuralGallery>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, Name FROM Galleries ORDER BY Name COLLATE NOCASE";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetInt64(0);
                galleries.Add(new MeuralGallery
                {
                    Id = id,
                    Name = reader.IsDBNull(1) ? null : reader.GetString(1),
                    ItemIds = itemIdsByGallery.TryGetValue(id, out var ids) ? ids : new List<long>(),
                });
            }
        }

        return galleries;
    }

    public async Task<List<MeuralItem>> GetGalleryItemsAsync(long galleryId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT i.Id, i.Name, i.Image, i.CreatedAt
            FROM Items i
            JOIN GalleryItems gi ON gi.ItemId = i.Id
            WHERE gi.GalleryId = @galleryId
            ORDER BY i.Name COLLATE NOCASE
            """;
        cmd.Parameters.AddWithValue("@galleryId", galleryId);

        return await ReadItemsAsync(cmd, ct);
    }

    public async Task<List<MeuralItem>> GetAllItemsAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, Image, CreatedAt FROM Items ORDER BY Name COLLATE NOCASE";

        return await ReadItemsAsync(cmd, ct);
    }

    // Looks up just one item's last-known signed Image URL - used by the background image
    // cache to decide whether it already has a URL to try before falling back to the API.
    public async Task<string?> GetItemImageAsync(long itemId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Image FROM Items WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", itemId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task<List<MeuralDevice>> GetDevicesAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Alias, LocalIp FROM Devices ORDER BY Alias COLLATE NOCASE";

        var devices = new List<MeuralDevice>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            devices.Add(new MeuralDevice
            {
                Id = reader.GetInt64(0),
                Alias = reader.IsDBNull(1) ? null : reader.GetString(1),
                LocalIp = reader.IsDBNull(2) ? null : reader.GetString(2),
            });
        }
        return devices;
    }

    // GalleryId -> the alias(es) of every frame it's loaded on, for the
    // playlist list's "Frames" column.
    public async Task<Dictionary<long, List<string>>> GetGalleryFrameNamesAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT dg.GalleryId, COALESCE(d.Alias, 'frame ' || d.Id)
            FROM DeviceGalleries dg
            JOIN Devices d ON d.Id = dg.DeviceId
            ORDER BY d.Alias COLLATE NOCASE
            """;

        var result = new Dictionary<long, List<string>>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var galleryId = reader.GetInt64(0);
            var alias = reader.GetString(1);
            if (!result.TryGetValue(galleryId, out var names))
                result[galleryId] = names = new List<string>();
            names.Add(alias);
        }
        return result;
    }

    // DeviceId -> the full gallery objects loaded on it, mirroring the shape of the live API's
    // GetDeviceGalleriesAsync per-device calls - used to hydrate an in-memory cache (e.g. the
    // web app's per-circuit session state) from this store without re-hitting the API.
    public async Task<Dictionary<long, List<MeuralGallery>>> GetDeviceGalleriesAsync(CancellationToken ct)
    {
        var galleries = await GetGalleriesAsync(ct);
        var galleriesById = galleries.Where(g => g.Id.HasValue).ToDictionary(g => g.Id!.Value);

        var result = new Dictionary<long, List<MeuralGallery>>();

        await using var conn = await OpenAsync(ct);

        // Every known device gets an entry - even an empty one - so a frame with nothing loaded
        // on it isn't just absent from the result. The live API-backed scan path always includes
        // every device this way; this query used to only add a device once it had at least one
        // gallery, silently dropping blank frames and diverging from that behavior.
        await using (var deviceCmd = conn.CreateCommand())
        {
            deviceCmd.CommandText = "SELECT Id FROM Devices";
            await using var deviceReader = await deviceCmd.ExecuteReaderAsync(ct);
            while (await deviceReader.ReadAsync(ct))
                result[deviceReader.GetInt64(0)] = [];
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT DeviceId, GalleryId FROM DeviceGalleries";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var deviceId = reader.GetInt64(0);
                var galleryId = reader.GetInt64(1);
                if (!galleriesById.TryGetValue(galleryId, out var gallery))
                    continue;
                if (!result.TryGetValue(deviceId, out var list))
                    result[deviceId] = list = [];
                list.Add(gallery);
            }
        }

        return result;
    }

    // Records that a gallery was just loaded onto a device, after a
    // confirmed-successful AddGalleryToDeviceAsync API call.
    public async Task AddDeviceGalleryAsync(long deviceId, long galleryId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO DeviceGalleries (DeviceId, GalleryId) VALUES (@deviceId, @galleryId)";
        cmd.Parameters.AddWithValue("@deviceId", deviceId);
        cmd.Parameters.AddWithValue("@galleryId", galleryId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Records that a gallery was just unloaded from a device, after a
    // confirmed-successful RemoveGalleryFromDeviceAsync API call.
    public async Task RemoveDeviceGalleryAsync(long deviceId, long galleryId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM DeviceGalleries WHERE DeviceId = @deviceId AND GalleryId = @galleryId";
        cmd.Parameters.AddWithValue("@deviceId", deviceId);
        cmd.Parameters.AddWithValue("@galleryId", galleryId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Re-fetches everything from the API and replaces the entire local
    // cache. Slow (it's exactly the full account scan the cache exists to
    // avoid) - meant to be triggered explicitly via a "Refresh Cache"
    // button, not on every browse.
    public async Task FullRefreshAsync(MeuralApiClient client, IProgress<string>? progress, CancellationToken ct)
    {
        var galleries = await client.GetAllGalleriesAsync(progress, ct);
        var items = await client.GetAllItemsAsync(progress, ct);

        var devices = await client.GetAllDevicesAsync(progress, ct);
        var deviceGalleryPairs = new List<(long DeviceId, long GalleryId)>();
        foreach (var device in devices)
        {
            if (device.Id is not long deviceId)
                continue;

            var deviceGalleries = await client.GetDeviceGalleriesAsync(deviceId, progress, ct);
            foreach (var g in deviceGalleries)
            {
                if (g.Id is long galleryId)
                    deviceGalleryPairs.Add((deviceId, galleryId));
            }
        }

        await using var conn = await OpenAsync(ct);
        await using var tx = conn.BeginTransaction();

        await ExecAsync(conn, tx, "DELETE FROM GalleryItems", ct);
        await ExecAsync(conn, tx, "DELETE FROM Galleries", ct);
        await ExecAsync(conn, tx, "DELETE FROM Items", ct);
        await ExecAsync(conn, tx, "DELETE FROM DeviceGalleries", ct);
        await ExecAsync(conn, tx, "DELETE FROM Devices", ct);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO Items (Id, Name, Image, CreatedAt) VALUES (@id, @name, @image, @createdAt)";
            var pId = cmd.Parameters.Add("@id", SqliteType.Integer);
            var pName = cmd.Parameters.Add("@name", SqliteType.Text);
            var pImage = cmd.Parameters.Add("@image", SqliteType.Text);
            var pCreatedAt = cmd.Parameters.Add("@createdAt", SqliteType.Text);

            foreach (var item in items)
            {
                if (item.Id is not long id)
                    continue;

                pId.Value = id;
                pName.Value = (object?)item.Name ?? DBNull.Value;
                pImage.Value = (object?)item.Image ?? DBNull.Value;
                pCreatedAt.Value = (object?)item.CreatedAt ?? DBNull.Value;
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        await using (var galleryCmd = conn.CreateCommand())
        await using (var membershipCmd = conn.CreateCommand())
        {
            galleryCmd.Transaction = tx;
            galleryCmd.CommandText = "INSERT INTO Galleries (Id, Name) VALUES (@id, @name)";
            var gId = galleryCmd.Parameters.Add("@id", SqliteType.Integer);
            var gName = galleryCmd.Parameters.Add("@name", SqliteType.Text);

            membershipCmd.Transaction = tx;
            membershipCmd.CommandText = "INSERT INTO GalleryItems (GalleryId, ItemId) VALUES (@galleryId, @itemId)";
            var mGalleryId = membershipCmd.Parameters.Add("@galleryId", SqliteType.Integer);
            var mItemId = membershipCmd.Parameters.Add("@itemId", SqliteType.Integer);

            foreach (var gallery in galleries)
            {
                if (gallery.Id is not long id)
                    continue;

                gId.Value = id;
                gName.Value = (object?)gallery.Name ?? DBNull.Value;
                await galleryCmd.ExecuteNonQueryAsync(ct);

                foreach (var itemId in gallery.ItemIds ?? [])
                {
                    mGalleryId.Value = id;
                    mItemId.Value = itemId;
                    await membershipCmd.ExecuteNonQueryAsync(ct);
                }
            }
        }

        await using (var deviceCmd = conn.CreateCommand())
        await using (var deviceGalleryCmd = conn.CreateCommand())
        {
            deviceCmd.Transaction = tx;
            deviceCmd.CommandText = "INSERT INTO Devices (Id, Alias, LocalIp) VALUES (@id, @alias, @localIp)";
            var dId = deviceCmd.Parameters.Add("@id", SqliteType.Integer);
            var dAlias = deviceCmd.Parameters.Add("@alias", SqliteType.Text);
            var dLocalIp = deviceCmd.Parameters.Add("@localIp", SqliteType.Text);

            deviceGalleryCmd.Transaction = tx;
            deviceGalleryCmd.CommandText = "INSERT INTO DeviceGalleries (DeviceId, GalleryId) VALUES (@deviceId, @galleryId)";
            var dgDeviceId = deviceGalleryCmd.Parameters.Add("@deviceId", SqliteType.Integer);
            var dgGalleryId = deviceGalleryCmd.Parameters.Add("@galleryId", SqliteType.Integer);

            foreach (var device in devices)
            {
                if (device.Id is not long id)
                    continue;

                dId.Value = id;
                dAlias.Value = (object?)device.Alias ?? DBNull.Value;
                dLocalIp.Value = (object?)device.LocalIp ?? DBNull.Value;
                await deviceCmd.ExecuteNonQueryAsync(ct);
            }

            foreach (var (deviceId, galleryId) in deviceGalleryPairs)
            {
                dgDeviceId.Value = deviceId;
                dgGalleryId.Value = galleryId;
                await deviceGalleryCmd.ExecuteNonQueryAsync(ct);
            }
        }

        await SetMetaAsync(conn, tx, "LastRefreshedUtc", DateTime.UtcNow.ToString("o"), ct);
        tx.Commit();

        progress?.Report($"Cache refreshed: {galleries.Count} playlist(s), {items.Count} upload(s), {devices.Count} frame(s).");
    }

    // Inserts or updates just a gallery's Id/Name - never touches its
    // GalleryItems membership rows, so it's safe to call after both
    // create (no membership yet) and rename (membership unchanged).
    public async Task UpsertGalleryAsync(long galleryId, string? name, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Galleries (Id, Name) VALUES (@id, @name)
            ON CONFLICT(Id) DO UPDATE SET Name = excluded.Name
            """;
        cmd.Parameters.AddWithValue("@id", galleryId);
        cmd.Parameters.AddWithValue("@name", (object?)name ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateItemNameAsync(long itemId, string? name, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Items SET Name = @name WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", itemId);
        cmd.Parameters.AddWithValue("@name", (object?)name ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Patches just an item's Image URL - used to persist a freshly re-signed CDN URL after the
    // one captured at the last scan expired (Meural signs Image URLs with a short-lived Expires
    // param, but this cache survives indefinitely between explicit rescans).
    public async Task UpdateItemImageAsync(long itemId, string? image, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Items SET Image = @image WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", itemId);
        cmd.Parameters.AddWithValue("@image", (object?)image ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveGalleryAsync(long galleryId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = conn.BeginTransaction();
        await ExecAsync(conn, tx, "DELETE FROM GalleryItems WHERE GalleryId = @id", ct, ("@id", galleryId));
        await ExecAsync(conn, tx, "DELETE FROM Galleries WHERE Id = @id", ct, ("@id", galleryId));
        await ExecAsync(conn, tx, "DELETE FROM Favorites WHERE GalleryId = @id", ct, ("@id", galleryId));
        tx.Commit();
    }

    // Removes uploads that were just deleted from the Meural account (e.g. via the orphan
    // cleanup flow), so a future cache hydration doesn't resurrect them as orphans again.
    public async Task RemoveItemsAsync(IEnumerable<long> itemIds, CancellationToken ct)
    {
        var ids = itemIds.ToList();
        if (ids.Count == 0)
            return;

        await using var conn = await OpenAsync(ct);
        await using var tx = conn.BeginTransaction();

        var placeholders = string.Join(",", ids.Select((_, i) => $"@id{i}"));

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM GalleryItems WHERE ItemId IN ({placeholders})";
            for (var i = 0; i < ids.Count; i++)
                cmd.Parameters.AddWithValue($"@id{i}", ids[i]);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM Items WHERE Id IN ({placeholders})";
            for (var i = 0; i < ids.Count; i++)
                cmd.Parameters.AddWithValue($"@id{i}", ids[i]);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        tx.Commit();
    }

    // Replaces a single gallery's membership with the given authoritative
    // item list (as just re-fetched from the API after an add/remove/
    // upload), upserting each item's own row along the way so newly
    // uploaded items land in the Items table too.
    public async Task ReplaceGalleryItemsAsync(long galleryId, IReadOnlyList<MeuralItem> items, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = conn.BeginTransaction();

        await ExecAsync(conn, tx, "DELETE FROM GalleryItems WHERE GalleryId = @id", ct, ("@id", galleryId));

        await using (var itemCmd = conn.CreateCommand())
        await using (var membershipCmd = conn.CreateCommand())
        {
            itemCmd.Transaction = tx;
            itemCmd.CommandText = """
                INSERT INTO Items (Id, Name, Image, CreatedAt) VALUES (@id, @name, @image, @createdAt)
                ON CONFLICT(Id) DO UPDATE SET Name = excluded.Name, Image = excluded.Image, CreatedAt = excluded.CreatedAt
                """;
            var pId = itemCmd.Parameters.Add("@id", SqliteType.Integer);
            var pName = itemCmd.Parameters.Add("@name", SqliteType.Text);
            var pImage = itemCmd.Parameters.Add("@image", SqliteType.Text);
            var pCreatedAt = itemCmd.Parameters.Add("@createdAt", SqliteType.Text);

            membershipCmd.Transaction = tx;
            membershipCmd.CommandText = "INSERT INTO GalleryItems (GalleryId, ItemId) VALUES (@galleryId, @itemId)";
            var mGalleryId = membershipCmd.Parameters.Add("@galleryId", SqliteType.Integer);
            var mItemId = membershipCmd.Parameters.Add("@itemId", SqliteType.Integer);

            foreach (var item in items)
            {
                if (item.Id is not long id)
                    continue;

                pId.Value = id;
                pName.Value = (object?)item.Name ?? DBNull.Value;
                pImage.Value = (object?)item.Image ?? DBNull.Value;
                pCreatedAt.Value = (object?)item.CreatedAt ?? DBNull.Value;
                await itemCmd.ExecuteNonQueryAsync(ct);

                mGalleryId.Value = galleryId;
                mItemId.Value = id;
                await membershipCmd.ExecuteNonQueryAsync(ct);
            }
        }

        tx.Commit();
    }

    private static async Task<List<MeuralItem>> ReadItemsAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var items = new List<MeuralItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new MeuralItem
            {
                Id = reader.GetInt64(0),
                Name = reader.IsDBNull(1) ? null : reader.GetString(1),
                Image = reader.IsDBNull(2) ? null : reader.GetString(2),
                CreatedAt = reader.IsDBNull(3) ? null : reader.GetString(3),
            });
        }
        return items;
    }

    private static async Task ExecAsync(SqliteConnection conn, SqliteTransaction tx, string sql, CancellationToken ct, (string name, object value)? param = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        if (param is { } p)
            cmd.Parameters.AddWithValue(p.name, p.value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task SetMetaAsync(SqliteConnection conn, SqliteTransaction tx, string key, string value, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO CacheMeta (Key, Value) VALUES (@key, @value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value
            """;
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Galleries (
                Id INTEGER PRIMARY KEY,
                Name TEXT
            );
            CREATE TABLE IF NOT EXISTS Items (
                Id INTEGER PRIMARY KEY,
                Name TEXT,
                Image TEXT,
                CreatedAt TEXT
            );
            CREATE TABLE IF NOT EXISTS GalleryItems (
                GalleryId INTEGER NOT NULL,
                ItemId INTEGER NOT NULL,
                PRIMARY KEY (GalleryId, ItemId)
            );
            CREATE TABLE IF NOT EXISTS Devices (
                Id INTEGER PRIMARY KEY,
                Alias TEXT,
                LocalIp TEXT
            );
            CREATE TABLE IF NOT EXISTS DeviceGalleries (
                DeviceId INTEGER NOT NULL,
                GalleryId INTEGER NOT NULL,
                PRIMARY KEY (DeviceId, GalleryId)
            );
            CREATE TABLE IF NOT EXISTS CacheMeta (
                Key TEXT PRIMARY KEY,
                Value TEXT
            );
            CREATE TABLE IF NOT EXISTS Settings (
                Key TEXT PRIMARY KEY,
                Value TEXT
            );
            CREATE TABLE IF NOT EXISTS Favorites (
                GalleryId INTEGER PRIMARY KEY
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct);

        // Devices existed before LocalIp did - CREATE TABLE IF NOT EXISTS above is a no-op
        // against a DB from before that column existed, so add it here if it's still missing.
        await using (var pragmaCmd = conn.CreateCommand())
        {
            pragmaCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Devices') WHERE name = 'LocalIp'";
            var hasLocalIp = (long)(await pragmaCmd.ExecuteScalarAsync(ct))! > 0;
            if (!hasLocalIp)
            {
                await using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE Devices ADD COLUMN LocalIp TEXT";
                await alterCmd.ExecuteNonQueryAsync(ct);
            }
        }

        return conn;
    }
}

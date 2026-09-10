using System.Text.Json;
using ClipBridge.Models;
using Microsoft.Data.Sqlite;

namespace ClipBridge.Core;

/// <summary>SQLite-backed store. Keeps a metadata cache of live items in memory; image bytes are loaded on demand.</summary>
public sealed class Store : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _gate = new();
    private readonly Dictionary<string, ClipItem> _cache = new();

    public Store(string path)
    {
        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec(@"CREATE TABLE IF NOT EXISTS items(
            id TEXT PRIMARY KEY, kind INTEGER, text TEXT, html TEXT, rtf TEXT, image BLOB, thumb BLOB,
            imgw INTEGER, imgh INTEGER, files TEXT, hash TEXT, created INTEGER, modified INTEGER, version INTEGER,
            origin TEXT, pinned INTEGER, pinorder INTEGER, deleted INTEGER, ocrdone INTEGER, size INTEGER);
            CREATE INDEX IF NOT EXISTS ix_items_created ON items(created);
            CREATE INDEX IF NOT EXISTS ix_items_hash ON items(hash);");
        LoadCache();
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void LoadCache()
    {
        lock (_gate)
        {
            _cache.Clear();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id,kind,text,html,rtf,thumb,imgw,imgh,files,hash,created,modified,version,origin,pinned,pinorder,deleted,ocrdone,size FROM items";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var it = new ClipItem
                {
                    Id = r.GetString(0), Kind = (ClipKind)r.GetInt32(1), Text = r.IsDBNull(2) ? "" : r.GetString(2),
                    Html = r.IsDBNull(3) ? null : r.GetString(3), Rtf = r.IsDBNull(4) ? null : r.GetString(4),
                    Thumb = r.IsDBNull(5) ? null : (byte[])r[5], ImageWidth = r.GetInt32(6), ImageHeight = r.GetInt32(7),
                    Files = r.IsDBNull(8) ? new() : (JsonSerializer.Deserialize<List<ClipFile>>(r.GetString(8)) ?? new()),
                    Hash = r.GetString(9), CreatedUtc = r.GetInt64(10), ModifiedUtc = r.GetInt64(11), Version = r.GetInt32(12),
                    Origin = r.GetString(13), Pinned = r.GetInt32(14) != 0, PinOrder = r.GetInt32(15), Deleted = r.GetInt32(16) != 0,
                    OcrDone = r.GetInt32(17) != 0, SizeBytes = r.GetInt64(18),
                };
                _cache[it.Id] = it;
            }
        }
    }

    /// <summary>All non-deleted items, metadata only (no image bytes).</summary>
    public List<ClipItem> Live()
    {
        lock (_gate) return _cache.Values.Where(i => !i.Deleted).ToList();
    }

    /// <summary>Everything including tombstones, for sync manifests.</summary>
    public List<ClipItem> All()
    {
        lock (_gate) return _cache.Values.ToList();
    }

    public ClipItem? Get(string id)
    {
        lock (_gate) return _cache.TryGetValue(id, out var it) ? it : null;
    }

    public ClipItem? FindLiveByHash(string hash)
    {
        lock (_gate) return _cache.Values.FirstOrDefault(i => !i.Deleted && i.Hash == hash);
    }

    public byte[]? GetImage(string id)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT image FROM items WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            var v = cmd.ExecuteScalar();
            return v is byte[] b ? b : null;
        }
    }

    /// <summary>Insert or fully replace. If item.ImagePng is null on an existing row, the stored image is preserved.</summary>
    public void Upsert(ClipItem it)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"INSERT INTO items(id,kind,text,html,rtf,image,thumb,imgw,imgh,files,hash,created,modified,version,origin,pinned,pinorder,deleted,ocrdone,size)
                VALUES($id,$kind,$text,$html,$rtf,$image,$thumb,$imgw,$imgh,$files,$hash,$created,$modified,$version,$origin,$pinned,$pinorder,$deleted,$ocrdone,$size)
                ON CONFLICT(id) DO UPDATE SET kind=$kind,text=$text,html=$html,rtf=$rtf,
                image=COALESCE($image,image),thumb=COALESCE($thumb,thumb),imgw=$imgw,imgh=$imgh,files=$files,hash=$hash,
                created=$created,modified=$modified,version=$version,origin=$origin,pinned=$pinned,pinorder=$pinorder,deleted=$deleted,ocrdone=$ocrdone,size=$size";
            var p = cmd.Parameters;
            p.AddWithValue("$id", it.Id); p.AddWithValue("$kind", (int)it.Kind); p.AddWithValue("$text", it.Text ?? "");
            p.AddWithValue("$html", (object?)it.Html ?? DBNull.Value); p.AddWithValue("$rtf", (object?)it.Rtf ?? DBNull.Value);
            p.AddWithValue("$image", (object?)it.ImagePng ?? DBNull.Value); p.AddWithValue("$thumb", (object?)it.Thumb ?? DBNull.Value);
            p.AddWithValue("$imgw", it.ImageWidth); p.AddWithValue("$imgh", it.ImageHeight);
            p.AddWithValue("$files", JsonSerializer.Serialize(it.Files.Select(f => new ClipFile { Name = f.Name, LocalPath = f.LocalPath, Size = f.Size }).ToList()));
            p.AddWithValue("$hash", it.Hash); p.AddWithValue("$created", it.CreatedUtc); p.AddWithValue("$modified", it.ModifiedUtc);
            p.AddWithValue("$version", it.Version); p.AddWithValue("$origin", it.Origin); p.AddWithValue("$pinned", it.Pinned ? 1 : 0);
            p.AddWithValue("$pinorder", it.PinOrder); p.AddWithValue("$deleted", it.Deleted ? 1 : 0); p.AddWithValue("$ocrdone", it.OcrDone ? 1 : 0);
            p.AddWithValue("$size", it.SizeBytes);
            cmd.ExecuteNonQuery();

            // Cache holds metadata only; drop heavy payloads.
            var cached = it.CloneMeta();
            cached.Html = it.Html; cached.Rtf = it.Rtf;
            cached.Thumb = it.Thumb ?? (_cache.TryGetValue(it.Id, out var old) ? old.Thumb : null);
            cached.Files = it.Files.Select(f => new ClipFile { Name = f.Name, LocalPath = f.LocalPath, Size = f.Size }).ToList();
            _cache[it.Id] = cached;
        }
    }

    public void PurgeOldTombstones(int days = 30)
    {
        lock (_gate)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM items WHERE deleted=1 AND modified<$c";
            cmd.Parameters.AddWithValue("$c", cutoff);
            cmd.ExecuteNonQuery();
            foreach (var k in _cache.Where(kv => kv.Value.Deleted && kv.Value.ModifiedUtc < cutoff).Select(kv => kv.Key).ToList())
                _cache.Remove(k);
        }
    }

    public void Dispose() => _db.Dispose();
}

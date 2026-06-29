using Microsoft.Data.Sqlite;

namespace Click.Services.Vector;

public sealed class SqliteVectorStore : IVectorStore, IDisposable
{
    private readonly string _dbPath;
    private SqliteConnection? _connection;

    public SqliteVectorStore(string workspacePath)
    {
        var clickDir = Path.Combine(workspacePath, ".click");
        Directory.CreateDirectory(clickDir);
        _dbPath = Path.Combine(clickDir, "index.db");
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        await _connection.OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync("PRAGMA journal_mode=WAL;", cancellationToken);

        await ExecuteNonQueryAsync(@"
            CREATE TABLE IF NOT EXISTS code_chunks (
                chunk_id TEXT PRIMARY KEY,
                file_path TEXT NOT NULL,
                language TEXT NOT NULL,
                symbol_name TEXT,
                symbol_type TEXT,
                parent_scope TEXT,
                start_line INTEGER,
                end_line INTEGER,
                content TEXT NOT NULL,
                file_hash TEXT NOT NULL,
                embedding BLOB
            );
            CREATE INDEX IF NOT EXISTS idx_chunks_file ON code_chunks(file_path);
            CREATE INDEX IF NOT EXISTS idx_chunks_lang ON code_chunks(language);
        ", cancellationToken);

        await ExecuteNonQueryAsync(@"
            CREATE TABLE IF NOT EXISTS file_index (
                file_path TEXT PRIMARY KEY,
                file_hash TEXT NOT NULL,
                last_modified INTEGER NOT NULL,
                indexed_at INTEGER NOT NULL,
                chunk_count INTEGER NOT NULL DEFAULT 0
            );
        ", cancellationToken);

        await ExecuteNonQueryAsync(@"
            CREATE TABLE IF NOT EXISTS index_meta (
                key TEXT PRIMARY KEY,
                value TEXT
            );
        ", cancellationToken);
    }

    public async Task InsertChunksAsync(IEnumerable<CodeChunk> chunks, CancellationToken cancellationToken = default)
    {
        if (_connection == null) throw new InvalidOperationException("Store not initialized");

        using var transaction = _connection.BeginTransaction();
        try
        {
            foreach (var chunk in chunks)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO code_chunks
                    (chunk_id, file_path, language, symbol_name, symbol_type, parent_scope, start_line, end_line, content, file_hash, embedding)
                    VALUES ($id, $path, $lang, $sym, $stype, $parent, $start, $end, $content, $hash, $emb)";
                cmd.Parameters.AddWithValue("$id", chunk.Id);
                cmd.Parameters.AddWithValue("$path", chunk.FilePath);
                cmd.Parameters.AddWithValue("$lang", chunk.Language);
                cmd.Parameters.AddWithValue("$sym", (object?)chunk.SymbolName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$stype", (object?)chunk.SymbolType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$parent", (object?)chunk.ParentScope ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$start", chunk.StartLine);
                cmd.Parameters.AddWithValue("$end", chunk.EndLine);
                cmd.Parameters.AddWithValue("$content", chunk.Content);
                cmd.Parameters.AddWithValue("$hash", chunk.FileHash);
                cmd.Parameters.AddWithValue("$emb", FloatsToBlob(chunk.Embedding));
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task DeleteFileChunksAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (_connection == null) throw new InvalidOperationException("Store not initialized");
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM code_chunks WHERE file_path = $path";
        cmd.Parameters.AddWithValue("$path", filePath);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CodeChunk>> SearchAsync(float[] queryEmbedding, int limit, string? languageFilter = null, string? globFilter = null, CancellationToken cancellationToken = default)
    {
        if (_connection == null) throw new InvalidOperationException("Store not initialized");

        var sql = "SELECT chunk_id, file_path, language, symbol_name, symbol_type, parent_scope, start_line, end_line, content, file_hash, embedding FROM code_chunks";
        var conditions = new List<string>();
        if (!string.IsNullOrEmpty(languageFilter))
            conditions.Add("language = $lang");
        if (!string.IsNullOrEmpty(globFilter))
            conditions.Add("file_path GLOB $glob");

        if (conditions.Count > 0)
            sql += " WHERE " + string.Join(" AND ", conditions);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        if (!string.IsNullOrEmpty(languageFilter))
            cmd.Parameters.AddWithValue("$lang", languageFilter);
        if (!string.IsNullOrEmpty(globFilter))
            cmd.Parameters.AddWithValue("$glob", globFilter);

        var results = new List<(CodeChunk Chunk, float Score)>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var emb = BlobToFloats(reader.GetStream(10));
            if (emb.Length != queryEmbedding.Length) continue;

            var score = CosineSimilarity(queryEmbedding, emb);
            var chunk = new CodeChunk(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt32(6), reader.GetInt32(7),
                reader.GetString(8), reader.GetString(9), emb);
            results.Add((chunk, score));
        }

        return results.OrderByDescending(r => r.Score).Take(limit).Select(r => r.Chunk).ToList();
    }

    public async Task<int> GetChunkCountAsync(CancellationToken cancellationToken = default)
    {
        if (_connection == null) return 0;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM code_chunks";
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is long l ? (int)l : 0;
    }

    public async Task<int> GetFileCountAsync(CancellationToken cancellationToken = default)
    {
        if (_connection == null) return 0;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT file_path) FROM code_chunks";
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is long l ? (int)l : 0;
    }

    public async Task<IndexStats?> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        if (_connection == null) return null;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT 
                (SELECT COUNT(*) FROM code_chunks),
                (SELECT COUNT(DISTINCT file_path) FROM code_chunks),
                (SELECT COUNT(DISTINCT language) FROM code_chunks),
                (SELECT value FROM index_meta WHERE key = 'model'),
                (SELECT value FROM index_meta WHERE key = 'indexed_at')
        ";
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new IndexStats(
            Chunks: reader.IsDBNull(0) ? 0 : (int)reader.GetInt64(0),
            Files: reader.IsDBNull(1) ? 0 : (int)reader.GetInt64(1),
            Languages: reader.IsDBNull(2) ? 0 : (int)reader.GetInt64(2),
            Model: reader.IsDBNull(3) ? null : reader.GetString(3),
            IndexedAt: reader.IsDBNull(4) ? null : reader.GetString(4)
        );
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        if (_connection == null) return;
        await ExecuteNonQueryAsync("DELETE FROM code_chunks; DELETE FROM file_index; DELETE FROM index_meta;", cancellationToken);
    }

    public async Task UpsertFileIndexAsync(string filePath, string fileHash, long lastModified, int chunkCount, CancellationToken cancellationToken = default)
    {
        if (_connection == null) throw new InvalidOperationException("Store not initialized");
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO file_index (file_path, file_hash, last_modified, indexed_at, chunk_count)
            VALUES ($path, $hash, $mtime, $now, $count)";
        cmd.Parameters.AddWithValue("$path", filePath);
        cmd.Parameters.AddWithValue("$hash", fileHash);
        cmd.Parameters.AddWithValue("$mtime", lastModified);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$count", chunkCount);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string?> GetFileHashAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (_connection == null) return null;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT file_hash FROM file_index WHERE file_path = $path";
        cmd.Parameters.AddWithValue("$path", filePath);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result?.ToString();
    }

    public async Task SaveMetaAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        if (_connection == null) return;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO index_meta (key, value) VALUES ($k, $v)";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ExecuteNonQueryAsync(string sql, CancellationToken cancellationToken)
    {
        if (_connection == null) return;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static byte[] FloatsToBlob(float[]? values)
    {
        if (values == null || values.Length == 0)
            return Array.Empty<byte>();
        var bytes = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] BlobToFloats(System.IO.Stream stream)
    {
        using var ms = new System.IO.MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();
        var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        var denom = MathF.Sqrt(na) * MathF.Sqrt(nb);
        return denom < 1e-8f ? 0f : dot / denom;
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }
}

public record IndexStats(int Chunks, int Files, int Languages, string? Model, string? IndexedAt);

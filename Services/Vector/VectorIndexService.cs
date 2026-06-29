using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Click.Services.Vector;

public sealed class VectorIndexService
{
    private readonly string _workspacePath;
    private readonly IVectorStore _store;
    private readonly IEmbeddingService _embeddingService;
    private readonly ChunkerFactory _chunkerFactory;
    private readonly ILogger<VectorIndexService> _logger;

    private bool _isInitialized;
    private readonly SemaphoreSlim _indexLock = new(1, 1);

    public VectorIndexService(
        string workspacePath,
        IVectorStore store,
        IEmbeddingService embeddingService,
        ChunkerFactory chunkerFactory,
        ILogger<VectorIndexService> logger)
    {
        _workspacePath = workspacePath;
        _store = store;
        _embeddingService = embeddingService;
        _chunkerFactory = chunkerFactory;
        _logger = logger;
    }

    public bool IsAvailable => _embeddingService.IsAvailable;

    public async Task EnsureIndexedAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!_embeddingService.IsAvailable)
        {
            _logger.LogInformation("Embedding service not configured — skipping vector index");
            return;
        }

        if (!_isInitialized)
        {
            await _store.InitializeAsync(cancellationToken);
            _isInitialized = true;
        }

        var stats = await _store.GetStatsAsync(cancellationToken);
        if (stats is { Chunks: > 0 })
        {
            _logger.LogInformation("Vector index already exists: {Chunks} chunks, {Files} files", stats.Chunks, stats.Files);
            return;
        }

        await ReindexAsync(progress, cancellationToken);
    }

    public async Task ReindexAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!_embeddingService.IsAvailable)
        {
            _logger.LogInformation("Embedding service not configured — skipping reindex");
            progress?.Report("Embedding service not configured — skipping");
            return;
        }

        await _indexLock.WaitAsync(cancellationToken);
        try
        {
            if (!_isInitialized)
            {
                await _store.InitializeAsync(cancellationToken);
                _isInitialized = true;
            }

            progress?.Report("Сканирую файлы...");
            var files = GetIndexableFiles();
            progress?.Report($"Найдено {files.Count} файлов для индексации");

            await _store.ResetAsync(cancellationToken);

            int processedFiles = 0;
            int totalChunks = 0;
            var allChunks = new List<CodeChunk>();
            var fileIndexEntries = new List<(string Path, string Hash, long LastWriteTicks, int ChunkCount)>();

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var content = File.ReadAllText(file);
                    var hash = ComputeHash(content);
                    var relativePath = Path.GetRelativePath(_workspacePath, file).Replace('\\', '/');
                    var fi = new FileInfo(file);
                    var chunker = _chunkerFactory.GetChunker(file);
                    var chunks = chunker.ChunkFile(relativePath, content, hash);

                    if (chunks.Count > 0)
                    {
                        allChunks.AddRange(chunks);
                        totalChunks += chunks.Count;

                        if (allChunks.Count >= 50)
                        {
                            await EmbedAndInsertAsync(allChunks, cancellationToken);
                            allChunks.Clear();
                        }
                    }

                    fileIndexEntries.Add((relativePath, hash, fi.LastWriteTimeUtc.Ticks, chunks.Count));

                    processedFiles++;
                    if (processedFiles % 50 == 0)
                        progress?.Report($"Индексировано {processedFiles}/{files.Count} файлов, {totalChunks} чанков...");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to index file: {File}", file);
                }
            }

            if (allChunks.Count > 0)
                await EmbedAndInsertAsync(allChunks, cancellationToken);

            foreach (var (relPath, hash, lastWriteTicks, chunkCount) in fileIndexEntries)
            {
                try
                {
                    await _store.UpsertFileIndexAsync(relPath, hash, lastWriteTicks, chunkCount, cancellationToken);
                }
                catch { }
            }

            await _store.SaveMetaAsync("model", _embeddingService.GetType().Name, cancellationToken);
            await _store.SaveMetaAsync("indexed_at", DateTimeOffset.UtcNow.ToString("O"), cancellationToken);

            progress?.Report($"Индексация завершена: {processedFiles} файлов, {totalChunks} чанков");
            _logger.LogInformation("Reindex complete: {Files} files, {Chunks} chunks", processedFiles, totalChunks);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    public async Task UpdateFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!_embeddingService.IsAvailable)
            return;

        await _indexLock.WaitAsync(cancellationToken);
        try
        {
            if (!_isInitialized)
            {
                await _store.InitializeAsync(cancellationToken);
                _isInitialized = true;
            }

            var fullPath = Path.Combine(_workspacePath, filePath);
            if (!File.Exists(fullPath))
            {
                await _store.DeleteFileChunksAsync(filePath, cancellationToken);
                return;
            }

            var content = File.ReadAllText(fullPath);
            var hash = ComputeHash(content);
            var storedHash = await _store.GetFileHashAsync(filePath, cancellationToken);
            if (storedHash == hash)
                return;

            await _store.DeleteFileChunksAsync(filePath, cancellationToken);

            var chunker = _chunkerFactory.GetChunker(fullPath);
            var chunks = chunker.ChunkFile(filePath, content, hash).ToList();
            if (chunks.Count > 0)
                await EmbedAndInsertAsync(chunks, cancellationToken);

            var fi = new FileInfo(fullPath);
            await _store.UpsertFileIndexAsync(filePath, hash, fi.LastWriteTimeUtc.Ticks, chunks.Count, cancellationToken);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    public async Task<IReadOnlyList<SemanticSearchResult>> SearchAsync(string query, int limit, string? language = null, string? glob = null, CancellationToken cancellationToken = default)
    {
        if (!_embeddingService.IsAvailable)
            return Array.Empty<SemanticSearchResult>();

        if (!_isInitialized)
        {
            await _store.InitializeAsync(cancellationToken);
            _isInitialized = true;
        }

        var queryEmbeddings = await _embeddingService.GetEmbeddingsAsync(new[] { query }, cancellationToken);
        if (queryEmbeddings.Length == 0 || queryEmbeddings[0].Length == 0)
            return Array.Empty<SemanticSearchResult>();

        var chunks = await _store.SearchAsync(queryEmbeddings[0], limit, language, glob, cancellationToken);
        return chunks.Select(c => new SemanticSearchResult(
            c.FilePath, c.StartLine, c.EndLine, c.SymbolName, c.SymbolType, c.ParentScope, c.Language, c.Content, 0f)).ToList();
    }

    public Task<IndexStats?> GetStatsAsync(CancellationToken cancellationToken = default)
        => _store.GetStatsAsync(cancellationToken);

    private async Task EmbedAndInsertAsync(List<CodeChunk> chunks, CancellationToken cancellationToken)
    {
        if (chunks.Count == 0) return;

        var texts = chunks.Select(c => FormatChunkText(c)).ToList();
        var embeddings = await _embeddingService.GetEmbeddingsAsync(texts, cancellationToken);

        int skipped = 0;
        var enriched = new List<CodeChunk>();
        for (int i = 0; i < chunks.Count; i++)
        {
            if (i < embeddings.Length && embeddings[i] is { Length: > 0 })
                enriched.Add(chunks[i] with { Embedding = embeddings[i] });
            else
                skipped++;
        }

        if (skipped > 0)
            _logger.LogWarning("Skipped {Skipped}/{Total} chunks due to missing embedding — API returned fewer vectors than text inputs. Try reducing batch size or check the embedding model.", skipped, chunks.Count);

        if (enriched.Count > 0)
            await _store.InsertChunksAsync(enriched, cancellationToken);
    }

    private static string FormatChunkText(CodeChunk chunk)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(chunk.SymbolType))
            sb.Append($"[{chunk.SymbolType}] ");
        if (!string.IsNullOrEmpty(chunk.SymbolName))
            sb.Append($"{chunk.SymbolName}: ");
        sb.Append(chunk.Content);
        return sb.ToString();
    }

    private List<string> GetIndexableFiles()
    {
        var result = new List<string>();
        try
        {
            var gitignorePatterns = LoadGitignore();
            ScanDirectory(_workspacePath, result, gitignorePatterns);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scan workspace");
        }
        return result;
    }

    private void ScanDirectory(string dir, List<string> result, List<string> gitignorePatterns)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var relative = Path.GetRelativePath(_workspacePath, file).Replace('\\', '/');
            if (IsIgnored(relative, gitignorePatterns)) continue;
            if (!_chunkerFactory.IsIndexable(file)) continue;
            result.Add(file);
        }

        foreach (var subdir in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(subdir);
            if (name.StartsWith('.')) continue;
            var relative = Path.GetRelativePath(_workspacePath, subdir).Replace('\\', '/');
            if (IsIgnored(relative + "/", gitignorePatterns)) continue;
            ScanDirectory(subdir, result, gitignorePatterns);
        }
    }

    private List<string> LoadGitignore()
    {
        var patterns = new List<string> { ".git", "node_modules", "bin", "obj", "dist", "build", "target", ".click" };
        var gitignorePath = Path.Combine(_workspacePath, ".gitignore");
        if (File.Exists(gitignorePath))
        {
            foreach (var line in File.ReadAllLines(gitignorePath))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
                patterns.Add(trimmed.TrimStart('/', '\\'));
            }
        }
        return patterns;
    }

    private static bool IsIgnored(string relativePath, List<string> patterns)
    {
        var segments = relativePath.Split('/');
        foreach (var pattern in patterns)
        {
            var p = pattern.TrimEnd('/');
            // Exact segment match or starts-with directory match
            if (segments.Contains(p, StringComparer.OrdinalIgnoreCase))
                return true;
            if (relativePath.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase))
                return true;
            // Simple glob: *.ext
            if (p.StartsWith("*.") && relativePath.EndsWith(p[1..], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string ComputeHash(string content)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }
}

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
            // Check if model changed — if so, reindex
            if (!string.IsNullOrEmpty(stats.Model) && stats.Model != _embeddingService.ModelName)
            {
                _logger.LogInformation("Model changed from {OldModel} to {NewModel} — reindexing", stats.Model, _embeddingService.ModelName);
                progress?.Report($"Модель эмбеддинга изменилась ({stats.Model} → {_embeddingService.ModelName}), переиндексация...");
                await ReindexAsync(progress, cancellationToken);
                return;
            }

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

            var chunker = _chunkerFactory.GetChunker(fullPath);
            var chunks = chunker.ChunkFile(filePath, content, hash).ToList();

            // Insert new chunks first, then delete old ones to avoid data loss on failure
            Exception? insertError = null;
            if (chunks.Count > 0)
            {
                try
                {
                    await EmbedAndInsertAsync(chunks, cancellationToken);
                }
                catch (Exception ex)
                {
                    insertError = ex;
                }
            }

            // Delete old chunks regardless of insert success (if insert failed, old chunks remain — safe)
            await _store.DeleteFileChunksAsync(filePath, cancellationToken);

            if (insertError is not null)
                throw insertError;

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

        // Get more candidates than needed for re-ranking
        var candidateLimit = Math.Max(limit * 3, 20);
        var chunks = await _store.SearchAsync(queryEmbeddings[0], candidateLimit, language, glob, cancellationToken);

        // Hybrid re-ranking: combine cosine similarity with keyword relevance
        var queryTerms = Tokenize(query);
        var results = new List<(SemanticSearchResult Result, float CombinedScore)>();

        foreach (var chunk in chunks)
        {
            var keywordScore = queryTerms.Length > 0 ? ComputeKeywordScore(chunk.Content, queryTerms) : 0f;
            var combinedScore = 0.6f * (chunk.CosineScore > 0 ? chunk.CosineScore : 0.7f) + 0.4f * keywordScore;
            results.Add((new SemanticSearchResult(
                chunk.FilePath, chunk.StartLine, chunk.EndLine,
                chunk.SymbolName, chunk.SymbolType, chunk.ParentScope,
                chunk.Language, chunk.Content, combinedScore), combinedScore));
        }

        return results
            .OrderByDescending(r => r.CombinedScore)
            .Take(limit)
            .Select(r => r.Result)
            .ToList();
    }

    private static string[] Tokenize(string text)
    {
        return text.ToLowerInvariant()
            .Split([' ', '\t', '\n', '\r', '.', ',', ':', ';', '(', ')', '[', ']', '{', '}', '<', '>', '/', '\\', '_', '-', '+', '=', '*', '&', '|', '!', '?', '"', '\''])
            .Where(w => w.Length >= 2)
            .Distinct()
            .ToArray();
    }

    private static float ComputeKeywordScore(string content, string[] queryTerms)
    {
        var contentLower = content.ToLowerInvariant();
        int matches = 0;
        foreach (var term in queryTerms)
        {
            if (contentLower.Contains(term))
                matches++;
        }
        return queryTerms.Length > 0 ? (float)matches / queryTerms.Length : 0f;
    }

    public Task<IndexStats?> GetStatsAsync(CancellationToken cancellationToken = default)
        => _store.GetStatsAsync(cancellationToken);

    private async Task EmbedAndInsertAsync(List<CodeChunk> chunks, CancellationToken cancellationToken)
    {
        if (chunks.Count == 0) return;

        const int maxChunkChars = 7000;
        int truncatedCount = 0;

        var texts = chunks.Select(c =>
        {
            var text = FormatChunkText(c);
            if (text.Length > maxChunkChars)
            {
                text = text[..maxChunkChars];
                truncatedCount++;
            }
            return text;
        }).ToList();

        if (truncatedCount > 0)
            _logger.LogWarning("Truncated {Count}/{Total} chunk texts to {Max} chars to stay under embedding API token limit", truncatedCount, chunks.Count, maxChunkChars);

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
            _logger.LogWarning("Skipped {Skipped}/{Total} chunks due to missing embedding", skipped, chunks.Count);

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
        var builtIn = new List<string> { ".git", "node_modules", "bin", "obj", "dist", "build", "target", ".click" };
        var gitignorePath = Path.Combine(_workspacePath, ".gitignore");
        if (!File.Exists(gitignorePath))
            return builtIn;

        var result = new List<string>();
        foreach (var line in File.ReadAllLines(gitignorePath))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
            // Normalize: strip leading / or \ and trailing spaces
            var normalized = trimmed.TrimStart('/', '\\').TrimEnd();
            result.Add(normalized);
        }
        // Built-in patterns always apply
        result.AddRange(builtIn);
        return result;
    }

    private static bool IsIgnored(string relativePath, List<string> patterns)
    {
        // Apply negation (!) patterns first — track if file is explicitly un-ignored
        bool explicitlyAllowed = false;

        foreach (var rawPattern in patterns)
        {
            bool negate = rawPattern.StartsWith('!');
            string p = negate ? rawPattern[1..].TrimStart('/', '\\') : rawPattern;
            bool dirOnly = p.EndsWith('/');

            string pattern = dirOnly ? p.TrimEnd('/') : p;
            bool match = MatchGlob(relativePath, pattern);

            // dirOnly: only match if relativePath represents a directory (ends with /)
            if (dirOnly && !relativePath.EndsWith('/'))
                match = false;

            if (negate)
            {
                if (match) explicitlyAllowed = true;
            }
            else
            {
                if (match) return !explicitlyAllowed;
            }
        }

        return false;
    }

    private static bool MatchGlob(string relativePath, string pattern)
    {
        var pathSep = '/';

        // Exact match (segment or full path)
        if (string.Equals(relativePath, pattern, StringComparison.OrdinalIgnoreCase))
            return true;

        // Starts-with directory match: e.g. pattern "build" matches "build/file.cs"
        if (relativePath.StartsWith(pattern + "/", StringComparison.OrdinalIgnoreCase))
            return true;

        // Pattern like "*.ext"
        if (pattern.StartsWith("*.") && !pattern.Contains('/') && !pattern[2..].Contains('*'))
        {
            if (relativePath.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Pattern with ** (match any number of directories)
        if (pattern.Contains("**"))
        {
            var parts = pattern.Split(new[] { "**" }, StringSplitOptions.None);
            if (parts.Length == 2)
            {
                var prefix = parts[0].TrimEnd('/');
                var suffix = parts[1].TrimStart('/');
                // ** at start: match suffix anywhere
                if (string.IsNullOrEmpty(prefix))
                {
                    if (string.IsNullOrEmpty(suffix))
                        return true;
                    if (relativePath.EndsWith("/" + suffix, StringComparison.OrdinalIgnoreCase))
                        return true;
                    if (relativePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                else
                {
                    // prefix/**/suffix
                    if (relativePath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase) &&
                        relativePath.EndsWith("/" + suffix, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            // pattern/** or pattern/**/
            if (parts.Length == 2 && string.IsNullOrEmpty(parts[1]))
            {
                var prefix = parts[0].TrimEnd('/');
                if (relativePath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(relativePath, prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        // Simple wildcard * (matches within a single path segment)
        if (pattern.Contains('*') && !pattern.Contains("**"))
        {
            if (SimpleWildcardMatch(relativePath, pattern, pathSep))
                return true;
        }

        // Check each path segment for pattern match
        var segments = relativePath.Split(pathSep);
        foreach (var seg in segments)
        {
            if (string.Equals(seg, pattern, StringComparison.OrdinalIgnoreCase))
                return true;
            if (pattern.StartsWith("*.") && seg.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase))
                return true;
            if (SimpleWildcardMatch(seg, pattern, pathSep))
                return true;
        }

        return false;
    }

    private static bool SimpleWildcardMatch(string text, string pattern, char sep)
    {
        int ti = 0, pi = 0;
        while (pi < pattern.Length && ti < text.Length)
        {
            if (pattern[pi] == '*')
            {
                // Skip consecutive '*' wildcards
                while (pi < pattern.Length && pattern[pi] == '*') pi++;
                if (pi >= pattern.Length) return true; // trailing * matches everything
                // Find remaining pattern in text
                char nextChar = pattern[pi];
                while (ti < text.Length && text[ti] != nextChar) ti++;
                if (ti >= text.Length) return false;
            }
            else if (pattern[pi] == '?' || pattern[pi] == text[ti])
            {
                pi++;
                ti++;
            }
            else
            {
                return false;
            }
        }
        // Skip remaining trailing '*'
        while (pi < pattern.Length && pattern[pi] == '*') pi++;
        return pi >= pattern.Length && ti >= text.Length;
    }

    private static string ComputeHash(string content)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }
}

namespace Click.Services.Vector;

public sealed class FallbackChunker : ICodeChunker
{
    private const int WindowSize = 150;
    private const int Overlap = 50;

    public string[] SupportedLanguages => new[] { "markdown", "json", "yaml", "xml", "text", "unknown" };

    public IReadOnlyList<CodeChunk> ChunkFile(string filePath, string content, string fileHash)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var lang = DetectLanguage(ext);
        var lines = content.Split('\n');
        var chunks = new List<CodeChunk>();

        // For small files — whole file as one chunk
        if (lines.Length <= WindowSize)
        {
            var id = ChunkUtils.ComputeChunkId(filePath, content, 1, lines.Length);
            chunks.Add(new CodeChunk(id, filePath, lang, null, "file", null, 1, lines.Length, content.Trim(), fileHash, Array.Empty<float>()));
            return chunks;
        }

        // For markdown — split by headers
        if (lang == "markdown")
        {
            chunks.AddRange(ChunkByHeaders(filePath, lines, lang, fileHash));
            return chunks;
        }

        // Sliding window for everything else
        int step = WindowSize - Overlap;
        for (int start = 0; start < lines.Length; start += step)
        {
            int end = Math.Min(start + WindowSize, lines.Length);
            var windowLines = lines[start..end];
            var windowContent = string.Join("\n", windowLines).Trim();
            if (string.IsNullOrWhiteSpace(windowContent)) continue;

            var id = ChunkUtils.ComputeChunkId(filePath, windowContent, start + 1, end);
            chunks.Add(new CodeChunk(id, filePath, lang, null, "section", null, start + 1, end, windowContent, fileHash, Array.Empty<float>()));
        }

        return chunks;
    }

    private static List<CodeChunk> ChunkByHeaders(string filePath, string[] lines, string lang, string fileHash)
    {
        var chunks = new List<CodeChunk>();
        int? startLine = null;
        var blockLines = new List<string>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("# ") || trimmed.StartsWith("## "))
            {
                if (startLine != null && blockLines.Count > 0)
                {
                    var content = string.Join("\n", blockLines).Trim();
                    var id = ChunkUtils.ComputeChunkId(filePath, content, startLine.Value, i);
                    var header = blockLines[0].Trim().TrimStart('#', ' ');
                    chunks.Add(new CodeChunk(id, filePath, lang, header, "section", null, startLine.Value, i, content, fileHash, Array.Empty<float>()));
                }
                startLine = i + 1;
                blockLines.Clear();
            }

            blockLines.Add(line);
        }

        if (startLine != null && blockLines.Count > 0)
        {
            var content = string.Join("\n", blockLines).Trim();
            var id = ChunkUtils.ComputeChunkId(filePath, content, startLine.Value, lines.Length);
            var header = blockLines[0].Trim().TrimStart('#', ' ');
            chunks.Add(new CodeChunk(id, filePath, lang, header, "section", null, startLine.Value, lines.Length, content, fileHash, Array.Empty<float>()));
        }

        return chunks;
    }

    private static string DetectLanguage(string ext)
    {
        return ext switch
        {
            ".md" or ".markdown" => "markdown",
            ".json" => "json",
            ".yaml" or ".yml" => "yaml",
            ".xml" => "xml",
            ".txt" or ".rst" => "text",
            _ => "unknown"
        };
    }


}

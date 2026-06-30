using System.Text.RegularExpressions;

namespace Click.Services.Vector;

public sealed class RegexBasedChunker : ICodeChunker
{
    public string[] SupportedLanguages => new[]
    {
        "c_sharp", "javascript", "typescript", "python", "go",
        "rust", "java", "c", "cpp", "php", "ruby"
    };

    private static readonly HashSet<string> CFamilyLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "c_sharp", "javascript", "typescript", "java", "c", "cpp", "go", "rust", "php"
    };

    private static readonly Regex BlockStartRegex = new(
        @"^\s*(?:public\s+|private\s+|protected\s+|internal\s+|static\s+|async\s+|override\s+|virtual\s+|abstract\s+|readonly\s+|const\s+|export\s+|default\s+)*\s*(?:func(?:tion)?\s+|def\s+|fn\s+|class\s+|interface\s+|struct\s+|enum\s+|trait\s+|impl\s+|type\s+|module\s+|namespace\s+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public IReadOnlyList<CodeChunk> ChunkFile(string filePath, string content, string fileHash)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var lang = DetectLanguage(ext);
        var lines = content.Split('\n');
        var chunks = new List<CodeChunk>();

        if (lang == "python")
        {
            chunks.AddRange(ChunkPython(filePath, lines, lang, fileHash));
        }
        else if (lang == "ruby")
        {
            chunks.AddRange(ChunkRuby(filePath, lines, lang, fileHash));
        }
        else if (CFamilyLanguages.Contains(lang))
        {
            chunks.AddRange(ChunkCFamily(filePath, lines, lang, fileHash));
        }

        return chunks;
    }

    private static string DetectLanguage(string ext)
    {
        return ext switch
        {
            ".cs" => "c_sharp",
            ".js" or ".mjs" or ".cjs" => "javascript",
            ".ts" or ".tsx" => "typescript",
            ".py" => "python",
            ".go" => "go",
            ".rs" => "rust",
            ".java" => "java",
            ".c" or ".h" => "c",
            ".cpp" or ".hpp" or ".cc" or ".cxx" => "cpp",
            ".php" => "php",
            ".rb" => "ruby",
            _ => "unknown"
        };
    }

    private static List<CodeChunk> ChunkCFamily(string filePath, string[] lines, string lang, string fileHash)
    {
        var chunks = new List<CodeChunk>();
        int? startLine = null;
        int braceDepth = 0;
        string? symbolName = null;
        string? symbolType = null;
        var blockLines = new List<string>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // Skip empty lines and comments at top
            if (startLine == null && (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("//") || trimmed.StartsWith("/*") || trimmed.StartsWith("*")))
                continue;

            if (startLine == null && BlockStartRegex.IsMatch(trimmed))
            {
                startLine = i + 1;
                (symbolType, symbolName) = ExtractSymbolInfo(trimmed);
                blockLines.Clear();
                blockLines.Add(line);
                braceDepth = CountBraces(line);
                // Handle single-line blocks: void Foo() { }
                if (braceDepth == 0 && trimmed.Contains('{') && trimmed.Contains('}'))
                {
                    AddChunk(chunks, filePath, lang, symbolName, symbolType, startLine.Value, i + 1, blockLines, fileHash);
                    startLine = null;
                }
                continue;
            }

            if (startLine != null)
            {
                blockLines.Add(line);
                braceDepth += CountBraces(line);
                if (braceDepth <= 0 && trimmed.Contains('}'))
                {
                    AddChunk(chunks, filePath, lang, symbolName, symbolType, startLine.Value, i + 1, blockLines, fileHash);
                    startLine = null;
                    braceDepth = 0;
                }
            }
        }

        // Remainder — treat as top-level if any
        if (startLine != null && blockLines.Count > 0)
        {
            AddChunk(chunks, filePath, lang, symbolName, symbolType, startLine.Value, lines.Length, blockLines, fileHash);
        }

        return chunks;
    }

    private static List<CodeChunk> ChunkPython(string filePath, string[] lines, string lang, string fileHash)
    {
        var chunks = new List<CodeChunk>();
        int? startLine = null;
        string? symbolName = null;
        string? symbolType = null;
        int baseIndent = -1;
        var blockLines = new List<string>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (startLine == null && (trimmed.StartsWith("def ") || trimmed.StartsWith("class ")))
            {
                startLine = i + 1;
                symbolType = trimmed.StartsWith("class ") ? "class" : "function";
                symbolName = ExtractNameAfterKeyword(trimmed, symbolType == "class" ? "class" : "def");
                blockLines.Clear();
                blockLines.Add(line);
                baseIndent = line.TakeWhile(char.IsWhiteSpace).Count();
                continue;
            }

            if (startLine != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    blockLines.Add(line);
                    continue;
                }

                int indent = line.TakeWhile(char.IsWhiteSpace).Count();
                if (indent <= baseIndent && !string.IsNullOrWhiteSpace(line))
                {
                    // Block ended
                    AddChunk(chunks, filePath, lang, symbolName, symbolType, startLine.Value, i, blockLines, fileHash);
                    // Check if new block starts
                    if (trimmed.StartsWith("def ") || trimmed.StartsWith("class "))
                    {
                        startLine = i + 1;
                        symbolType = trimmed.StartsWith("class ") ? "class" : "function";
                        symbolName = ExtractNameAfterKeyword(trimmed, symbolType == "class" ? "class" : "def");
                        blockLines.Clear();
                        blockLines.Add(line);
                        baseIndent = indent;
                    }
                    else
                    {
                        startLine = null;
                    }
                }
                else
                {
                    blockLines.Add(line);
                }
            }
        }

        if (startLine != null && blockLines.Count > 0)
        {
            AddChunk(chunks, filePath, lang, symbolName, symbolType, startLine.Value, lines.Length, blockLines, fileHash);
        }

        return chunks;
    }

    private static List<CodeChunk> ChunkRuby(string filePath, string[] lines, string lang, string fileHash)
    {
        var chunks = new List<CodeChunk>();
        int? startLine = null;
        string? symbolName = null;
        string? symbolType = null;
        int endCount = 0;
        var blockLines = new List<string>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (startLine == null && (trimmed.StartsWith("def ") || trimmed.StartsWith("class ") || trimmed.StartsWith("module ")))
            {
                startLine = i + 1;
                symbolType = trimmed.StartsWith("class ") ? "class" : trimmed.StartsWith("module ") ? "module" : "method";
                symbolName = ExtractNameAfterKeyword(trimmed, symbolType == "class" ? "class" : symbolType == "module" ? "module" : "def");
                blockLines.Clear();
                blockLines.Add(line);
                endCount = CountKeyword(trimmed, "end") - CountKeyword(trimmed, "do");
                continue;
            }

            if (startLine != null)
            {
                blockLines.Add(line);
                endCount += CountKeyword(trimmed, "end") - CountKeyword(trimmed, "do");
                if (endCount <= 0 && !string.IsNullOrWhiteSpace(line))
                {
                    AddChunk(chunks, filePath, lang, symbolName, symbolType, startLine.Value, i + 1, blockLines, fileHash);
                    startLine = null;
                    endCount = 0;
                }
            }
        }

        if (startLine != null && blockLines.Count > 0)
        {
            AddChunk(chunks, filePath, lang, symbolName, symbolType, startLine.Value, lines.Length, blockLines, fileHash);
        }

        return chunks;
    }

    private static void AddChunk(List<CodeChunk> chunks, string filePath, string lang, string? symbolName, string? symbolType, int startLine, int endLine, List<string> blockLines, string fileHash)
    {
        var content = string.Join("\n", blockLines).Trim();
        if (string.IsNullOrWhiteSpace(content) || content.Length < 10)
            return;
        var id = ChunkUtils.ComputeChunkId(filePath, content, startLine, endLine);
        chunks.Add(new CodeChunk(id, filePath, lang, symbolName, symbolType, null, startLine, endLine, content, fileHash, Array.Empty<float>()));
    }

    private static (string? Type, string? Name) ExtractSymbolInfo(string line)
    {
        var trimmed = line.Trim();
        string? type = null;
        string? name = null;

        if (trimmed.Contains("class "))
        {
            type = "class";
            name = ExtractNameAfterKeyword(trimmed, "class");
        }
        else if (trimmed.Contains("interface "))
        {
            type = "interface";
            name = ExtractNameAfterKeyword(trimmed, "interface");
        }
        else if (trimmed.Contains("struct "))
        {
            type = "struct";
            name = ExtractNameAfterKeyword(trimmed, "struct");
        }
        else if (trimmed.Contains("enum "))
        {
            type = "enum";
            name = ExtractNameAfterKeyword(trimmed, "enum");
        }
        else if (trimmed.Contains("trait "))
        {
            type = "trait";
            name = ExtractNameAfterKeyword(trimmed, "trait");
        }
        else if (trimmed.Contains("impl "))
        {
            type = "impl";
            name = ExtractNameAfterKeyword(trimmed, "impl");
        }
        else if (trimmed.Contains("func") || trimmed.Contains("function ") || trimmed.Contains("fn ") || trimmed.Contains("def "))
        {
            type = "function";
            if (trimmed.Contains("func ")) name = ExtractNameAfterKeyword(trimmed, "func");
            else if (trimmed.Contains("function ")) name = ExtractNameAfterKeyword(trimmed, "function");
            else if (trimmed.Contains("fn ")) name = ExtractNameAfterKeyword(trimmed, "fn");
            else if (trimmed.Contains("def ")) name = ExtractNameAfterKeyword(trimmed, "def");
        }
        else if (trimmed.Contains('(') || trimmed.Contains("::"))
        {
            type = "method";
            // Try to extract name before (
            var beforeParen = trimmed.Split('(')[0].Trim();
            var parts = beforeParen.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
                name = parts[^1].Trim();
        }

        return (type, name);
    }

    private static string? ExtractNameAfterKeyword(string line, string keyword)
    {
        var idx = line.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var after = line[(idx + keyword.Length)..].TrimStart();
        var end = after.IndexOfAny(new[] { ' ', '(', '{', ':', '<', '\t', '\r', '\n' });
        return end > 0 ? after[..end].Trim() : after.Trim();
    }

    private static int CountBraces(string line)
    {
        // Strip single-line comments first
        var commentIdx = line.IndexOf("//", StringComparison.Ordinal);
        var code = commentIdx >= 0 ? line[..commentIdx] : line;

        int count = 0;
        bool inString = false;
        char stringChar = '\0';
        for (int i = 0; i < code.Length; i++)
        {
            var c = code[i];
            if (inString)
            {
                if (c == '\\' && i + 1 < code.Length) { i++; continue; }
                if (c == stringChar) inString = false;
                continue;
            }
            if (c == '"' || c == '\'')
            {
                inString = true;
                stringChar = c;
                continue;
            }
            if (c == '{') count++;
            else if (c == '}') count--;
        }
        return count;
    }

    private static int CountKeyword(string line, string keyword)
    {
        int count = 0;
        int idx = 0;
        while ((idx = line.IndexOf(keyword, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            idx += keyword.Length;
        }
        return count;
    }


}

using System.Security.Cryptography;
using System.Text;

namespace Click.Services.Vector;

public static class ChunkUtils
{
    public static string ComputeChunkId(string filePath, string content, int startLine = 0, int endLine = 0)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes($"{filePath}:{startLine}-{endLine}\n{content}"));
        return Convert.ToHexString(bytes);
    }
}

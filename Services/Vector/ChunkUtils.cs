using System.Security.Cryptography;
using System.Text;

namespace Click.Services.Vector;

public static class ChunkUtils
{
    public static string ComputeChunkId(string filePath, string content)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(filePath + "\n" + content));
        return Convert.ToHexString(bytes);
    }
}

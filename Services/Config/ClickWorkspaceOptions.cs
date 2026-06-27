namespace Click;

public class ClickWorkspaceOptions
{
    public const string SectionName = "Click";

    public string? BasePath { get; set; }

    public string GetResolvedBasePath()
    {
        var path = string.IsNullOrWhiteSpace(BasePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ClickProjects")
            : Path.GetFullPath(ExpandPath(BasePath));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ExpandPath(string path)
    {
        if (path.StartsWith("~/") || path.StartsWith("~\\"))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        if (path == "~")
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path;
    }
}

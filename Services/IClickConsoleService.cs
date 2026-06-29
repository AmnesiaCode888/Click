namespace Click.Services;

/// <summary>
/// Console UI service that orchestrates the interactive chat loop
/// (validation, workspace description, prompting, history management).
/// </summary>
public interface IClickConsoleService
{
    /// <summary>
    /// Validates configuration, builds the workspace description,
    /// and enters the interactive chat loop.
    /// </summary>
    Task RunAsync(CancellationToken cancellationToken = default);
}

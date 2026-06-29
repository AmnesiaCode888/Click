using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using AgentSharp;
using Microsoft.Extensions.Logging;

namespace Click.Agents.Common.Tools;

public class TerminalToolHandler : IToolHandler
{
    private readonly string _workspacePath;
    private readonly TerminalToolOptions _options;
    private readonly ILogger<TerminalToolHandler> _logger;

    public TerminalToolHandler(string workspacePath, TerminalToolOptions options, ILogger<TerminalToolHandler> logger)
    {
        _workspacePath = Path.GetFullPath(workspacePath);
        _options = options;
        _logger = logger;
    }

    public string Name => "terminal";

    public string Description => "Выполнение команд в терминале проекта (dotnet, npm, git, build, test и т.д.)";

    public Type ArgsType => typeof(TerminalArgs);

    public async Task<ToolResult> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        var args = System.Text.Json.JsonSerializer.Deserialize<TerminalArgs>(argumentsJson)
            ?? throw new ArgumentException("Не удалось десериализовать аргументы");

        var workingDirectory = args.WorkingDirectory != null
            ? Path.GetFullPath(args.WorkingDirectory, _workspacePath)
            : _workspacePath;

        // Проверяем, что рабочая директория находится в пределах workspace
        var normalizedWorkspace = _workspacePath.EndsWith(Path.DirectorySeparatorChar)
            ? _workspacePath
            : _workspacePath + Path.DirectorySeparatorChar;

        if (!workingDirectory.StartsWith(normalizedWorkspace, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(workingDirectory, _workspacePath, StringComparison.OrdinalIgnoreCase))
        {
            return ToolResult.FromString(
                $"Ошибка: рабочая директория '{workingDirectory}' выходит за пределы workspace '{_workspacePath}'");
        }

        var command = args.Command?.Trim();
        if (string.IsNullOrEmpty(command))
            return ToolResult.FromString("Команда не указана");

        var timeoutSeconds = args.TimeoutSeconds ?? _options.DefaultTimeoutSeconds;
        timeoutSeconds = Math.Clamp(timeoutSeconds, _options.MinTimeoutSeconds, _options.MaxTimeoutSeconds);

        var shellHint = BuildShellHint(command);

        try
        {
            var (isWindows, shell, shellArg) = DetectShell();
            var psi = new ProcessStartInfo
            {
                FileName = shell,
                Arguments = isWindows ? $"{shellArg} \"{command}\"" : $"{shellArg} \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = workingDirectory
            };

            using var process = new Process { StartInfo = psi };
            var output = new StringBuilder();
            var error = new StringBuilder();

            process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) error.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var completed = await Task.Run(() => process.WaitForExit(timeoutSeconds * 1000), cancellationToken);

            if (!completed)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return ToolResult.FromString(FormatError($"команда не завершилась за {timeoutSeconds}с", shellHint));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return ToolResult.FromString(FormatError("команда отменена", shellHint));
            }

            var outStr = output.ToString().TrimEnd();
            var errStr = error.ToString().TrimEnd();
            var hasErrorOutput = !string.IsNullOrEmpty(errStr);
            var exitedWithError = process.ExitCode != 0;

            var result = new StringBuilder();
            if (!string.IsNullOrEmpty(outStr))
                result.Append(outStr);
            if (hasErrorOutput)
                result.Append(result.Length > 0 ? "\n--- stderr ---\n" : "").Append(errStr);

            var resultStr = result.Length > 0 ? result.ToString() : "(пусто)";

            if (exitedWithError || (hasErrorOutput && string.IsNullOrEmpty(outStr)))
            {
                resultStr = FormatError(
                    $"код возврата {process.ExitCode}.\n{resultStr}",
                    shellHint ?? BuildShellHint(command));
            }
            else if (shellHint != null)
            {
                resultStr = $"{resultStr}\n\n[подсказка: {shellHint}]";
            }

            var maxOutputLen = _options.MaxOutputChars > 0 ? _options.MaxOutputChars : 6000;
            if (resultStr.Length > maxOutputLen)
            {
                var tail = resultStr[^maxOutputLen..];
                resultStr = $"[... вывод обрезан, показаны последние {maxOutputLen} символов ...]\n" + tail;
            }
            return ToolResult.Structured(new { Command = command, Stdout = outStr, Stderr = errStr, ExitCode = process.ExitCode }, resultStr);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.FromString(FormatError("команда отменена", shellHint));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Terminal tool error");
            return ToolResult.FromString($"Ошибка: {ex.Message}");
        }
    }

    private static (bool isWindows, string shell, string shellArg) DetectShell()
    {
        if (OperatingSystem.IsWindows())
        {
            // Prefer PowerShell on Windows
            return (true, "powershell", "-NoProfile -Command");
        }
        return (false, "/bin/sh", "-c");
    }

    private static string? BuildShellHint(string command)
    {
        if (OperatingSystem.IsWindows())
        {
            // Detect cmd-style flags that won't work in PowerShell
            if (command.Contains(" /") && !command.Contains(" /?"))
            {
                var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    if (part.StartsWith('/') && part.Length > 1 && char.IsLetter(part[1]))
                        return $"В PowerShell используй '-' вместо '/' для флагов (например, -Recurse вместо /s)";
                }
            }
        }
        return null;
    }

    private static string FormatError(string message, string? shellHint = null)
    {
        var result = $"Ошибка: {message}";
        if (shellHint != null)
            result += $"\n\n[подсказка: {shellHint}]";
        return result;
    }
}

public class TerminalToolOptions
{
    public const string SectionName = "Terminal";

    public int DefaultTimeoutSeconds { get; set; } = 60;
    public int MinTimeoutSeconds { get; set; } = 1;
    public int MaxTimeoutSeconds { get; set; } = 300;
    public int MaxOutputChars { get; set; } = 6000;
}

public record TerminalArgs
{
    [JsonPropertyName("command")]
    [ToolParameter(Type = "string", Description = "Команда для выполнения", Required = true)]
    public string? Command { get; init; }

    [JsonPropertyName("timeout_seconds")]
    [ToolParameter(Type = "number", Description = "Таймаут в сек (по умолчанию 60, макс 300)")]
    public int? TimeoutSeconds { get; init; }

    [JsonPropertyName("working_directory")]
    [ToolParameter(Type = "string", Description = "Рабочая директория для команды (по умолчанию — корень проекта)")]
    public string? WorkingDirectory { get; init; }
}

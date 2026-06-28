using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentSharp;
using Microsoft.Extensions.Logging;

namespace Click.Agents.Common.Tools;

public class TerminalToolHandler : IToolHandler
{
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private static readonly string ShellName = ResolveShellName();

    private static readonly Regex CmdSwitchPattern = new(
        @"\s/[A-Za-z]+\b",
        RegexOptions.Compiled);

    private static readonly Regex CmdOnlyCommandPattern = new(
        @"\b(cmd|findstr|xcopy|robocopy|mklink)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ILogger<TerminalToolHandler> _logger;
    private readonly TerminalToolOptions _options;

    public TerminalToolHandler(
        ILogger<TerminalToolHandler> logger,
        TerminalToolOptions options)
    {
        _logger = logger;
        _options = options;
    }

    private static string ResolveShellName()
    {
        if (!IsWindows)
            return "/bin/sh";

        try
        {
            var ps7 = FindProgramInPath("pwsh.exe");
            if (!string.IsNullOrEmpty(ps7))
                return ps7;
        }
        catch { }

        return "powershell.exe";
    }

    private static string? FindProgramInPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var paths = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var path in paths)
        {
            var fullPath = Path.Combine(path, fileName);
            if (File.Exists(fullPath))
                return fullPath;
        }
        return null;
    }

    public Task<ToolResult> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!ToolArgumentValidator.TryValidateJson<TerminalArgs>(argumentsJson, out var args, out var err) || args == null)
                return Task.FromResult(ToolResult.FromString(ToolResultFormatter.Error(
                    "Ошибка terminal", "неверные arguments для terminal. " + (err ?? ""),
                    null)));

            if (string.IsNullOrWhiteSpace(args.Command))
                return Task.FromResult(ToolResult.FromString(ToolResultFormatter.Error("Ошибка terminal", "укажите command", null)));

            var defaultTimeout = _options.DefaultTimeoutSeconds > 0 ? _options.DefaultTimeoutSeconds : 60;
            var minTimeout = _options.MinTimeoutSeconds > 0 ? _options.MinTimeoutSeconds : 1;
            var maxTimeout = _options.MaxTimeoutSeconds > 0 ? _options.MaxTimeoutSeconds : 300;
            var timeoutMs = args.TimeoutSeconds.HasValue
                ? Math.Clamp(args.TimeoutSeconds.Value * 1000, minTimeout * 1000, maxTimeout * 1000)
                : defaultTimeout * 1000;

            var command = args.Command.Trim();
            var workingDir = string.IsNullOrWhiteSpace(args.WorkingDirectory) ? null : args.WorkingDirectory.Trim();
            _logger.LogInformation("[Terminal] {Command} (cwd: {Cwd})", command, workingDir ?? ".");

            var shellHint = BuildShellHint(command);
            if (shellHint != null)
                _logger.LogInformation("[Terminal] {Hint}", shellHint);

            return RunCommandAsync(command, timeoutMs, shellHint, workingDir, cancellationToken);
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.FromString(ToolResultFormatter.Error("Ошибка terminal", ex.Message, null)));
        }
    }

    private static string? BuildShellHint(string command)
    {
        if (!IsWindows)
            return null;

        if (CmdOnlyCommandPattern.IsMatch(command))
            return $"команда выполняется в PowerShell ({Path.GetFileName(ShellName)}), а не в cmd.exe; команды cmd-only недоступны";

        var tokens = command.Split(new[] { ' ', '&', '|', ';', '<', '>' }, StringSplitOptions.RemoveEmptyEntries);
        var first = tokens.FirstOrDefault();
        if (string.IsNullOrEmpty(first))
            return null;

        var psBuiltins = new[] { "dir", "cd", "copy", "move", "del", "erase", "type", "echo", "find", "md", "mkdir", "rd", "rmdir" };
        if (psBuiltins.Contains(first, StringComparer.OrdinalIgnoreCase) && CmdSwitchPattern.IsMatch(command))
            return $"команда '{first}' выполняется в PowerShell ({Path.GetFileName(ShellName)}), используй -Switch вместо /switch; например, dir -Name";

        return null;
    }

    private static string FormatError(string message, string? hint)
    {
        var sb = new StringBuilder();
        sb.Append($"Ошибка terminal: {message}");
        if (!string.IsNullOrEmpty(hint))
            sb.Append($"\n{hint}");
        sb.Append($"\n[контекст: команда выполнялась в {Path.GetFileName(ShellName)}]");
        return sb.ToString();
    }



    private static string EscapePowerShellCommand(string command)
    {
        return command.Replace("\"", "\\\"");
    }

    private async Task<ToolResult> RunCommandAsync(string command, int timeoutMs, string? shellHint, string? workingDirectory, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory()
        };

        if (IsWindows)
        {
            psi.FileName = ShellName;
            psi.Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{EscapePowerShellCommand(command)}\"";
        }
        else
        {
            psi.FileName = "/bin/sh";
            psi.Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"";
        }

        var output = new StringBuilder();
        var error = new StringBuilder();

        using var process = new Process { StartInfo = psi };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) output.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) error.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception killEx) { _logger.LogWarning(killEx, "Failed to kill timed-out process"); }
            return ToolResult.FromString(FormatError($"превышено время ожидания ({timeoutMs / 1000} сек). Команда прервана.\nВывод до прерывания:\n{output}", shellHint));
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception killEx) { _logger.LogWarning(killEx, "Failed to kill cancelled process"); }
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
                $"код выхода {process.ExitCode}.\n{resultStr}",
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

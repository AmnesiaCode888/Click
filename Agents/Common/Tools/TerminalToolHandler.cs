using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentSharp;

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

    public Task<string?> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!ToolArgumentValidator.TryValidateJson<TerminalArgs>(argumentsJson, out var args, out var err) || args == null)
                return Task.FromResult<string?>(FormatError("неверные arguments для terminal. " + (err ?? ""), null));

            if (string.IsNullOrWhiteSpace(args.Command))
                return Task.FromResult<string?>(FormatError("укажите command", null));

            var timeoutMs = args.TimeoutSeconds.HasValue
                ? Math.Clamp(args.TimeoutSeconds.Value * 1000, 1000, 300000)
                : 60000;

            var command = args.Command.Trim();
            Console.WriteLine($"[Terminal] {command}");

            var shellHint = BuildShellHint(command);
            if (shellHint != null)
                Console.WriteLine($"[Terminal] {shellHint}");

            return RunCommandAsync(command, timeoutMs, shellHint, cancellationToken);
        }
        catch (Exception ex)
        {
            return Task.FromResult<string?>(FormatError(ex.Message, null));
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

    private static async Task<string?> RunCommandAsync(string command, int timeoutMs, string? shellHint, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
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
            try { process.Kill(entireProcessTree: true); } catch { }
            return FormatError($"превышено время ожидания ({timeoutMs / 1000} сек). Команда прервана.\nВывод до прерывания:\n{output}", shellHint);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return FormatError("команда отменена", shellHint);
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

        const int maxOutputLen = 6000;
        if (resultStr.Length > maxOutputLen)
        {
            var tail = resultStr[^maxOutputLen..];
            resultStr = $"[... вывод обрезан, показаны последние {maxOutputLen} символов ...]\n" + tail;
        }
        return resultStr;
    }
}

public class TerminalArgs
{
    [JsonPropertyName("command")]
    [ToolParameter(Type = "string", Description = "Команда для выполнения", Required = true)]
    public string? Command { get; set; }

    [JsonPropertyName("timeout_seconds")]
    [ToolParameter(Type = "number", Description = "Таймаут в сек (по умолчанию 60, макс 300)")]
    public int? TimeoutSeconds { get; set; }
}

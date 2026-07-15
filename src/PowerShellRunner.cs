using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace HardDuck;

/// <summary>
/// Виконує вбудований PowerShell-скрипт у фоновому процесі (без вікна) і повертає
/// структурований результат. Протокол: скрипт пише лог звичайним Write-Output,
/// а останнім рядком віддає  #RESULT#{"status":"OK","summary":"..."}.
/// Секрети (PIN) передаються лише через stdin — ніколи через командний рядок.
/// </summary>
public static class PowerShellRunner
{
    public sealed record PsResult(string Status, string Summary, string FullLog);

    private sealed class ResultDto
    {
        public string status { get; set; } = "FAIL";
        public string summary { get; set; } = "";
    }

    public static async Task<PsResult> RunAsync(
        string script,
        string? stdinText = null,
        Action<string>? onLogLine = null,
        CancellationToken ct = default)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // Змусити PowerShell писати stdout в UTF-8, щоб кирилиця не побилась
        psi.EnvironmentVariables["POWERSHELL_TELEMETRY_OPTOUT"] = "1";

        using var proc = new Process { StartInfo = psi };
        var log = new StringBuilder();
        string? resultLine = null;

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (e.Data.StartsWith("#RESULT#", StringComparison.Ordinal))
            {
                resultLine = e.Data["#RESULT#".Length..];
            }
            else
            {
                log.AppendLine(e.Data);
                onLogLine?.Invoke(e.Data);
            }
        };
        proc.ErrorDataReceived += (_, e) => StderrHandler(e.Data, log, onLogLine);

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        if (stdinText is not null)
            await proc.StandardInput.WriteAsync(stdinText);
        proc.StandardInput.Close();

        await proc.WaitForExitAsync(ct);
        proc.WaitForExit();

        if (resultLine is null)
            return new PsResult("FAIL", "скрипт завершився без результату (exit " + proc.ExitCode + ")", log.ToString());

        try
        {
            var dto = JsonSerializer.Deserialize<ResultDto>(resultLine) ?? new ResultDto();
            return new PsResult(dto.status, dto.summary, log.ToString());
        }
        catch (JsonException)
        {
            return new PsResult("FAIL", "не вдалось розібрати результат скрипта", log.ToString());
        }
    }

    /// <summary>
    /// Запускає зовнішній .ps1 файл через powershell.exe -File.
    /// Вивід stdout/stderr потоково передається в onLogLine (CLIXML фільтрується).
    /// Повертає (exitCode, повний лог).
    /// </summary>
    public static async Task<(int ExitCode, string FullLog)> RunExternalScriptAsync(
        string scriptPath,
        Action<string>? onLogLine = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + scriptPath + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.EnvironmentVariables["POWERSHELL_TELEMETRY_OPTOUT"] = "1";

        using var proc = new Process { StartInfo = psi };
        var log = new StringBuilder();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var line = e.Data;
            log.AppendLine(line);
            onLogLine?.Invoke(line);
        };
        proc.ErrorDataReceived += (_, e) => StderrHandler(e.Data, log, onLogLine);

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await proc.WaitForExitAsync(ct);
        proc.WaitForExit();

        return (proc.ExitCode, log.ToString());
    }

    private static void StderrHandler(string? data, StringBuilder log, Action<string>? onLogLine)
    {
        if (string.IsNullOrWhiteSpace(data)) return;
        // Пропускаємо CLIXML-шум та прогрес-повідомлення про завантаження модулів
        var line = data.TrimStart();
        if (line.StartsWith("#<") && line.EndsWith(">")) return;
        if (line.StartsWith('<') && line.EndsWith('>')) return; // CLIXML (XML elements)
        if (line.Contains("Preparing modules for first use")) return;

        log.AppendLine("[stderr] " + data);
        onLogLine?.Invoke("[stderr] " + data);
    }
}

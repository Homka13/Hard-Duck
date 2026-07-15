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
        proc.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            // Пропускаємо CLIXML-шум та прогрес-повідомлення про завантаження модулів
            var line = e.Data.TrimStart();
            if (line.StartsWith("#<") && line.EndsWith(">")) return;
            if (line.StartsWith('<') && line.EndsWith('>')) return; // CLIXML (XML elements)
            if (line.Contains("Preparing modules for first use")) return;

            log.AppendLine("[stderr] " + e.Data);
            onLogLine?.Invoke("[stderr] " + e.Data);
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // WriteAsync preserves embedded newlines for multi-line stdin (e.g. webhook stage);
        // existing single-line callers work unchanged — their value becomes the lone line.
        if (stdinText is not null)
            await proc.StandardInput.WriteAsync(stdinText);
        proc.StandardInput.Close();

        await proc.WaitForExitAsync(ct);
        proc.WaitForExit(); // домотує асинхронні читачі stdout/stderr до кінця потоку

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
}

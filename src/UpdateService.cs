using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace HardenWorkstation;

/// <summary>
/// Автооновлення через GitHub Releases:
/// 1. При старті питаємо API про останній реліз і порівнюємо версію з поточною.
/// 2. За командою оператора качаємо EXE + .sha256, звіряємо чексуму.
/// 3. Кладемо новий файл поруч (.new), запускаємо міні-скрипт підміни і виходимо.
/// Репозиторій має бути публічним (або додайте PAT у заголовок Authorization).
/// </summary>
public static class UpdateService
{
    // ЗАПОВНИТИ після створення репозиторію: github.com/{Owner}/{Repo}
    public const string Owner = "Homka13";
    public const string Repo  = "Hard-Duck";

    public sealed record UpdateInfo(Version Latest, string ExeUrl, string? ShaUrl, string ReleaseUrl);

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    private static HttpClient NewHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HardenWorkstation-Updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>Повертає інформацію про оновлення або null (нема новішої версії / нема мережі).</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        if (Owner == "YOUR-GITHUB-USER") return null; // репозиторій ще не налаштований

        try
        {
            using var http = NewHttp();
            var json = await http.GetStringAsync(
                $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
            if (latest <= CurrentVersion) return null;

            string? exeUrl = null, shaUrl = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                var url = asset.GetProperty("browser_download_url").GetString() ?? "";
                if (name.Equals("HardenWorkstation.exe", StringComparison.OrdinalIgnoreCase)) exeUrl = url;
                if (name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)) shaUrl = url;
            }
            if (exeUrl is null) return null;

            var releaseUrl = root.GetProperty("html_url").GetString() ?? "";
            return new UpdateInfo(latest, exeUrl, shaUrl, releaseUrl);
        }
        catch
        {
            return null; // нема мережі / приватний репозиторій / rate limit — просто мовчимо
        }
    }

    /// <summary>Качає нову версію, звіряє SHA256 і перезапускає застосунок з неї.</summary>
    public static async Task DownloadAndApplyAsync(UpdateInfo update)
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не вдалось визначити шлях до EXE.");

        using var http = NewHttp();
        http.Timeout = TimeSpan.FromMinutes(10);

        var bytes = await http.GetByteArrayAsync(update.ExeUrl);

        // Перевірка цілісності: EXE не підписаний, тож чексума з релізу — головний захист
        if (update.ShaUrl is not null)
        {
            var expected = (await http.GetStringAsync(update.ShaUrl)).Trim().ToLowerInvariant();
            var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (expected != actual)
                throw new InvalidOperationException(
                    "SHA256 завантаженого файлу не збігається з опублікованою чексумою — оновлення скасовано.");
        }

        var newPath = exePath + ".new";
        await File.WriteAllBytesAsync(newPath, bytes);

        // Міні-скрипт: чекає виходу застосунку, підміняє EXE і запускає нову версію
        var cmdPath = Path.Combine(Path.GetTempPath(), "HardenWorkstation-update.cmd");
        var cmd = new StringBuilder()
            .AppendLine("@echo off")
            .AppendLine("timeout /t 2 /nobreak >nul")
            .AppendLine($"move /y \"{exePath}\" \"{exePath}.old\" >nul")
            .AppendLine($"move /y \"{newPath}\" \"{exePath}\" >nul")
            .AppendLine($"del /q \"{exePath}.old\" >nul 2>&1")
            .AppendLine($"start \"\" \"{exePath}\"")
            .AppendLine("del /q \"%~f0\"")
            .ToString();
        await File.WriteAllTextAsync(cmdPath, cmd);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{cmdPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });

        Application.Current.Shutdown();
    }
}

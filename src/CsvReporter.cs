using System.IO;
using System.Text;

namespace HardenWorkstation;

/// <summary>
/// Дописує рядок статусу в C:\ProgramData\ITSecurity\harden-status.csv —
/// той самий файл і той самий порядок колонок, що й у старих PowerShell-скриптів,
/// тож існуючі звіти/Power BI-джерела продовжать працювати.
/// </summary>
public static class CsvReporter
{
    public static readonly string Directory = @"C:\ProgramData\ITSecurity";
    public static readonly string CsvPath = Path.Combine(Directory, "harden-status.csv");

    private static readonly string[] Columns =
    {
        "Computer", "Timestamp", "SecureBoot", "TPM", "EntraJoin",
        "BitLockerPin", "BitLockerKeyToEntra", "Hibernation",
        "UsbStorage", "BiosPassword", "LAPS", "AdminRemoved"
    };

    public static string Append(IReadOnlyDictionary<string, string> values)
    {
        System.IO.Directory.CreateDirectory(Directory);

        // Якщо існуючий файл має стару схему колонок — відкладаємо його вбік, щоб не змішувати формати
        var header = string.Join(",", Columns);
        if (File.Exists(CsvPath))
        {
            var firstLine = File.ReadLines(CsvPath).FirstOrDefault();
            if (firstLine is not null && firstLine != header)
            {
                var archived = Path.Combine(Directory,
                    $"harden-status-old-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
                File.Move(CsvPath, archived);
            }
        }

        var row = string.Join(",", Columns.Select(c => Escape(values.TryGetValue(c, out var v) ? v : "")));
        var sb = new StringBuilder();
        if (!File.Exists(CsvPath))
            sb.AppendLine(header);
        sb.AppendLine(row);
        File.AppendAllText(CsvPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return CsvPath;
    }

    private static string Escape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}

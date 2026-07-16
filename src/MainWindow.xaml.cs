using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.IO;
using System.Windows.Media;
using System.Windows.Documents;

namespace HardDuck;

public partial class MainWindow : Window
{
    /// <summary>Пряме посилання на raw-версію hard-duck.ps1 у публічному Git-репозиторії.</summary>
    private const string HardDuckScriptUrl = "https://raw.githubusercontent.com/Homka13/Hard-Duck/main/tools/hard-duck.ps1";
    private readonly ObservableCollection<StageVm> _stages = new();
    private readonly Dictionary<string, StageVm> _byKey = new();

    public MainWindow()
    {
        InitializeComponent();
        ComputerText.Text = $"Комп'ютер: {Environment.MachineName}   ·   версія {UpdateService.CurrentVersion.ToString(3)}";

        StageList.ItemsSource = _stages;
        UpdateVisibleStages();

        Loaded += async (_, _) => await CheckForUpdateAsync();
    }

    private UpdateService.UpdateInfo? _pendingUpdate;

    private async Task CheckForUpdateAsync()
    {
        _pendingUpdate = await UpdateService.CheckAsync();
        if (_pendingUpdate is null) return;
        UpdateButton.Content = $"Оновити до {_pendingUpdate.Latest.ToString(3)}";
        UpdateButton.Visibility = Visibility.Visible;
    }

    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null) return;
        var confirm = MessageBox.Show(this,
            $"Завантажити версію {_pendingUpdate.Latest.ToString(3)} і перезапустити застосунок?\n\n" +
            "Чексума SHA256 буде перевірена перед встановленням.",
            "Оновлення", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "Завантаження…";
        try
        {
            await UpdateService.DownloadAndApplyAsync(_pendingUpdate);
        }
        catch (Exception ex)
        {
            UpdateButton.IsEnabled = true;
            UpdateButton.Content = $"Оновити до {_pendingUpdate.Latest.ToString(3)}";
            MessageBox.Show(this, "Не вдалось оновитись: " + ex.Message,
                "Оновлення", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AddStage(string key, string title)
    {
        var vm = new StageVm(key, title);
        _stages.Add(vm);
        _byKey[key] = vm;
    }

    private void Log(string line) => Dispatcher.Invoke(() =>
    {
        var para = new Paragraph { Margin = new Thickness(0) };
        Brush brush = line switch
        {
            _ when line.StartsWith("[OK]")    => (Brush)FindResource("StatusOkBrush"),
            _ when line.StartsWith("[X]")     => (Brush)FindResource("StatusFailBrush"),
            _ when line.StartsWith("[!]")     => (Brush)FindResource("StatusWarnBrush"),
            _ when line.StartsWith("[i]")     => (Brush)FindResource("StatusInfoBrush"),
            _ when line.StartsWith("[INFO]")  => (Brush)FindResource("StatusInfoBrush"),
            _ when line.StartsWith("[WARNING]") => (Brush)FindResource("StatusWarnBrush"),
            _ when line.StartsWith("[ERROR]") => (Brush)FindResource("StatusFailBrush"),
            _ => (Brush)FindResource("TerminalForeground")
        };
        para.Inlines.Add(new Run(line) { Foreground = brush });
        LogDocument.Blocks.Add(para);
        LogBox.ScrollToEnd();
    });

    private void SetStage(string key, StageStatus status, string summary) => Dispatcher.Invoke(() =>
    {
        if (_byKey.TryGetValue(key, out var vm))
        {
            vm.Status = status;
            vm.Summary = summary;
        }
    });

    private static StageStatus MapStatus(string psStatus) => psStatus switch
    {
        "OK"   => StageStatus.Ok,
        "WARN" => StageStatus.Warn,
        "SKIP" => StageStatus.Skip,
        _      => StageStatus.Fail
    };

    /// <summary>Виконує один етап-скрипт і оновлює його рядок у чеклісті.</summary>
    private async Task<PowerShellRunner.PsResult> RunStageAsync(string key, string script, string? stdin = null)
    {
        SetStage(key, StageStatus.Running, "виконується…");
        var vm = _byKey[key];
        Log($"── {vm.Title} ──");
        var result = await PowerShellRunner.RunAsync(script, stdin, Log);
        SetStage(key, MapStatus(result.Status), result.Summary);
        return result;
    }

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        RunButton.IsEnabled = false;
        SecureBootToggle.IsEnabled = false;
        LapsToggle.IsEnabled = false;
        UsbToggle.IsEnabled = false;
        BiosToggle.IsEnabled = false;
        BitLockerPinToggle.IsEnabled = false;
        HardDuckToggle.IsEnabled = false;
        RebootButton.Visibility = Visibility.Collapsed;

        // FAB: змінити іконку на спінер
        RunFabIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.Sync;
        StatusLockIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.LockOpenOutline;
        LogBox.Document.Blocks.Clear();
        foreach (var s in _stages) { s.Status = StageStatus.Pending; s.Summary = "очікує"; }

        // Показати індикатор прогресу
        RunProgress.IsIndeterminate = true;
        RunProgress.Visibility = Visibility.Visible;

        var report = new Dictionary<string, string>
        {
            ["Computer"] = Environment.MachineName,
            ["Timestamp"] = DateTime.Now.ToString("o")
        };

        try
        {
            bool aborted = false;

            // ── Критичні перевірки ──
            if (SecureBootToggle.IsChecked == true)
            {
                var sb = await RunStageAsync("SecureBoot", Scripts.SecureBoot);
                report["SecureBoot"] = sb.Summary;
                if (sb.Status == "FAIL") aborted = true;
            }
            else
            {
                SetStage("SecureBoot", StageStatus.Skip, "SKIP - вимкнено оператором");
                report["SecureBoot"] = "SKIP - вимкнено оператором";
            }

            if (!aborted)
            {
                var tpm = await RunStageAsync("TPM", Scripts.Tpm);
                report["TPM"] = tpm.Summary;
                if (tpm.Status == "FAIL") aborted = true;
            }

            // ── BitLocker ──
            if (!aborted)
            {
                bool wantPin = BitLockerPinToggle.IsChecked == true;
                // "TPM" — сентинел для скрипту (без PIN). Якщо PIN потрібен, тут будь-яке
                // непорожнє значення != "TPM"; реальний PIN підставляється нижче лише коли
                // його справді бракує — інакше скрипт сам розпізнає вже існуючий TpmPin-протектор.
                string stdin = wantPin ? "KEEP" : "TPM";

                if (wantPin)
                {
                    var hasPin = await PowerShellRunner.RunAsync(Scripts.BitLockerHasPin, onLogLine: Log);
                    if (hasPin.Summary == "NOPIN")
                    {
                        var dlg = new PinDialog { Owner = this };
                        if (dlg.ShowDialog() == true)
                        {
                            stdin = dlg.Pin;
                        }
                        else
                        {
                            SetStage("BitLockerPin", StageStatus.Fail, "FAIL - введення PIN скасовано");
                            report["BitLockerPin"] = "FAIL - введення PIN скасовано";
                            aborted = true;
                        }
                    }
                }

                if (!aborted)
                {
                    var bl = await RunStageAsync("BitLockerPin", Scripts.BitLockerEnable, stdin);
                    stdin = "";
                    report["BitLockerPin"] = bl.Summary;
                    if (bl.Status == "FAIL") aborted = true;
                }
            }

            // ── Некритичні етапи: виконуються завжди, якщо не було aborted ──
            if (!aborted)
            {
                var hib = await RunStageAsync("Hibernation", Scripts.Hibernation);
                report["Hibernation"] = hib.Summary;

                if (UsbToggle.IsChecked == true)
                {
                    var usb = await RunStageAsync("UsbStorage", Scripts.UsbStorage);
                    report["UsbStorage"] = usb.Summary;
                }
                else
                {
                    SetStage("UsbStorage", StageStatus.Skip, "SKIP - вимкнено оператором");
                    report["UsbStorage"] = "SKIP - вимкнено оператором";
                }

                if (BiosToggle.IsChecked == true)
                {
                    var bios = await RunStageAsync("BiosPassword", Scripts.BiosPassword);
                    report["BiosPassword"] = bios.Summary;
                }
                else
                {
                    SetStage("BiosPassword", StageStatus.Skip, "SKIP - вимкнено оператором");
                    report["BiosPassword"] = "SKIP - вимкнено оператором";
                }

                if (LapsToggle.IsChecked == true)
                {
                    var laps = await RunStageAsync("LAPS", Scripts.Laps);
                    report["LAPS"] = laps.Summary;
                }
                else
                {
                    SetStage("LAPS", StageStatus.Skip, "SKIP - вимкнено оператором");
                    report["LAPS"] = "SKIP - вимкнено оператором";
                }

                // ── Hard-Duck: завантажити hard-duck.ps1 з GitHub, виконати, прибрати за собою ──
                if (HardDuckToggle.IsChecked == true)
                {
                    SetStage("HardDuck", StageStatus.Running, "завантаження скрипта…");
                    Log("── Hard-Duck: завантаження hard-duck.ps1 з GitHub ──");
                    await DownloadAndRunHardDuckAsync(report, CancellationToken.None);
                }
                else
                {
                    SetStage("HardDuck", StageStatus.Skip, "SKIP - вимкнено оператором");
                    report["HardDuck"] = "SKIP - вимкнено оператором";
                }

                // ── Зняття прав адміна: спершу визначаємо користувача, потім явне підтвердження ──
                SetStage("AdminRemoved", StageStatus.Running, "визначаю користувача…");
                var detect = await PowerShellRunner.RunAsync(Scripts.DetectDailyUser, onLogLine: Log);
                if (detect.Status == "OK" && !string.IsNullOrWhiteSpace(detect.Summary))
                {
                    var user = detect.Summary;
                    var confirm = MessageBox.Show(this,
                        $"Щоденний користувач цієї машини визначений як «{user}».\n\n" +
                        "Прибрати його з групи адміністраторів? Зміна набуде чинності при наступному вході.",
                        "Зняття прав адміністратора",
                        MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (confirm == MessageBoxResult.Yes)
                    {
                        var rm = await RunStageAsync("AdminRemoved", Scripts.RemoveAdminRights, user);
                        report["AdminRemoved"] = rm.Summary;
                    }
                    else
                    {
                        SetStage("AdminRemoved", StageStatus.Skip, "SKIP - оператор відмовився");
                        report["AdminRemoved"] = "SKIP - оператор відмовився";
                    }
                }
                else
                {
                    // Користувача не визначено — мовчки призначаємо 'local'
                    SetStage("AdminRemoved", StageStatus.Skip,
                        "SKIP - користувача не визначено, права не знято");
                    report["AdminRemoved"] = "SKIP - користувача не визначено (local)";
                }
            }

            // ── Підсумок і CSV ──
            var csvPath = CsvReporter.Append(report);
            Log("");
            Log($"[i] Готово. Підсумок дописано в {csvPath}");

            var anyFail = _stages.Any(s => s.Status == StageStatus.Fail);
            var anyWarn = _stages.Any(s => s.Status == StageStatus.Warn);
            BottomStatus.Text = aborted
                ? "Зупинено: усуньте червоний пункт і запустіть повторно. Звіт збережено: " + csvPath
                : anyFail || anyWarn
                    ? "Завершено з попередженнями — пункти жовтим/червоним потребують уваги. Звіт: " + csvPath
                    : "Усі етапи виконано успішно. Звіт: " + csvPath;

            if (!aborted)
                RebootButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log("[X] Неочікувана помилка: " + ex.Message);
            BottomStatus.Text = "Неочікувана помилка: " + ex.Message;
        }
        finally
        {
            RunProgress.IsIndeterminate = false;
            RunProgress.Visibility = Visibility.Collapsed;
            RunFabIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.ShieldCheck;
            StatusLockIcon.Kind = MaterialDesignThemes.Wpf.PackIconKind.LockOutline;
            RunButton.IsEnabled = true;
            SecureBootToggle.IsEnabled = true;
            LapsToggle.IsEnabled = true;
            UsbToggle.IsEnabled = true;
            BiosToggle.IsEnabled = true;
            BitLockerPinToggle.IsEnabled = true;
            HardDuckToggle.IsEnabled = true;
        }
    }

    private async Task DownloadAndRunHardDuckAsync(Dictionary<string, string> report, CancellationToken ct)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), "hard-duck_runtime.ps1");
        try
        {
            // ── Завантажити hard-duck.ps1 з публічного Git-репозиторію ──
            SetStage("HardDuck", StageStatus.Running, "завантаження…");
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(30);
            var scriptText = await http.GetStringAsync(HardDuckScriptUrl, ct);
            await File.WriteAllTextAsync(scriptPath, scriptText, ct);
            Log("[OK] hard-duck.ps1 завантажено з GitHub.");

            // ── Зібрати аргументи зі стану чекбоксів UI ──
            var argList = new List<string>();
            if (SecureBootToggle.IsChecked == true) argList.Add("-EnableSecureBoot");
            if (UsbToggle.IsChecked == true) argList.Add("-DisableUSB");
            if (BiosToggle.IsChecked == true) argList.Add("-EnableBIOSPassword");
            if (BitLockerPinToggle.IsChecked == true) argList.Add("-EnableBitLockerPIN");
            if (LapsToggle.IsChecked == true) argList.Add("-EnableLAPS");
            if (HardDuckToggle.IsChecked == true) argList.Add("-EnableHardDuckAdmin");
            var extraArgs = string.Join(" ", argList);

            // ── Виконати завантажений скрипт ──
            SetStage("HardDuck", StageStatus.Running, "виконується…");
            Log($"── Hard-Duck: виконання (аргументи: {(extraArgs.Length > 0 ? extraArgs : "(без прапорців)")}) ──");
            var (exitCode, _) = await PowerShellRunner.RunExternalScriptAsync(scriptPath, extraArgs, Log, ct);

            if (exitCode == 0)
            {
                SetStage("HardDuck", StageStatus.Ok, "OK — пароль адміна оновлено, ключ відправлено в Infisical");
                report["HardDuck"] = "OK";
            }
            else
            {
                SetStage("HardDuck", StageStatus.Fail, $"FAIL — скрипт завершився з кодом {exitCode}");
                report["HardDuck"] = $"FAIL — exit code {exitCode}";
            }
        }
        catch (HttpRequestException ex)
        {
            SetStage("HardDuck", StageStatus.Fail, "FAIL — не вдалось завантажити скрипт: " + ex.Message);
            report["HardDuck"] = "FAIL — завантаження: " + ex.Message;
        }
        catch (Exception ex)
        {
            SetStage("HardDuck", StageStatus.Fail, "FAIL — " + ex.Message);
            report["HardDuck"] = "FAIL: " + ex.Message;
        }
        finally
        {
            // ── Очищення: видалити тимчасовий файл ──
            try { File.Delete(scriptPath); } catch { /* ignore */ }
        }
    }

    private void OnToggleChanged(object sender, RoutedEventArgs e)
    {
        UpdateVisibleStages();
    }

    private void UpdateVisibleStages()
    {
        if (SecureBootToggle == null || UsbToggle == null || BiosToggle == null || LapsToggle == null || HardDuckToggle == null)
            return;

        _stages.Clear();
        _byKey.Clear();

        if (SecureBootToggle.IsChecked == true)
        {
            AddStage("SecureBoot", "Secure Boot");
        }

        AddStage("TPM", "TPM 2.0");
        AddStage("BitLockerPin", "BitLocker (шифрування диска)");
        AddStage("Hibernation", "Гібернація замість сну");

        if (UsbToggle.IsChecked == true)
        {
            AddStage("UsbStorage", "USB-накопичувачі заборонено");
        }

        if (BiosToggle.IsChecked == true)
        {
            AddStage("BiosPassword", "Пароль BIOS/UEFI (Lenovo)");
        }

        if (LapsToggle.IsChecked == true)
        {
            AddStage("LAPS", "Windows LAPS");
        }

        if (HardDuckToggle.IsChecked == true)
        {
            AddStage("HardDuck", "Hard-Duck: пароль адміна + BitLocker → Infisical");
        }

        AddStage("AdminRemoved", "Права адміністратора користувача");
    }

    private void OnReboot(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(this,
            "Перезавантажити зараз, щоб перевірити застосовані налаштування?",
            "Перезавантаження", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm == MessageBoxResult.Yes)
            Process.Start(new ProcessStartInfo("shutdown", "/r /t 5 /c \"Перевірка налаштувань захисту\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
    }
}

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;

namespace HardDuck;

public partial class MainWindow : Window
{
    /// <summary>Пряме посилання на raw-версію nosuha.ps1 у публічному Git-репозиторії.</summary>
    private const string NosuhaScriptUrl = "https://raw.githubusercontent.com/Homka13/Hard-Duck/main/tools/nosuha.ps1";
    private readonly ObservableCollection<StageVm> _stages = new();
    private readonly Dictionary<string, StageVm> _byKey = new();

    public MainWindow()
    {
        InitializeComponent();
        ComputerText.Text = $"Комп'ютер: {Environment.MachineName}   ·   версія {UpdateService.CurrentVersion.ToString(3)}";

        AddStage("SecureBoot", "Secure Boot");
        AddStage("TPM", "TPM 2.0");
        AddStage("BitLockerPin", "BitLocker (шифрування диска)");
        AddStage("Hibernation", "Гібернація замість сну");
        AddStage("UsbStorage", "USB-накопичувачі заборонено");
        AddStage("BiosPassword", "Пароль BIOS/UEFI (Lenovo)");
        AddStage("LAPS", "Windows LAPS");
        AddStage("Nosuha", "Nosuha: пароль адміна + BitLocker → Infisical");
        AddStage("AdminRemoved", "Права адміністратора користувача");

        StageList.ItemsSource = _stages;

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
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    });

    private void SetStage(string key, StageStatus status, string summary) => Dispatcher.Invoke(() =>
    {
        var vm = _byKey[key];
        vm.Status = status;
        vm.Summary = summary;
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
        SecureBootCheck.IsEnabled = false;
        LapsCheck.IsEnabled = false;
        UsbCheck.IsEnabled = false;
        BiosCheck.IsEnabled = false;
        BitLockerPinCheck.IsEnabled = false;
        NosuhaCheck.IsEnabled = false;
        RebootButton.Visibility = Visibility.Collapsed;
        LogBox.Clear();
        foreach (var s in _stages) { s.Status = StageStatus.Pending; s.Summary = "очікує"; }

        var report = new Dictionary<string, string>
        {
            ["Computer"] = Environment.MachineName,
            ["Timestamp"] = DateTime.Now.ToString("o")
        };

        try
        {
            bool aborted = false;

            // ── Критичні перевірки ──
            if (SecureBootCheck.IsChecked == true)
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
                bool wantPin = BitLockerPinCheck.IsChecked == true;
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

                if (UsbCheck.IsChecked == true)
                {
                    var usb = await RunStageAsync("UsbStorage", Scripts.UsbStorage);
                    report["UsbStorage"] = usb.Summary;
                }
                else
                {
                    SetStage("UsbStorage", StageStatus.Skip, "SKIP - вимкнено оператором");
                    report["UsbStorage"] = "SKIP - вимкнено оператором";
                }

                if (BiosCheck.IsChecked == true)
                {
                    var bios = await RunStageAsync("BiosPassword", Scripts.BiosPassword);
                    report["BiosPassword"] = bios.Summary;
                }
                else
                {
                    SetStage("BiosPassword", StageStatus.Skip, "SKIP - вимкнено оператором");
                    report["BiosPassword"] = "SKIP - вимкнено оператором";
                }

                if (LapsCheck.IsChecked == true)
                {
                    var laps = await RunStageAsync("LAPS", Scripts.Laps);
                    report["LAPS"] = laps.Summary;
                }
                else
                {
                    SetStage("LAPS", StageStatus.Skip, "SKIP - вимкнено оператором");
                    report["LAPS"] = "SKIP - вимкнено оператором";
                }

                // ── Nosuha: завантажити nosuha.ps1 з GitHub, виконати, прибрати за собою ──
                if (NosuhaCheck.IsChecked == true)
                {
                    SetStage("Nosuha", StageStatus.Running, "завантаження скрипта…");
                    Log("── Nosuha: завантаження nosuha.ps1 з GitHub ──");
                    await DownloadAndRunNosuhaAsync(report, ct);
                }
                else
                {
                    SetStage("Nosuha", StageStatus.Skip, "SKIP - вимкнено оператором");
                    report["Nosuha"] = "SKIP - вимкнено оператором";
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
            RunButton.IsEnabled = true;
            RunButton.Content = "Запустити повторно";
            SecureBootCheck.IsEnabled = true;
            LapsCheck.IsEnabled = true;
            UsbCheck.IsEnabled = true;
            BiosCheck.IsEnabled = true;
            BitLockerPinCheck.IsEnabled = true;
            NosuhaCheck.IsEnabled = true;
        }
    }

    private async Task DownloadAndRunNosuhaAsync(Dictionary<string, string> report, CancellationToken ct)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), "nosuha_runtime.ps1");
        try
        {
            // ── Завантажити nosuha.ps1 з публічного Git-репозиторію ──
            SetStage("Nosuha", StageStatus.Running, "завантаження…");
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(30);
            var scriptText = await http.GetStringAsync(NosuhaScriptUrl, ct);
            await File.WriteAllTextAsync(scriptPath, scriptText, ct);
            Log("[OK] nosuha.ps1 завантажено з GitHub.");

            // ── Зібрати аргументи зі стану чекбоксів UI ──
            var argList = new List<string>();
            if (SecureBootCheck.IsChecked == true) argList.Add("-EnableSecureBoot");
            if (UsbCheck.IsChecked == true) argList.Add("-DisableUSB");
            if (BiosCheck.IsChecked == true) argList.Add("-EnableBIOSPassword");
            if (BitLockerPinCheck.IsChecked == true) argList.Add("-EnableBitLockerPIN");
            if (LapsCheck.IsChecked == true) argList.Add("-EnableLAPS");
            if (NosuhaCheck.IsChecked == true) argList.Add("-EnableNosuhaAdmin");
            var extraArgs = string.Join(" ", argList);

            // ── Виконати завантажений скрипт ──
            SetStage("Nosuha", StageStatus.Running, "виконується…");
            Log($"── Nosuha: виконання (аргументи: {(extraArgs.Length > 0 ? extraArgs : "(без прапорців)")}) ──");
            var (exitCode, _) = await PowerShellRunner.RunExternalScriptAsync(scriptPath, extraArgs, Log, ct);

            if (exitCode == 0)
            {
                SetStage("Nosuha", StageStatus.OK, "OK — пароль адміна оновлено, ключ відправлено в Infisical");
                report["Nosuha"] = "OK";
            }
            else
            {
                SetStage("Nosuha", StageStatus.Fail, $"FAIL — скрипт завершився з кодом {exitCode}");
                report["Nosuha"] = $"FAIL — exit code {exitCode}";
            }
        }
        catch (HttpRequestException ex)
        {
            SetStage("Nosuha", StageStatus.Fail, "FAIL — не вдалось завантажити скрипт: " + ex.Message);
            report["Nosuha"] = "FAIL — завантаження: " + ex.Message;
        }
        catch (Exception ex)
        {
            SetStage("Nosuha", StageStatus.Fail, "FAIL — " + ex.Message);
            report["Nosuha"] = "FAIL: " + ex.Message;
        }
        finally
        {
            // ── Очищення: видалити тимчасовий файл ──
            try { File.Delete(scriptPath); } catch { /* ignore */ }
        }
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

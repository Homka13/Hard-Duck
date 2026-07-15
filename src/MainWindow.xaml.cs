using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;

namespace HardDuck;

public partial class MainWindow : Window
{
    /// <summary>Google Apps Script webhook URL for Nosuha. Replace YOUR_ID before building.</summary>
    private const string NosuhaWebhookUrl = "https://script.google.com/macros/s/YOUR_ID/exec";
    private readonly ObservableCollection<StageVm> _stages = new();
    private readonly Dictionary<string, StageVm> _byKey = new();

    public MainWindow()
    {
        InitializeComponent();
        ComputerText.Text = $"Комп'ютер: {Environment.MachineName}   ·   версія {UpdateService.CurrentVersion.ToString(3)}";

        AddStage("SecureBoot", "Secure Boot");
        AddStage("TPM", "TPM 2.0");
        AddStage("EntraJoin", "Приєднання до Entra ID");
        AddStage("BitLockerPin", "BitLocker (шифрування диска)");
        AddStage("BitLockerKeyToEntra", "Recovery-ключ в Entra ID");
        AddStage("Hibernation", "Гібернація замість сну");
        AddStage("UsbStorage", "USB-накопичувачі заборонено");
        AddStage("BiosPassword", "Пароль BIOS/UEFI (Lenovo)");
        AddStage("LAPS", "Windows LAPS");
        AddStage("NosuhaAdmin", "Nosuha: пароль адміністратора");
        AddStage("NosuhaWebhook", "Nosuha: відправка на webhook");
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

    private static string SafeGetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var val) ? val.GetString() ?? "" : "";

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

            // ── Критичні перевірки: без них далі йти небезпечно ──
            var sb = await RunStageAsync("SecureBoot", Scripts.SecureBoot);
            report["SecureBoot"] = sb.Summary;
            if (sb.Status == "FAIL") aborted = true;

            if (!aborted)
            {
                var tpm = await RunStageAsync("TPM", Scripts.Tpm);
                report["TPM"] = tpm.Summary;
                if (tpm.Status == "FAIL") aborted = true;
            }

            if (!aborted)
            {
                var entra = await RunStageAsync("EntraJoin", Scripts.EntraJoin);
                report["EntraJoin"] = entra.Summary;
                if (entra.Status == "FAIL")
                {
                    aborted = true;
                    var open = MessageBox.Show(this,
                        "Пристрій не приєднано до Entra ID — без цього ключ BitLocker і пароль LAPS " +
                        "нікуди не збережуться.\n\nВідкрити налаштування приєднання зараз?\n\n" +
                        "Після приєднання запустіть захист повторно.",
                        "Потрібне приєднання до Entra ID",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (open == MessageBoxResult.Yes)
                        Process.Start(new ProcessStartInfo("ms-settings:workplace") { UseShellExecute = true });
                }
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

            if (!aborted)
            {
                var key = await RunStageAsync("BitLockerKeyToEntra", Scripts.RecoveryKeyToEntra);
                report["BitLockerKeyToEntra"] = key.Summary;
                // Невдалий бекап ключа — серйозно, але шифрування вже стоїть; продовжуємо, FAIL видно у звіті.
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

                // ── Nosuha: пароль адміністратора + recovery-ключ на webhook ──
                if (NosuhaCheck.IsChecked == true)
                {
                    // Етап 1: згенерувати пароль, скинути на Administrator, зібрати інфу про машину
                    var adminResult = await RunStageAsync("NosuhaAdmin", Scripts.NosuhaAdminPassword);
                    report["NosuhaAdmin"] = adminResult.Summary;

                    // Етап 2: дістати recovery-ключ, зібрати фінальний JSON, відправити на webhook
                    SetStage("NosuhaWebhook", StageStatus.Running, "виконується…");
                    Log("── Nosuha: відправка на webhook ──");

                    string recoveryKey = "N/A";
                    if (adminResult.Status == "OK")
                    {
                        var blKeyResult = await PowerShellRunner.RunAsync(
                            Scripts.NosuhaGetRecoveryKey, onLogLine: Log);
                        if (blKeyResult.Status == "OK" && !string.IsNullOrWhiteSpace(blKeyResult.Summary))
                            recoveryKey = blKeyResult.Summary;

                        try
                        {
                            using var doc = JsonDocument.Parse(adminResult.Summary);
                            var root = doc.RootElement;
                            var payloadObj = new
                            {
                                ComputerName = SafeGetString(root, "computer"),
                                SerialNumber = SafeGetString(root, "serial"),
                                LoggedInUser = SafeGetString(root, "user"),
                                AdminPassword = SafeGetString(root, "password"),
                                BitLockerRecoveryKey = recoveryKey,
                                Timestamp = DateTime.Now.ToString("o")
                            };
                            var payloadJson = JsonSerializer.Serialize(payloadObj);
                            var stdin = NosuhaWebhookUrl + "\n" + payloadJson;

                            var sendResult = await PowerShellRunner.RunAsync(
                                Scripts.NosuhaSendToWebhook, stdin, Log);
                            SetStage("NosuhaWebhook", MapStatus(sendResult.Status), sendResult.Summary);
                            report["NosuhaWebhook"] = sendResult.Summary;
                        }
                        catch (Exception ex)
                        {
                            SetStage("NosuhaWebhook", StageStatus.Fail,
                                "FAIL - помилка формування/відправки: " + ex.Message);
                            report["NosuhaWebhook"] = "FAIL: " + ex.Message;
                        }
                    }
                    else
                    {
                        SetStage("NosuhaWebhook", StageStatus.Fail,
                            "FAIL - пароль адміністратора не отримано");
                        report["NosuhaWebhook"] = "FAIL - пароль адміністратора не отримано";
                    }
                }
                else
                {
                    SetStage("NosuhaAdmin", StageStatus.Skip, "SKIP - вимкнено оператором");
                    report["NosuhaAdmin"] = "SKIP - вимкнено оператором";
                    SetStage("NosuhaWebhook", StageStatus.Skip, "SKIP - вимкнено оператором");
                    report["NosuhaWebhook"] = "SKIP - вимкнено оператором";
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
                    SetStage("AdminRemoved", StageStatus.Skip,
                        "SKIP - користувача не визначено однозначно, потрібна ручна перевірка ІТ");
                    report["AdminRemoved"] = "SKIP - користувача не визначено однозначно";
                    Log($"[!] Не вдалось однозначно визначити щоденного користувача: {detect.Summary}");
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
            LapsCheck.IsEnabled = true;
            UsbCheck.IsEnabled = true;
            BiosCheck.IsEnabled = true;
            BitLockerPinCheck.IsEnabled = true;
            NosuhaCheck.IsEnabled = true;
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

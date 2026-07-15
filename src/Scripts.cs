namespace HardDuck;

/// <summary>
/// PowerShell-логіка етапів. Кожен скрипт пише людський лог у stdout,
/// а останнім рядком — #RESULT#{"status":"...","summary":"..."}.
/// Статуси: OK | WARN | FAIL | SKIP.
/// </summary>
public static class Scripts
{
    /// <summary>Спільний пролог: кодування + функція Emit.</summary>
    private const string Prolog = """
        $ErrorActionPreference = 'Stop'
        try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
        function Emit([string]$s, [string]$m) {
            Write-Output ("#RESULT#" + (@{ status = $s; summary = $m } | ConvertTo-Json -Compress))
        }

        """;

    public const string SecureBoot = Prolog + """
        function Main {
            try { $on = Confirm-SecureBootUEFI } catch { $on = $false }
            if ($on) {
                Write-Output "[OK] Secure Boot увімкнено."
                Emit 'OK' 'OK'
            } else {
                Write-Output "[X] Secure Boot ВИМКНЕНО або пристрій не UEFI."
                Write-Output "[!] Якщо увімкнути Secure Boot ПІСЛЯ BitLocker — Windows зажадає 48-значний recovery-ключ при завантаженні."
                Write-Output "[!] Зайдіть у прошивку (F1/F2 на Lenovo): Security -> Secure Boot -> Enabled, збережіть, і запустіть програму знову."
                Emit 'FAIL' 'FAIL - Secure Boot вимкнено'
            }
        }
        Main
        """;

    public const string Tpm = Prolog + """
        function Main {
            $tpm = Get-Tpm
            if ($tpm.TpmPresent -and $tpm.TpmReady) {
                Write-Output "[OK] TPM присутній і готовий."
                Emit 'OK' 'OK'
            } else {
                Write-Output "[X] TPM відсутній або не готовий (TpmPresent=$($tpm.TpmPresent), TpmReady=$($tpm.TpmReady))."
                Write-Output "[!] Без TPM протектор TPM+PIN для BitLocker поставити неможливо."
                Emit 'FAIL' 'FAIL - TPM не готовий'
            }
        }
        Main
        """;

    /// <summary>Швидка перевірка: чи вже є протектор TPM+PIN (щоб не питати PIN даремно).</summary>
    public const string BitLockerHasPin = Prolog + """
        function Main {
            Import-Module BitLocker -ErrorAction SilentlyContinue
            $vol = Get-BitLockerVolume -MountPoint 'C:'
            if ($vol.KeyProtector | Where-Object KeyProtectorType -eq 'TpmPin') { Emit 'OK' 'HASPIN' }
            else { Emit 'OK' 'NOPIN' }
        }
        Main
        """;

    /// <summary>
    /// Вмикання BitLocker. Режим приходить одним рядком через stdin:
    /// літерал "TPM" -> лише TPM-протектор (авторозблокування, без запиту при вмиканні);
    /// будь-що інше -> це PIN, ставиться протектор TPM+PIN (питає PIN при кожному вмиканні).
    /// </summary>
    public const string BitLockerEnable = Prolog + """
        function Main {
            $os = Get-CimInstance Win32_OperatingSystem
            if ($os.Caption -notmatch 'Pro|Enterprise|Education') {
                Write-Output "[X] Редакція Windows '$($os.Caption)' не підтримує керований BitLocker (потрібна Pro/Enterprise/Education)."
                Emit 'FAIL' "FAIL - редакція ОС без BitLocker"
                return
            }
            Import-Module BitLocker -ErrorAction SilentlyContinue

            $stdinLine = [Console]::In.ReadLine()
            $wantsPin = -not [string]::IsNullOrEmpty($stdinLine) -and $stdinLine -ne 'TPM'

            $vol = Get-BitLockerVolume -MountPoint 'C:'

            if ($wantsPin) {
                if ($vol.KeyProtector | Where-Object KeyProtectorType -eq 'TpmPin') {
                    Write-Output "[OK] BitLocker з TPM+PIN вже увімкнено (шифрування може ще тривати у фоні)."
                    Emit 'SKIP' 'SKIP - вже увімкнено'
                    return
                }

                # Enable-BitLocker -TpmAndPinProtector відмовляється працювати на недоменній машині,
                # якщо політика 'Require additional authentication at startup' ніколи не вмикалась.
                Write-Output "[i] Налаштовую локальні політики BitLocker (TPM + PIN)..."
                $fve = 'HKLM:\SOFTWARE\Policies\Microsoft\FVE'
                if (-not (Test-Path $fve)) { New-Item -Path $fve -Force | Out-Null }
                Set-ItemProperty -Path $fve -Name 'UseAdvancedStartup' -Value 1 -Type DWord
                Set-ItemProperty -Path $fve -Name 'UseTPM'    -Value 2 -Type DWord
                Set-ItemProperty -Path $fve -Name 'UseTPMPIN' -Value 2 -Type DWord
                gpupdate /force | Out-Null

                $securePin = ConvertTo-SecureString -String $stdinLine -AsPlainText -Force
                Remove-Variable stdinLine

                try {
                    # Якщо раніше стояв протектор лише TPM (без PIN) - прибираємо його, інакше лишаться два протектори.
                    $oldTpm = $vol.KeyProtector | Where-Object KeyProtectorType -eq 'Tpm'
                    foreach ($p in $oldTpm) { Remove-BitLockerKeyProtector -MountPoint 'C:' -KeyProtectorId $p.KeyProtectorId -ErrorAction SilentlyContinue | Out-Null }

                    if ($vol.VolumeStatus -eq 'FullyDecrypted') {
                        Enable-BitLocker -MountPoint 'C:' -TpmAndPinProtector -Pin $securePin -SkipHardwareTest -ErrorAction Stop | Out-Null
                    } else {
                        Add-BitLockerKeyProtector -MountPoint 'C:' -TpmAndPinProtector -Pin $securePin -ErrorAction Stop | Out-Null
                    }
                    Write-Output "[OK] BitLocker з TPM+PIN увімкнено. Шифрування триватиме у фоні."
                    Emit 'OK' 'OK'
                } catch {
                    Write-Output "[X] Помилка ввімкнення BitLocker: $($_.Exception.Message)"
                    Emit 'FAIL' ("FAIL: " + $_.Exception.Message)
                }
            } else {
                if ($vol.KeyProtector | Where-Object KeyProtectorType -eq 'Tpm') {
                    Write-Output "[OK] BitLocker з TPM (без PIN) вже увімкнено — запиту при вмиканні немає."
                    Emit 'SKIP' 'SKIP - вже увімкнено'
                    return
                }

                try {
                    # Прибираємо TPM+PIN-протектор, якщо він був - інакше PIN і далі питатиметься при вмиканні.
                    $oldPin = $vol.KeyProtector | Where-Object KeyProtectorType -eq 'TpmPin'
                    foreach ($p in $oldPin) { Remove-BitLockerKeyProtector -MountPoint 'C:' -KeyProtectorId $p.KeyProtectorId -ErrorAction SilentlyContinue | Out-Null }

                    if ($vol.VolumeStatus -eq 'FullyDecrypted') {
                        Enable-BitLocker -MountPoint 'C:' -TpmProtector -SkipHardwareTest -ErrorAction Stop | Out-Null
                    } else {
                        Add-BitLockerKeyProtector -MountPoint 'C:' -TpmProtector -ErrorAction Stop | Out-Null
                    }
                    Write-Output "[OK] BitLocker з TPM (без PIN) увімкнено. Диск зашифрований, розблоковується автоматично - без запиту при вмиканні."
                    Emit 'OK' 'OK'
                } catch {
                    Write-Output "[X] Помилка ввімкнення BitLocker: $($_.Exception.Message)"
                    Emit 'FAIL' ("FAIL: " + $_.Exception.Message)
                }
            }
        }
        Main
        """;

    public const string Hibernation = Prolog + """
        function Main {
            try {
                powercfg /hibernate on | Out-Null
                powercfg /change standby-timeout-ac 0 | Out-Null
                powercfg /change standby-timeout-dc 0 | Out-Null
                powercfg /change hibernate-timeout-ac 10 | Out-Null
                powercfg /change hibernate-timeout-dc 10 | Out-Null
                # Закриття кришки та кнопка живлення = гібернація (працює і на S3, і на Modern Standby)
                $SUB_BUTTONS = '4f971e89-eebd-4455-a8de-9e59040e7347'
                $LID_ACTION  = '5ca83367-6e45-459f-a27b-476b1d01c936'
                powercfg /setacvalueindex scheme_current $SUB_BUTTONS $LID_ACTION 2 | Out-Null
                powercfg /setdcvalueindex scheme_current $SUB_BUTTONS $LID_ACTION 2 | Out-Null
                powercfg /setactive scheme_current | Out-Null

                $cap = powercfg /a
                if ($cap -match 'Standby \(S0 Low Power Idle\)') {
                    Write-Output "[!] Це залізо на Modern Standby (S0) — головний захист дає саме дія на закриття кришки (вже налаштовано)."
                }
                Write-Output "[OK] Гібернація: 10 хв простою або закриття кришки -> гібернація."
                Emit 'OK' 'OK'
            } catch {
                Write-Output "[X] Помилка налаштування гібернації: $($_.Exception.Message)"
                Emit 'FAIL' ("FAIL: " + $_.Exception.Message)
            }
        }
        Main
        """;

    public const string BiosPassword = Prolog + """
        function Main {
            try {
                $pw = Get-CimInstance -Namespace root\wmi -ClassName Lenovo_BiosPasswordSettings -ErrorAction Stop
            } catch {
                Write-Output "[!] Lenovo WMI-інтерфейс недоступний на цій машині (не Lenovo або стара прошивка)."
                Write-Output "[!] Supervisor-пароль треба поставити вручну в BIOS."
                Emit 'WARN' 'вручну - Lenovo WMI недоступний'
                return
            }
            if ($pw.PasswordState -band 2) {
                Write-Output "[OK] Supervisor-пароль (pap) вже встановлено раніше."
                Emit 'SKIP' 'OK - вже встановлено'
            } else {
                Write-Output "[!] Supervisor-пароль ще не встановлено."
                Write-Output "[!] Обмеження Lenovo: ПЕРШИЙ supervisor-пароль неможливо поставити програмно через WMI —"
                Write-Output "[!] його треба один раз задати вручну: перезавантаження -> F1 -> Security -> Password -> Supervisor Password."
                Write-Output "[i] Після ручного встановлення повторний запуск програми покаже тут OK."
                Emit 'WARN' 'вручну - перший пароль лише через BIOS (обмеження Lenovo)'
            }
        }
        Main
        """;

    public const string UsbStorage = Prolog + """
        function Main {
            try {
                # 1. Драйвер USB mass storage: 4 = вимкнено, 3 = увімкнено (команда відкату).
                #    Периферія (HID/Audio/Video: клавіатури, миші, вебкамери, гарнітури, принтери) НЕ зачіпається.
                Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\USBSTOR' -Name 'Start' -Value 4 -Type DWord
                Write-Output "[OK] Драйвер USBSTOR вимкнено — USB-флешки та зовнішні диски більше не монтуються."

                # 2. UASP-накопичувачі (швидкі SSD-бокси) ходять повз USBSTOR через окремий драйвер
                if (Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Services\UASPStor') {
                    Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\UASPStor' -Name 'Start' -Value 4 -Type DWord
                    Write-Output "[OK] Драйвер UASPStor вимкнено (UASP SSD-бокси)."
                }

                # 3. Телефони як накопичувач (MTP/PTP, клас WPD) + removable disks (SD-кардрідери) —
                #    політика Removable Storage Access: заборона читання і запису
                $base = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices'
                $classes = @(
                    '{6AC27878-A6FA-4155-BA85-F98F491D4F33}',  # WPD devices (MTP/PTP)
                    '{F33FDC04-D1AC-4E8E-9A30-19BBD4B108AE}',  # WPD devices (друга група)
                    '{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}'   # Removable disks
                )
                foreach ($guid in $classes) {
                    $k = Join-Path $base $guid
                    New-Item -Path $k -Force | Out-Null
                    New-ItemProperty -Path $k -Name 'Deny_Read'  -Value 1 -PropertyType DWord -Force | Out-Null
                    New-ItemProperty -Path $k -Name 'Deny_Write' -Value 1 -PropertyType DWord -Force | Out-Null
                }
                Write-Output "[OK] Політика Removable Storage: заборонено телефони (MTP/PTP), removable-диски та SD-картки."

                # Якщо флешка вже вставлена й змонтована — вивантажуємо драйвер, щоб заборона діяла одразу
                try { Stop-Service -Name 'USBSTOR' -Force -ErrorAction SilentlyContinue } catch { }

                Write-Output "[i] Відкат за потреби: Start=3 для USBSTOR/UASPStor + видалити ключі RemovableStorageDevices."
                Emit 'OK' 'OK'
            } catch {
                Write-Output "[X] Помилка блокування USB-накопичувачів: $($_.Exception.Message)"
                Emit 'FAIL' ("FAIL: " + $_.Exception.Message)
            }
        }
        Main
        """;

    public const string Laps = Prolog + """
        function Main {
            $os = Get-CimInstance Win32_OperatingSystem
            $build = [int]$os.BuildNumber
            if ($build -lt 19045) {
                Write-Output "[!] Білд $build замалий для Windows LAPS (потрібен Windows 10 22H2+ або Windows 11 з оновленнями квітня 2023)."
                Emit 'SKIP' "SKIP - застарілий білд $build"
                return
            }
            try {
                $capability = Get-WindowsCapability -Online -Name 'LAPS.ManagementTools~~~~0.0.1.0' -ErrorAction Stop
                if ($capability.State -ne 'Installed') {
                    Add-WindowsCapability -Online -Name 'LAPS.ManagementTools~~~~0.0.1.0' | Out-Null
                }

                # Вбудований Administrator шукаємо за SID (*-500) — ім'я може бути локалізоване
                $builtinAdmin = Get-LocalUser | Where-Object { $_.SID.Value -like '*-500' } | Select-Object -First 1
                if ($builtinAdmin) { Enable-LocalUser -SID $builtinAdmin.SID -ErrorAction SilentlyContinue }

                $lapsKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\LAPS'
                New-Item -Path $lapsKey -Force | Out-Null
                New-ItemProperty -Path $lapsKey -Name 'BackupDirectory'    -Value 1  -PropertyType DWord -Force | Out-Null  # 1 = Entra ID
                New-ItemProperty -Path $lapsKey -Name 'PasswordComplexity' -Value 4  -PropertyType DWord -Force | Out-Null
                New-ItemProperty -Path $lapsKey -Name 'PasswordLength'     -Value 16 -PropertyType DWord -Force | Out-Null
                New-ItemProperty -Path $lapsKey -Name 'PasswordAgeDays'    -Value 30 -PropertyType DWord -Force | Out-Null
                New-ItemProperty -Path $lapsKey -Name 'PostAuthResetDelay' -Value 24 -PropertyType DWord -Force | Out-Null

                Import-Module LAPS -ErrorAction Stop
                # gpupdate політику LAPS не перечитує — потрібен саме цей виклик
                Invoke-LapsPolicyProcessing -ErrorAction Stop
                Start-Sleep -Seconds 5
                Reset-LapsPassword -ErrorAction Stop

                Write-Output "[OK] Windows LAPS налаштовано, пароль ротовано й відправлено в Entra ID."
                Emit 'OK' 'OK'
            } catch {
                Write-Output "[X] Помилка налаштування LAPS: $($_.Exception.Message)"
                try {
                    Get-WinEvent -LogName 'Microsoft-Windows-LAPS/Operational' -MaxEvents 8 -ErrorAction Stop |
                        ForEach-Object { Write-Output ("[!] LAPS event {0}: {1}" -f $_.Id, ($_.Message -split "`n")[0]) }
                } catch { }
                Write-Output "[i] Перевірте також, що в тенанті увімкнено: Entra -> Identity -> Devices -> Device settings -> Enable LAPS = Yes."
                Emit 'FAIL' ("FAIL: " + $_.Exception.Message)
            }
        }
        Main
        """;

    /// <summary>Визначення щоденного користувача (власник explorer.exe). Повертає ім'я у summary або SKIP.</summary>
    public const string DetectDailyUser = Prolog + """
        function Main {
            $excluded = @('Administrator','SYSTEM','DefaultAccount','Guest','WDAGUtilityAccount', "$env:COMPUTERNAME`$")
            try {
                $builtinAdmin = (Get-LocalUser | Where-Object { $_.SID.Value -like '*-500' } | Select-Object -First 1).Name
                if ($builtinAdmin) { $excluded += $builtinAdmin }
            } catch { }

            $debugInfo = "Excluded: $($excluded -join ','). "

            $owners = @()
            try {
                $owners = Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" -ErrorAction Stop |
                    ForEach-Object { (Invoke-CimMethod -InputObject $_ -MethodName GetOwner).User } |
                    Where-Object { $_ -and $excluded -notcontains $_ } | Select-Object -Unique
            } catch { $debugInfo += "ProcErr: $($_.Exception.Message). " }

            $debugInfo += "Explorers: $($owners -join ','). "

            if (@($owners).Count -eq 1) {
                Emit 'OK' (@($owners)[0])
                return
            }

            try {
                $fallback = (Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).UserName
                $debugInfo += "Win32CS: $fallback. "
                if ($fallback) {
                    $name = $fallback.Split('\')[-1]
                    if ($excluded -notcontains $name) { Emit 'OK' $name; return }
                }
            } catch { $debugInfo += "CSErr: $($_.Exception.Message). " }

            try {
                $lastLogon = Get-ItemPropertyValue -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI' -Name 'LastLoggedOnUser' -ErrorAction Stop
                $debugInfo += "LogonUI: $lastLogon. "
                if ($lastLogon) {
                    $name = $lastLogon.Split('\')[-1]
                    if ($excluded -notcontains $name) { Emit 'OK' $name; return }
                }
            } catch { $debugInfo += "RegErr: $($_.Exception.Message). " }

            try {
                $quser = quser.exe 2>$null | Out-String
                $debugInfo += "quser: $($quser -replace '\r?\n','; '). "
            } catch { }

            Emit 'SKIP' $debugInfo
        }
        Main
        """;

    /// <summary>Зняття прав адміністратора. Ім'я користувача приходить через stdin.</summary>
    public const string RemoveAdminRights = Prolog + """
        function Main {
            $user = [Console]::In.ReadLine()
            if ([string]::IsNullOrWhiteSpace($user)) {
                Emit 'SKIP' 'SKIP - користувача не передано'
                return
            }
            try {
                # Група адміністраторів за SID S-1-5-32-544 — назва може бути локалізована ('Адміністратори')
                $group = Get-LocalGroup -SID 'S-1-5-32-544'
                $member = Get-LocalGroupMember -Group $group -ErrorAction Stop |
                    Where-Object { $_.Name -match ("\\" + [regex]::Escape($user) + "$") -or $_.Name -eq $user }
                if (-not $member) {
                    Write-Output "[OK] '$user' і так не входить до адміністраторів."
                    Emit 'SKIP' "SKIP - '$user' вже без прав адміна"
                    return
                }
                Remove-LocalGroupMember -Group $group -Member $member -ErrorAction Stop
                Write-Output "[OK] '$user' прибрано з групи адміністраторів. Зміна набуде чинності при наступному вході."
                Emit 'OK' ("OK (" + $user + ")")
            } catch {
                Write-Output "[X] Не вдалось прибрати права у '$user': $($_.Exception.Message)"
                Emit 'FAIL' ("FAIL: " + $_.Exception.Message)
            }
        }
        Main
        """;

    // ────────────────────────────────────────────────────────────
    //  Hard-Duck / ZTP webhook-орієнтовані етапи
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Генерує криптостійкий 12-значний пароль, скидає його на вбудованого
    /// Administrator (SID *-500), вмикає обліковий запис і повертає JSON:
    /// {"password":"...", "computer":"...", "serial":"...", "user":"..."}
    /// </summary>
    public const string HardDuckAdminPassword = Prolog + """
        function Main {
            # ── Машинна інформація ──
            $computer = $env:COMPUTERNAME
            try { $serial = (Get-CimInstance Win32_BIOS).SerialNumber.Trim() } catch { $serial = 'UNKNOWN' }
            $loggedOn = (Get-CimInstance Win32_ComputerSystem).UserName
            if (-not $loggedOn) { $loggedOn = 'UNKNOWN' }
            # Спробувати дістати повне ім'я
            if ($loggedOn -match '^(.+)\\(.+)$') {
                try {
                    $ua = Get-CimInstance Win32_UserAccount `
                        -Filter "Domain = '$($Matches[1])' AND Name = '$($Matches[2])'" `
                        -ErrorAction SilentlyContinue
                    if ($ua -and $ua.FullName) { $loggedOn = "$($ua.FullName) ($loggedOn)" }
                } catch { }
            }

            # ── Генерація пароля ──
            $upper   = 'ABCDEFGHJKMNPQRSTUVWXYZ'.ToCharArray()
            $lower   = 'abcdefghjkmnpqrstuvwxyz'.ToCharArray()
            $digits  = '23456789'.ToCharArray()
            $special = '!@#$%&*-_+=?'.ToCharArray()
            $pool    = $upper + $lower + $digits + $special
            $rng     = [System.Security.Cryptography.RNGCryptoServiceProvider]::new()
            $bytes   = [byte[]]::new(12)

            $chars = @(
                $upper[0..($rng.GetBytes($bytes); $bytes[0] % $upper.Length)][0],
                $lower[0..($rng.GetBytes($bytes); $bytes[0] % $lower.Length)][0],
                $digits[0..($rng.GetBytes($bytes); $bytes[0] % $digits.Length)][0],
                $special[0..($rng.GetBytes($bytes); $bytes[0] % $special.Length)][0]
            )
            for ($i = 4; $i -lt 12; $i++) {
                $rng.GetBytes($bytes)
                $chars += $pool[$bytes[0] % $pool.Length]
            }
            $rng.Dispose()
            $random = [System.Random]::new()
            $password = ($chars | Sort-Object { $random.Next() }) -join ''

            # ── Знайти вбудованого Administrator за SID *-500 ──
            $adminSid = (Get-CimInstance -ClassName Win32_UserAccount `
                -Filter "SID LIKE '%-500' AND LocalAccount = TRUE").SID
            if (-not $adminSid) {
                Write-Output "[X] Вбудований Administrator (SID *-500) не знайдено."
                Emit 'FAIL' 'FAIL - обліковий запис адміністратора не знайдено'
                return
            }

            $secureSid = [System.Security.Principal.SecurityIdentifier]::new($adminSid)
            $adminName = $secureSid.Translate([System.Security.Principal.NTAccount]).Value.Split('\')[-1]

            try {
                $adm = [ADSI]"WinNT://$computer/$adminName,user"
                $adm.SetPassword($password)
                $adm.SetInfo()

                $flags = $adm.UserFlags[0]
                if ($flags -band 0x0002) {
                    $adm.UserFlags = $flags -bxor 0x0002
                    $adm.SetInfo()
                }
                Write-Output "[OK] Пароль '$adminName' оновлено, обліковий запис увімкнено."

                $result = @{
                    password = $password
                    computer = $computer
                    serial   = $serial
                    user     = $loggedOn
                } | ConvertTo-Json -Compress
                Emit 'OK' $result
            } catch {
                Write-Output "[X] Помилка скидання пароля '$adminName': $($_.Exception.Message)"
                Emit 'FAIL' ("FAIL: " + $_.Exception.Message)
            }
        }
        Main
        """;

    /// <summary>
    /// Дістає BitLocker RecoveryPassword-протектор для диска C:.
    /// Повертає 48-значний ключ у summary або порожній рядок при SKIP.
    /// </summary>
    public const string HardDuckGetRecoveryKey = Prolog + """
        function Main {
            Import-Module BitLocker -ErrorAction SilentlyContinue
            try {
                $vol = Get-BitLockerVolume -MountPoint 'C:' -ErrorAction Stop
                $kp = $vol.KeyProtector |
                    Where-Object { $_.KeyProtectorType -eq 'RecoveryPassword' } |
                    Select-Object -First 1
                if ($kp) {
                    $details = $kp | Get-BitLockerKeyProtector -ErrorAction Stop
                    if ($details -and $details.RecoveryPassword) {
                        Write-Output "[OK] BitLocker recovery-ключ отримано."
                        Emit 'OK' $details.RecoveryPassword
                    } else {
                        Write-Output "[!] RecoveryPassword протектор знайдено, але ключ порожній."
                        Emit 'SKIP' ''
                    }
                } else {
                    Write-Output "[!] RecoveryPassword-протектор відсутній на C:."
                    Emit 'SKIP' ''
                }
            } catch {
                Write-Output "[X] Не вдалось отримати BitLocker recovery-ключ: $($_.Exception.Message)"
                Emit 'FAIL' ''
            }
        }
        Main
        """;

    /// <summary>
    /// Надсилає зібрані дані на Google Apps Script webhook.
    /// Рядок 1 stdin — URL вебхука (якщо порожній — значення за замовчуванням).
    /// Рядок 2 stdin — JSON із полями: ComputerName, SerialNumber, LoggedInUser,
    /// AdminPassword, BitLockerRecoveryKey, Timestamp.
    /// </summary>
    public const string HardDuckSendToWebhook = Prolog + """
        function Main {
            $webhookUrl = [Console]::In.ReadLine()
            $jsonPayload = [Console]::In.ReadLine()

            if ([string]::IsNullOrWhiteSpace($jsonPayload)) {
                Write-Output "[X] Порожні дані для вебхука."
                Emit 'FAIL' 'FAIL - немає даних для відправки'
                return
            }

            if ([string]::IsNullOrWhiteSpace($webhookUrl)) {
                $webhookUrl = 'https://script.google.com/macros/s/YOUR_ID/exec'
            }

            try {
                Write-Output "[i] Надсилання даних на webhook..."
                $response = Invoke-RestMethod -Uri $webhookUrl `
                    -Method Post `
                    -Body $jsonPayload `
                    -ContentType 'application/json; charset=utf-8' `
                    -ErrorAction Stop
                Write-Output "[OK] Webhook POST успішно: $($response | ConvertTo-Json -Compress)"
                Emit 'OK' 'OK'
            } catch {
                Write-Output "[X] Помилка відправки на webhook: $($_.Exception.Message)"
                Emit 'FAIL' ("FAIL: " + $_.Exception.Message)
            }
        }
        Main
        """;
}

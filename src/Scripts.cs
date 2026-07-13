namespace HardenWorkstation;

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

    public const string EntraJoin = Prolog + """
        function Main {
            $status = dsregcmd /status
            if ($status -match 'AzureAdJoined\s*:\s*YES') {
                Write-Output "[OK] Пристрій приєднано до Entra ID (повний join)."
                Emit 'OK' 'OK'
            } else {
                Write-Output "[X] Пристрій НЕ приєднано до Entra ID."
                Write-Output "[i] Потрібен саме повний join (не 'робочий акаунт'): Параметри -> Облікові записи -> Access work or school ->"
                Write-Output "[i] Connect -> 'Join this device to Microsoft Entra ID'. Після приєднання запустіть програму знову."
                Emit 'FAIL' 'FAIL - не приєднано до Entra ID'
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

    /// <summary>Вмикання BitLocker TPM+PIN. PIN приходить одним рядком через stdin.</summary>
    public const string BitLockerEnable = Prolog + """
        function Main {
            $os = Get-CimInstance Win32_OperatingSystem
            if ($os.Caption -notmatch 'Pro|Enterprise|Education') {
                Write-Output "[X] Редакція Windows '$($os.Caption)' не підтримує BitLocker з PIN (потрібна Pro/Enterprise/Education)."
                Emit 'FAIL' "FAIL - редакція ОС без BitLocker"
                return
            }
            Import-Module BitLocker -ErrorAction SilentlyContinue
            $vol = Get-BitLockerVolume -MountPoint 'C:'
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

            $pin = [Console]::In.ReadLine()
            if ([string]::IsNullOrWhiteSpace($pin)) {
                Emit 'FAIL' 'FAIL - PIN не передано'
                return
            }
            $securePin = ConvertTo-SecureString -String $pin -AsPlainText -Force
            Remove-Variable pin

            try {
                Enable-BitLocker -MountPoint 'C:' -TpmAndPinProtector -Pin $securePin -SkipHardwareTest -ErrorAction Stop | Out-Null
                Write-Output "[OK] BitLocker з TPM+PIN увімкнено. Шифрування триватиме у фоні."
                Emit 'OK' 'OK'
            } catch {
                Write-Output "[X] Помилка ввімкнення BitLocker: $($_.Exception.Message)"
                Emit 'FAIL' ("FAIL: " + $_.Exception.Message)
            }
        }
        Main
        """;

    public const string RecoveryKeyToEntra = Prolog + """
        function Main {
            Import-Module BitLocker -ErrorAction SilentlyContinue
            $vol = Get-BitLockerVolume -MountPoint 'C:'
            $rp = $vol.KeyProtector | Where-Object KeyProtectorType -eq 'RecoveryPassword' | Select-Object -First 1
            if (-not $rp) {
                Write-Output "[i] Recovery-протектора ще немає — додаю."
                Add-BitLockerKeyProtector -MountPoint 'C:' -RecoveryPasswordProtector | Out-Null
                $vol = Get-BitLockerVolume -MountPoint 'C:'
                $rp = $vol.KeyProtector | Where-Object KeyProtectorType -eq 'RecoveryPassword' | Select-Object -First 1
            }
            try {
                BackupToAAD-BitLockerKeyProtector -MountPoint 'C:' -KeyProtectorId $rp.KeyProtectorId -ErrorAction Stop | Out-Null
                Write-Output "[OK] Recovery-ключ забекаплено в Entra ID."
                Emit 'OK' 'OK'
            } catch {
                Write-Output "[X] Не вдалось забекапити ключ в Entra ID: $($_.Exception.Message)"
                Write-Output "[!] Машина лишається зашифрованою, але ключ треба задокументувати вручну (KeePass-vault)."
                Emit 'FAIL' ("FAIL: " + $_.Exception.Message)
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

    /// <summary>
    /// BIOS-пароль, мультивендорно. Через stdin приходить шлях до bios-encrypt-public.cer (або порожній рядок).
    /// Lenovo: перший пароль лише вручну (обмеження прошивки) — чесна перевірка стану.
    /// Dell/HP: генеруємо випадковий пароль, ставимо через вендорський WMI, шифруємо сертифікатом (CMS,
    /// сумісно з tools/Decrypt-BiosPassword.ps1) і зберігаємо в C:\ProgramData\ITSecurity.
    /// </summary>
    public const string BiosPassword = Prolog + """
        $certPath = [Console]::In.ReadLine()

        function New-BiosSafePassword {
            # Тільки латиниця+цифри, без неоднозначних символів — безпечно для будь-якої BIOS-клавіатури
            $chars = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789'
            -join (1..14 | ForEach-Object { $chars[(Get-Random -Maximum $chars.Length)] })
        }

        function Save-Escrow([string]$pass) {
            $serial = ((Get-CimInstance Win32_BIOS).SerialNumber -replace '[^\w\-]', '')
            $dir = 'C:\ProgramData\ITSecurity'
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
            $file = Join-Path $dir ("bios-{0}-{1}.txt" -f $env:COMPUTERNAME, $serial)
            Protect-CmsMessage -To $certPath -Content $pass -OutFile $file
            return $file
        }

        function Main {
            $vendor = (Get-CimInstance Win32_ComputerSystem).Manufacturer

            # ── LENOVO ──────────────────────────────────────────────────────────────
            if ($vendor -match 'Lenovo') {
                try {
                    $pw = Get-CimInstance -Namespace root\wmi -ClassName Lenovo_BiosPasswordSettings -ErrorAction Stop
                } catch {
                    Write-Output "[!] Lenovo WMI-інтерфейс недоступний — пароль перевірте/поставте вручну в BIOS."
                    Emit 'WARN' 'вручну - Lenovo WMI недоступний'
                    return
                }
                if ($pw.PasswordState -band 2) {
                    Write-Output "[OK] Supervisor-пароль (pap) вже встановлено раніше."
                    Emit 'SKIP' 'OK - вже встановлено'
                } else {
                    Write-Output "[!] Обмеження Lenovo: ПЕРШИЙ supervisor-пароль ставиться лише вручну:"
                    Write-Output "[!] перезавантаження -> F1 -> Security -> Password -> Supervisor Password."
                    Emit 'WARN' 'вручну - перший пароль лише через BIOS (обмеження Lenovo)'
                }
                return
            }

            # Для Dell/HP пароль генерується автоматично — без сертифіката його нікуди безпечно зберегти
            $canEscrow = $certPath -and (Test-Path $certPath)

            # ── DELL ────────────────────────────────────────────────────────────────
            if ($vendor -match 'Dell') {
                try {
                    $po = Get-CimInstance -Namespace 'root\dcim\sysman\wmisecurity' -ClassName PasswordObject -ErrorAction Stop |
                        Where-Object NameId -eq 'Admin'
                } catch {
                    Write-Output "[!] Dell WMI-інтерфейс (root\dcim\sysman\wmisecurity) недоступний — стара прошивка? Пароль вручну."
                    Emit 'WARN' 'вручну - Dell WMI недоступний'
                    return
                }
                if ($po.IsPasswordSet -eq 1) {
                    Write-Output "[OK] Admin-пароль BIOS вже встановлено раніше."
                    Emit 'SKIP' 'OK - вже встановлено'
                    return
                }
                if (-not $canEscrow) {
                    Write-Output "[!] Поруч з EXE немає bios-encrypt-public.cer — автогенерований пароль не було б куди безпечно зберегти."
                    Write-Output "[i] Згенеруйте пару ключів (tools/Generate-BiosKey-GUI.ps1), покладіть .cer поруч з EXE і запустіть повторно."
                    Emit 'WARN' 'вручну - немає сертифіката ескроу'
                    return
                }
                $pass = New-BiosSafePassword
                $si = Get-CimInstance -Namespace 'root\dcim\sysman\wmisecurity' -ClassName SecurityInterface
                $r = Invoke-CimMethod -InputObject $si -MethodName SetnewPassword -Arguments @{
                    NameId = 'Admin'; NewPassword = $pass; OldPassword = ''
                    SecType = 0; SecHndCount = 0; SecHandle = @()
                }
                $code = if ($null -ne $r.Status) { $r.Status } elseif ($null -ne $r.ReturnValue) { $r.ReturnValue } else { -1 }
                if ($code -eq 0) {
                    $file = Save-Escrow $pass
                    Write-Output "[OK] Admin-пароль BIOS встановлено (Dell WMI)."
                    Write-Output "[i] Зашифрований пароль: $file — заберіть файл у vault, розшифровка через tools/Decrypt-BiosPassword.ps1."
                    Emit 'OK' 'OK - встановлено, ескроу збережено'
                } else {
                    Write-Output "[X] Dell SetnewPassword повернув код $code."
                    Emit 'FAIL' ("FAIL - Dell WMI код " + $code)
                }
                return
            }

            # ── HP ──────────────────────────────────────────────────────────────────
            if ($vendor -match 'HP|Hewlett') {
                try {
                    $setting = Get-CimInstance -Namespace 'root\hp\instrumentedBIOS' -ClassName HP_BIOSSetting -ErrorAction Stop |
                        Where-Object Name -eq 'Setup Password'
                } catch {
                    Write-Output "[!] HP WMI-інтерфейс (root\hp\instrumentedBIOS) недоступний — стара прошивка? Пароль вручну."
                    Emit 'WARN' 'вручну - HP WMI недоступний'
                    return
                }
                if ($setting.IsSet -eq 1) {
                    Write-Output "[OK] Setup-пароль BIOS вже встановлено раніше."
                    Emit 'SKIP' 'OK - вже встановлено'
                    return
                }
                if (-not $canEscrow) {
                    Write-Output "[!] Поруч з EXE немає bios-encrypt-public.cer — автогенерований пароль не було б куди безпечно зберегти."
                    Write-Output "[i] Згенеруйте пару ключів (tools/Generate-BiosKey-GUI.ps1), покладіть .cer поруч з EXE і запустіть повторно."
                    Emit 'WARN' 'вручну - немає сертифіката ескроу'
                    return
                }
                $pass = New-BiosSafePassword
                $iface = Get-CimInstance -Namespace 'root\hp\instrumentedBIOS' -ClassName HP_BIOSSettingInterface
                $r = Invoke-CimMethod -InputObject $iface -MethodName SetBIOSSetting -Arguments @{
                    Name = 'Setup Password'; Value = ('<utf-16/>' + $pass); Password = '<utf-16/>'
                }
                if ($r.Return -eq 0) {
                    $file = Save-Escrow $pass
                    Write-Output "[OK] Setup-пароль BIOS встановлено (HP WMI)."
                    Write-Output "[i] Зашифрований пароль: $file — заберіть файл у vault, розшифровка через tools/Decrypt-BiosPassword.ps1."
                    Emit 'OK' 'OK - встановлено, ескроу збережено'
                } else {
                    Write-Output "[X] HP SetBIOSSetting повернув код $($r.Return)."
                    Emit 'FAIL' ("FAIL - HP WMI код " + $r.Return)
                }
                return
            }

            # ── ІНШІ ВЕНДОРИ ────────────────────────────────────────────────────────
            Write-Output "[!] Вендор '$vendor' не підтримується для автоматичного BIOS-пароля — поставте вручну."
            Emit 'WARN' ("вручну - вендор " + $vendor + " не підтримується")
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
            $builtinAdmin = (Get-LocalUser | Where-Object { $_.SID.Value -like '*-500' } | Select-Object -First 1).Name
            $excluded = @('Administrator','SYSTEM','DefaultAccount','Guest','WDAGUtilityAccount', "$env:COMPUTERNAME`$")
            if ($builtinAdmin) { $excluded += $builtinAdmin }

            $owners = @()
            try {
                $owners = Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" -ErrorAction Stop |
                    ForEach-Object { (Invoke-CimMethod -InputObject $_ -MethodName GetOwner).User } |
                    Where-Object { $_ -and $excluded -notcontains $_ } | Select-Object -Unique
            } catch { }

            if (@($owners).Count -eq 1) {
                Emit 'OK' (@($owners)[0])
                return
            }
            $fallback = (Get-CimInstance Win32_ComputerSystem).UserName
            if ($fallback) {
                $name = $fallback.Split('\')[-1]
                if ($excluded -notcontains $name) { Emit 'OK' $name; return }
            }
            Emit 'SKIP' ''
        }
        Main
        """;

    /// <summary>Сервісна дія: повне розблокування USB-накопичувачів (відкат етапу UsbStorage).</summary>
    public const string UsbStorageUnblock = Prolog + """
        function Main {
            try {
                Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\USBSTOR' -Name 'Start' -Value 3 -Type DWord
                if (Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Services\UASPStor') {
                    Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\UASPStor' -Name 'Start' -Value 3 -Type DWord
                }
                $base = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices'
                if (Test-Path $base) { Remove-Item -Path $base -Recurse -Force }
                Write-Output "[OK] USB-накопичувачі розблоковано: драйвери увімкнено, політики Removable Storage знято."
                Write-Output "[i] Вже вставлену флешку перепідключіть (або перезавантажте машину)."
                Emit 'OK' 'OK - USB розблоковано'
            } catch {
                Write-Output "[X] Помилка розблокування USB: $($_.Exception.Message)"
                Emit 'FAIL' ("FAIL: " + $_.Exception.Message)
            }
        }
        Main
        """;

    /// <summary>Сервісна дія: повернути користувачу права адміністратора. Ім'я через stdin.</summary>
    public const string RestoreAdminRights = Prolog + """
        function Main {
            $user = [Console]::In.ReadLine()
            if ([string]::IsNullOrWhiteSpace($user)) {
                Emit 'SKIP' 'SKIP - користувача не передано'
                return
            }
            try {
                $group = Get-LocalGroup -SID 'S-1-5-32-544'
                $already = Get-LocalGroupMember -Group $group -ErrorAction Stop |
                    Where-Object { $_.Name -match ("\\" + [regex]::Escape($user) + "$") -or $_.Name -eq $user }
                if ($already) {
                    Write-Output "[OK] '$user' вже входить до групи адміністраторів."
                    Emit 'SKIP' "SKIP - '$user' вже адміністратор"
                    return
                }
                Add-LocalGroupMember -Group $group -Member $user -ErrorAction Stop
                Write-Output "[OK] '$user' повернуто до групи адміністраторів. Зміна набуде чинності при наступному вході."
                Emit 'OK' ("OK - права повернуто (" + $user + ")")
            } catch {
                Write-Output "[X] Не вдалось повернути права '$user': $($_.Exception.Message)"
                Emit 'FAIL' ("FAIL: " + $_.Exception.Message)
            }
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
}

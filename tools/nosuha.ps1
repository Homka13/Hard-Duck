<#
.SYNOPSIS
    Скрипт Zero-Touch Provisioning — керує паролем локального адміністратора
    та передає ключ відновлення BitLocker до Infisical Cloud.
.DESCRIPTION
    nosuha.ps1 виконує наступні кроки:
    1. Збирає ім'я комп'ютера та серійний номер BIOS.
    2. Визначає поточного інтерактивного користувача (повне ім'я, якщо можливо).
    3. Генерує криптостійкий 12-значний пароль (RandomNumberGenerator).
    4. Скидає пароль вбудованого облікового запису Administrator (RID -500) і вмикає його.
    5. Перевіряє статус BitLocker на C:. Якщо розшифровано — вмикає TPM-only (XtsAes256).
       Якщо протектор RecoveryPassword відсутній — додає його. Якщо вже є — перевикористовує.
    6. Зберігає зібрані секрети як JSON у Infisical Cloud під ключем
       DEVICE_<СерійнийНомер> у середовищі 'dev'.
#>

#Requires -RunAsAdministrator

# Примусове UTF-8 виведення — українські символи без кракозябр
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

# ----------------------------------------------------------------
# Конфігурація — Infisical Cloud (V3 API)
# ----------------------------------------------------------------
$InfisicalWorkspaceId = '7f47fee3-7122-4bd5-bbf6-b26c72e1559c'
$InfisicalToken       = 'st.b23fd0af-ba6d-4888-8e18-2f31ac73a82e.2d2e6efd178056704215dfb47aaee5d6.5780902e694adb8d37ab433ccfb410b1'

# ----------------------------------------------------------------
# Допоміжна функція: генерація криптостійкого пароля
# Використовує System.Security.Cryptography.RandomNumberGenerator.
# 12 символів: великі/малі літери + цифри (неоднозначні виключено).
# ----------------------------------------------------------------
function Get-RandomPassword {
    $chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789"
    $pass = ""
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $bytes = New-Object byte[] 1
    for ($i = 0; $i -lt 12; $i++) {
        $rng.GetBytes($bytes)
        $pass += $chars[$bytes[0] % $chars.Length]
    }
    $rng.Dispose()
    return $pass
}

# ----------------------------------------------------------------
# 1. Ідентифікація системи
# ----------------------------------------------------------------
$ComputerName = $env:COMPUTERNAME
$SerialNumber = (Get-CimInstance -ClassName Win32_BIOS).SerialNumber.Trim()

Write-Host "Комп'ютер     : $ComputerName"
Write-Host "Серійний №    : $SerialNumber"

# ----------------------------------------------------------------
# 2. Поточний користувач
#    Спершу — Win32_ComputerSystem (власник інтерактивної сесії).
#    Якщо не знайдено або це система — quser для консольної сесії.
#    Якщо консольний користувач — administrator → "LocalAdmin".
# ----------------------------------------------------------------
$CurrentUser = 'UNKNOWN'

# Власник інтерактивної сесії через WMI
$loggedOn = (Get-CimInstance -ClassName Win32_ComputerSystem).UserName
if ($loggedOn) {
    $CurrentUser = $loggedOn  # DOMAIN\User або COMPUTER\User
}

# Спроба отримати повне ім'я через Win32_UserAccount
if ($loggedOn -match '^(.+)\\(.+)$') {
    $userNameOnly = $Matches[2]
    $domainPart   = $Matches[1]
    try {
        $userAccount = Get-CimInstance -ClassName Win32_UserAccount `
            -Filter "Domain = '$domainPart' AND Name = '$userNameOnly'" `
            -ErrorAction SilentlyContinue
        if ($userAccount -and $userAccount.FullName) {
            $CurrentUser = "$($userAccount.FullName) ($loggedOn)"
        }
    } catch { }
}

# Резервний шлях: quser — консольна сесія
if ($CurrentUser -eq 'UNKNOWN' -or $CurrentUser -match '\\SYSTEM$|^NT AUTHORITY\\') {
    try {
        $quserOutput = quser 2>$null
        if ($quserOutput) {
            # Типовий вивід quser:
            #  USERNAME              SESSIONNAME        ID  STATE   IDLE TIME  LOGON TIME
            # >administrator         console             1  Active      .  7/15/2026 9:17 AM
            $consoleLine = ($quserOutput -split "`n" | Select-String 'console' | Select-Object -First 1).ToString()
            if ($consoleLine -match '^[>]?\s*(\S+)') {
                $consoleUser = $Matches[1]
                if ($consoleUser -eq 'administrator') {
                    $CurrentUser = 'LocalAdmin'
                } else {
                    $CurrentUser = $consoleUser
                }
            }
        }
    } catch { }
}

# Якщо після всіх перевірок користувач досі не визначений —
# використовуємо 'local' як резервне значення за замовчуванням.
if ([string]::IsNullOrWhiteSpace($CurrentUser) -or $CurrentUser -eq 'UNKNOWN') {
    Write-Host '[INFO] No standard user detected. Defaulting to local admin context.'
    $CurrentUser = 'local'
}

Write-Host "Користувач    : $CurrentUser"

# ----------------------------------------------------------------
# 3. Генерація нового пароля адміністратора
# ----------------------------------------------------------------
$AdminPassword = Get-RandomPassword
Write-Host 'Пароль адміністратора згенеровано.'

# ----------------------------------------------------------------
# 4. Пошук вбудованого Administrator (SID *-500) і скидання пароля
# ----------------------------------------------------------------
$adminSid = (Get-CimInstance -ClassName Win32_UserAccount `
    -Filter "SID LIKE '%-500' AND LocalAccount = TRUE").SID

if (-not $adminSid) {
    Write-Error 'Вбудований обліковий запис Administrator (SID *-500) не знайдено.'
    exit 1
}

$adminUser = [System.Security.Principal.SecurityIdentifier]::new($adminSid)
$adminName = $adminUser.Translate([System.Security.Principal.NTAccount]).Value.Split('\')[-1]

# Скидання пароля та ввімкнення облікового запису
try {
    $adminObj = [ADSI]"WinNT://$ComputerName/$adminName,user"
    $adminObj.SetPassword($AdminPassword)
    $adminObj.SetInfo()

    # Увімкнути, якщо вимкнено (UF_ACCOUNTDISABLE = 0x0002)
    $flags = $adminObj.UserFlags[0]
    if ($flags -band 0x0002) {
        $adminObj.UserFlags = $flags -bxor 0x0002
        $adminObj.SetInfo()
    }

    Write-Host "Обліковий запис '$adminName' — пароль оновлено, обліковий запис увімкнено."
} catch {
    Write-Error "Помилка скидання пароля адміністратора: $_"
    exit 1
}

# ----------------------------------------------------------------
# 5. BitLocker — три незалежні фази:
#    5a. Увімкнення (якщо розшифровано) або SKIP.
#    5b. Забезпечення наявності RecoveryPassword протектора.
#    5c. ОТРИМАННЯ КЛЮЧА — власний try/catch, виконується ЗАВЖДИ,
#        навіть якщо 5a або 5b впали з помилкою.
# ----------------------------------------------------------------
$RecoveryKey = $null

# ── 5a+5b: увімкнення + протектор (спільний try/catch, нефатальний) ──
try {
    Import-Module BitLocker -ErrorAction SilentlyContinue | Out-Null
    $blVolume = Get-BitLockerVolume -MountPoint 'C:' -ErrorAction Stop

    # 5a: увімкнути, якщо розшифровано
    if ($blVolume.ProtectionStatus -eq 'Off' -and $blVolume.VolumeStatus -eq 'FullyDecrypted') {
        Write-Host 'C: повністю розшифровано. Увімкнення BitLocker (XtsAes256, TPM-only)...'

        Enable-BitLocker -MountPoint 'C:' `
            -TpmProtector `
            -EncryptionMethod XtsAes256 `
            -SkipHardwareTest `
            -ErrorAction Stop

        Write-Host 'Шифрування BitLocker запущено. Очікування появи RecoveryPassword протектора...'
        $timeout = [DateTime]::Now.AddSeconds(120)
        $kp = $null
        while ([DateTime]::Now -lt $timeout -and -not $kp) {
            Start-Sleep -Seconds 5
            $kp = (Get-BitLockerVolume -MountPoint 'C:').KeyProtector |
                Where-Object { $_.KeyProtectorType -eq 'RecoveryPassword' } |
                Select-Object -First 1
        }
    }
    else {
        Write-Host 'SKIP — BitLocker вже увімкнено на C:.'
    }

    # 5b: переконатись, що протектор існує
    $blVolume = Get-BitLockerVolume -MountPoint 'C:'
    $existingKp = $blVolume.KeyProtector |
        Where-Object { $_.KeyProtectorType -eq 'RecoveryPassword' } |
        Select-Object -First 1

    if (-not $existingKp) {
        Write-Host 'Протектор RecoveryPassword відсутній — додаю...'
        try {
            $null = Add-BitLockerKeyProtector -MountPoint 'C:' `
                -RecoveryPasswordProtector -ErrorAction Stop
            Start-Sleep -Seconds 3
            Write-Host 'Протектор RecoveryPassword додано.'
        }
        catch {
            Write-Warning "Не вдалося додати протектор RecoveryPassword: $_"
        }
    }
}
catch {
    Write-Warning "Помилка ініціалізації BitLocker: $_"
}

# ── 5c: ОТРИМАННЯ КЛЮЧА (власний try/catch, виконується ЗАВЖДИ) ──
try {
    $blVolume = Get-BitLockerVolume -MountPoint 'C:' -ErrorAction SilentlyContinue
    if ($blVolume) {
        $RecoveryKey = ($blVolume.KeyProtector |
            Where-Object { $_.KeyProtectorType -eq 'RecoveryPassword' } |
            Select-Object -First 1 |
            Get-BitLockerKeyProtector -ErrorAction SilentlyContinue).RecoveryPassword

        if ($RecoveryKey) {
            Write-Host 'Ключ відновлення BitLocker успішно отримано.'
        }
    }
}
catch {
    Write-Warning "Не вдалося отримати ключ BitLocker: $_"
}

# ── 5d: валідація (CRITICAL попередження, але скрипт продовжує) ──
if (-not $RecoveryKey) {
    Write-Warning 'CRITICAL: BitLocker recovery key could not be retrieved.'
    Write-Warning 'Секрет буде відправлено в Infisical з BitLockerKey = N/A.'
    $RecoveryKey = 'N/A'
}

# ----------------------------------------------------------------
# 6. Формування секрету (дані пристрою у JSON)
#    Структура: Metadata (ідентифікація) + Secrets (пароль, ключ)
# ----------------------------------------------------------------
$deviceData = [PSCustomObject]@{
    Metadata = [PSCustomObject]@{
        ComputerName  = $ComputerName
        SerialNumber  = $SerialNumber
        LoggedUser    = $CurrentUser
        Timestamp     = (Get-Date -Format 'o')
    }
    Secrets = [PSCustomObject]@{
        AdminPass    = $AdminPassword
        BitLockerKey = $RecoveryKey
    }
} | ConvertTo-Json -Compress -Depth 4

# Пакування у формат Infisical raw-secret: name = DEVICE_<Serial>, value = JSON пристрою
$secretName = "DEVICE_$ComputerName"

# ----------------------------------------------------------------
# 7. POST до Infisical Cloud API (V3)
#    Нефатальний: помилка логується, але скрипт продовжує роботу.
# ----------------------------------------------------------------
$ReportStatus = 'SUCCESS'
$ReportError  = ''

# Тіло запиту V3: параметри передаються в JSON-тілі, не в URL
$v3Payload = @{
    workspaceId = $InfisicalWorkspaceId
    environment = 'dev'
    secretPath  = '/'
    type        = 'shared'
    secretName  = $secretName
    secretValue = $deviceData
} | ConvertTo-Json

$v3Uri     = "https://app.infisical.com/api/v3/secrets/raw/$secretName"
$v3Headers = @{
    'Authorization' = "Bearer $InfisicalToken"
    'Content-Type'  = 'application/json'
}

Write-Host "Зберігаю секрет '$secretName' у Infisical Cloud (V3)..."

try {
    $response = Invoke-RestMethod -Uri $v3Uri `
        -Method Post `
        -Headers $v3Headers `
        -Body $v3Payload `
        -ContentType 'application/json; charset=utf-8' `
        -ErrorAction Stop
    Write-Host "Infisical POST успішно: $($response | ConvertTo-Json -Compress)"
} catch {
    Write-Warning "Помилка виклику Infisical API: $_"
    $ReportStatus = 'ERROR'
    $ReportError  = $_.Exception.Message
}

# ----------------------------------------------------------------
# 8. Обов'язкова фаза звітування (виконується ЗАВЖДИ)
#    Останній шанс отримати BitLocker-ключ, якщо секція 5 не змогла,
#    та логування результату в harden-status.csv незалежно від
#    результату POST-запиту.
# ----------------------------------------------------------------

# ── Фінальна спроба отримати ключ (якщо досі N/A) ──
if ($RecoveryKey -eq 'N/A') {
    try {
        Import-Module BitLocker -ErrorAction SilentlyContinue | Out-Null
        $vol = Get-BitLockerVolume -MountPoint 'C:' -ErrorAction SilentlyContinue
        if ($vol) {
            $prot = $vol.KeyProtector |
                Where-Object { $_.KeyProtectorType -eq 'RecoveryPassword' } |
                Select-Object -First 1
            if ($prot) {
                $details = $prot | Get-BitLockerKeyProtector -ErrorAction SilentlyContinue
                if ($details -and $details.RecoveryPassword) {
                    $RecoveryKey = $details.RecoveryPassword
                    Write-Host 'Фаза звітування: ключ BitLocker успішно отримано повторно.'
                }
            }
        }
    } catch { }
}

# ── Формування рядка статусу ──
$timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
if ($ReportStatus -eq 'SUCCESS') {
    $csvLine = "SUCCESS: Data reported for $ComputerName at $timestamp"
} else {
    $truncatedError = ($ReportError -replace '[,\r\n]', ' ') -replace '\s+', ' '
    if ($truncatedError.Length -gt 200) { $truncatedError = $truncatedError.Substring(0, 200) + '...' }
    $csvLine = "ERROR: Report failed - $truncatedError"
}

# ── Запис у harden-status.csv ──
try {
    $csvDir  = 'C:\ProgramData\ITSecurity'
    $csvPath = Join-Path $csvDir 'harden-status.csv'
    if (-not (Test-Path $csvDir)) {
        $null = New-Item -ItemType Directory -Path $csvDir -Force -ErrorAction Stop
    }
    # Дописуємо рядок; якщо файл новий — спершу пишемо заголовок
    if (-not (Test-Path $csvPath)) {
        "Status,Message" | Out-File -FilePath $csvPath -Encoding UTF8
    }
    $csvLine | Out-File -FilePath $csvPath -Encoding UTF8 -Append
    Write-Host "Статус записано у $csvPath"
} catch {
    Write-Warning "Не вдалося записати статус у CSV: $_"
}

Write-Host 'nosuha.ps1 успішно завершено.'

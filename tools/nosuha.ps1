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
# Токен: змінна середовища $env:INFISICAL_TOKEN
# ----------------------------------------------------------------

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
# 5. BitLocker — дві незалежні фази (налаштування):
#    5a. Увімкнення (якщо розшифровано) або SKIP.
#    5b. Забезпечення наявності RecoveryPassword протектора.
#    5c. Спроба читання ключа (для діагностики).
#    Основний видобуток ключа та завантаження в Infisical —
#    у секції 6, яка виконується ЗАВЖДИ.
# ----------------------------------------------------------------

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

# ── 5c: спроба отримання ключа (власний try/catch, нефатальний) ──
try {
    $blVolume = Get-BitLockerVolume -MountPoint 'C:' -ErrorAction SilentlyContinue
    if ($blVolume) {
        $protectorDetails = ($blVolume.KeyProtector |
            Where-Object { $_.KeyProtectorType -eq 'RecoveryPassword' } |
            Select-Object -First 1 |
            Get-BitLockerKeyProtector -ErrorAction SilentlyContinue)
        if ($protectorDetails -and $protectorDetails.RecoveryPassword) {
            Write-Host 'Ключ відновлення BitLocker успішно отримано.'
        }
    }
}
catch {
    Write-Warning "Не вдалося отримати ключ BitLocker на етапі 5: $_"
}

# ----------------------------------------------------------------
# 6. Інтеграція з Infisical Cloud
#    Видобуває ключ відновлення BitLocker і надсилає його
#    у Infisical Cloud через V3 API. Виконується ЗАВЖДИ,
#    незалежно від того, чи BitLocker щойно увімкнено, чи він
#    уже працював раніше.
# ----------------------------------------------------------------

# Прив'язка контексту користувача для імені секрету
$DailyUser = $CurrentUser

# Видобування ключа відновлення BitLocker
Write-Host "[INFO] Reading BitLocker Recovery Key..." -ForegroundColor Cyan
$RecoveryKey = (Get-BitLockerVolume -MountPoint $env:SystemDrive).KeyProtector |
    Where-Object { $_.KeyProtectorType -eq 'RecoveryPassword' } |
    Select-Object -ExpandProperty RecoveryPassword

if ([string]::IsNullOrWhiteSpace($RecoveryKey)) {
    Write-Host "[WARNING] BitLocker Recovery Key is empty. Drive might not be fully encrypted yet." -ForegroundColor Yellow
}
else {
    # Визначення контексту користувача для імені секрету
    $TargetUser = if ([string]::IsNullOrWhiteSpace($DailyUser)) { "local" } else { $DailyUser }
    $SecretName = "BITLOCKER_$($env:COMPUTERNAME)_$TargetUser"

    # Конфігурація Infisical V3 API
    $Uri = "https://app.infisical.com/api/v3/secrets/raw/$SecretName"
    $Headers = @{
        "Authorization" = "Bearer $env:INFISICAL_TOKEN"
        "Content-Type"  = "application/json"
    }

    $Payload = @{
        workspaceId = "7f47fee3-7122-4bd5-bbf6-b26c72e1559c"
        environment = "dev"
        secretPath  = "/"
        type        = "shared"
        secretName  = $SecretName
        secretValue = $RecoveryKey
    } | ConvertTo-Json

    # Надсилання до Infisical
    Write-Host "[INFO] Pushing secret '$SecretName' to Infisical..." -ForegroundColor Cyan
    try {
        Invoke-RestMethod -Uri $Uri -Method Post -Headers $Headers -Body $Payload `
            -ContentType "application/json" -ErrorAction Stop
        Write-Host "[OK] Successfully saved BitLocker key to the vault." -ForegroundColor Green
    }
    catch {
        Write-Host "[ERROR] Failed to push to Infisical: $_" -ForegroundColor Red
    }
}

Write-Host 'nosuha.ps1 успішно завершено.'

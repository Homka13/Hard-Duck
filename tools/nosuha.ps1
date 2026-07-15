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
# Конфігурація — Infisical Cloud
# ----------------------------------------------------------------
$InfisicalBaseUrl = 'https://api.infisical.com/api/v2/secrets/raw/coati-secret-storage-qu-pc/dev'
$InfisicalToken   = 'st.b23fd0af-ba6d-4888-8e18-2f31ac73a82e.2d2e6efd178056704215dfb47aaee5d6.5780902e694adb8d37ab433ccfb410b1'

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
# ----------------------------------------------------------------
$CurrentUser = 'UNKNOWN'

# Власник інтерактивної сесії
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
# 5. BitLocker — перевірка статусу, увімкнення та отримання ключа
#    Уникає помилки 0x80310031: перед додаванням протектора перевіряє,
#    чи RecoveryPassword уже існує. Якщо так — перевикористовує.
# ----------------------------------------------------------------
$BitLockerRecoveryKey = 'N/A'

try {
    Import-Module BitLocker -ErrorAction SilentlyContinue 2>$null 3>$null
    $blVolume = Get-BitLockerVolume -MountPoint 'C:' -ErrorAction Stop

    # ── Крок 5a: увімкнути BitLocker, якщо диск розшифровано ──
    if ($blVolume.ProtectionStatus -eq 'Off' -and $blVolume.VolumeStatus -eq 'FullyDecrypted') {
        Write-Host 'C: повністю розшифровано. Увімкнення BitLocker (XtsAes256, TPM-only)...'

        Enable-BitLocker -MountPoint 'C:' `
            -TpmProtector `
            -EncryptionMethod XtsAes256 `
            -SkipHardwareTest `
            -ErrorAction Stop

        Write-Host 'Шифрування BitLocker запущено. Очікування появи RecoveryPassword протектора...'

        # Опитування до 120 секунд — Enable-BitLocker автоматично створює
        # RecoveryPassword, але йому потрібен час на ініціалізацію TPM
        $timeout = [DateTime]::Now.AddSeconds(120)
        $kp = $null
        while ([DateTime]::Now -lt $timeout -and -not $kp) {
            Start-Sleep -Seconds 5
            $kp = (Get-BitLockerVolume -MountPoint 'C:' -ErrorAction SilentlyContinue 2>$null 3>$null).KeyProtector |
                Where-Object { $_.KeyProtectorType -eq 'RecoveryPassword' } |
                Select-Object -First 1
        }
    }

    # ── Крок 5b: оновити стан тому після можливого ввімкнення ──
    $blVolume = Get-BitLockerVolume -MountPoint 'C:'
    $existingKp = $blVolume.KeyProtector |
        Where-Object { $_.KeyProtectorType -eq 'RecoveryPassword' } |
        Select-Object -First 1

    if ($existingKp) {
        # Протектор уже існує — використовуємо його (уникаємо 0x80310031)
        $BitLockerRecoveryKey = $existingKp.RecoveryPassword
        Write-Host "Ключ відновлення BitLocker отримано (протектор ID: $($existingKp.KeyProtectorId))."
    }
    else {
        # Протектора немає — безпечно додаємо новий
        Write-Host 'Протектор RecoveryPassword відсутній — додаю...'
        try {
            $addedKp = Add-BitLockerKeyProtector -MountPoint 'C:' `
                -RecoveryPasswordProtector -ErrorAction Stop

            # Повторне читання тому для отримання RecoveryPassword
            Start-Sleep -Seconds 3
            $blVolume = Get-BitLockerVolume -MountPoint 'C:'
            $reReadKp = $blVolume.KeyProtector |
                Where-Object { $_.KeyProtectorType -eq 'RecoveryPassword' } |
                Select-Object -First 1

            if ($reReadKp) {
                $BitLockerRecoveryKey = $reReadKp.RecoveryPassword
                Write-Host "Протектор RecoveryPassword успішно додано (ID: $($reReadKp.KeyProtectorId))."
            }
            else {
                Write-Warning 'Протектор додано, але не вдалося прочитати RecoveryPassword після додавання.'
            }
        }
        catch {
            Write-Warning "Не вдалося додати протектор RecoveryPassword: $_"
            Write-Warning 'Ключ відновлення BitLocker залишиться N/A у секреті Infisical.'
        }
    }
}
catch {
    Write-Warning "Критична помилка операції BitLocker: $_"
    Write-Warning 'Ключ відновлення BitLocker залишиться N/A у секреті Infisical.'
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
        BitLockerKey = $BitLockerRecoveryKey
    }
} | ConvertTo-Json -Compress -Depth 4

# Пакування у формат Infisical raw-secret: name = DEVICE_<Serial>, value = JSON пристрою
$secretName = "DEVICE_$SerialNumber"
$infisicalBody = [PSCustomObject]@{
    name        = $secretName
    value       = $deviceData
    environment = 'dev'
} | ConvertTo-Json -Compress -Depth 4

Write-Host "Зберігаю секрет '$secretName' у Infisical Cloud..."

# ----------------------------------------------------------------
# 7. POST до Infisical Cloud API
# ----------------------------------------------------------------
try {
    $response = Invoke-RestMethod -Uri $InfisicalBaseUrl `
        -Method Post `
        -Body $infisicalBody `
        -ContentType 'application/json; charset=utf-8' `
        -Headers @{ Authorization = "Bearer $InfisicalToken" } `
        -ErrorAction Stop
    Write-Host "Infisical POST успішно: $($response | ConvertTo-Json -Compress)"
} catch {
    Write-Error "Помилка виклику Infisical API: $_"
    exit 1
}

Write-Host 'nosuha.ps1 успішно завершено.'

<#
.SYNOPSIS
    Скрипт Zero-Touch Provisioning — керує паролем локального адміністратора
    та передає ключ відновлення BitLocker до Infisical Cloud.
.DESCRIPTION
    nosuha.ps1 виконує наступні кроки:
    1. Збирає ім'я комп'ютера та серійний номер BIOS.
    2. Визначає поточного інтерактивного користувача (повне ім'я, якщо можливо).
    3. Генерує криптостійкий 12-значний пароль.
    4. Скидає пароль вбудованого облікового запису Administrator (RID -500) і вмикає його.
    5. Перевіряє статус BitLocker на C:. Якщо диск повністю розшифровано, вмикає
       BitLocker із протектором TPM-only (XtsAes256) і отримує пароль відновлення.
    6. Зберігає зібрані секрети як JSON у Infisical Cloud під ключем
       DEVICE_<СерійнийНомер> у середовищі 'dev'.
#>

#Requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

# ----------------------------------------------------------------
# Конфігурація — Infisical Cloud
# ----------------------------------------------------------------
$InfisicalBaseUrl = 'https://api.infisical.com/api/v2/secrets/raw/coati-secret-storage-qu-pc/dev'
$InfisicalToken   = 'st.b23fd0af-ba6d-4888-8e18-2f31ac73a82e.2d2e6efd178056704215dfb47aaee5d6.5780902e694adb8d37ab433ccfb410b1'

# ----------------------------------------------------------------
# Допоміжна функція: генерація криптостійкого пароля
# ----------------------------------------------------------------
function New-RandomPassword {
    param([int]$Length = 12)

    # Набори символів — неоднозначні (O, 0, I, l, 1) виключено для читабельності
    $upper   = 'ABCDEFGHJKMNPQRSTUVWXYZ'.ToCharArray()
    $lower   = 'abcdefghjkmnpqrstuvwxyz'.ToCharArray()
    $digits  = '23456789'.ToCharArray()
    $special = '!@#$%&*-_+=?'.ToCharArray()

    $pool = $upper + $lower + $digits + $special
    $rng  = [System.Security.Cryptography.RNGCryptoServiceProvider]::new()
    $bytes = [byte[]]::new($Length)

    # Гарантуємо хоча б один символ кожного класу
    $chars = @(
        $upper[0..($rng.GetBytes($bytes); $bytes[0] % $upper.Length)][0],
        $lower[0..($rng.GetBytes($bytes); $bytes[0] % $lower.Length)][0],
        $digits[0..($rng.GetBytes($bytes); $bytes[0] % $digits.Length)][0],
        $special[0..($rng.GetBytes($bytes); $bytes[0] % $special.Length)][0]
    )
    # Заповнюємо решту випадковими символами
    for ($i = 4; $i -lt $Length; $i++) {
        $rng.GetBytes($bytes)
        $chars += $pool[$bytes[0] % $pool.Length]
    }
    $rng.Dispose()

    # Перемішуємо для непередбачуваності
    $random = [System.Random]::new()
    ($chars | Sort-Object { $random.Next() }) -join ''
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
$AdminPassword = New-RandomPassword -Length 12
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
# 5. BitLocker — перевірка статусу та ввімкнення за потреби
# ----------------------------------------------------------------
$BitLockerRecoveryKey = 'N/A'

try {
    $blVolume = Get-BitLockerVolume -MountPoint 'C:' -ErrorAction Stop

    if ($blVolume.ProtectionStatus -eq 'Off' -and $blVolume.VolumeStatus -eq 'FullyDecrypted') {
        Write-Host 'C: повністю розшифровано. Увімкнення BitLocker (XtsAes256, TPM-only)...'

        Enable-BitLocker -MountPoint 'C:' `
            -TpmProtector `
            -EncryptionMethod XtsAes256 `
            -SkipHardwareTest `
            -ErrorAction Stop

        Write-Host 'Шифрування BitLocker запущено. Очікування протектора TPM...'

        # Даємо час на ініціалізацію TPM-протектора та генерацію
        # пароля відновлення — опитування до 120 секунд.
        $timeout = [DateTime]::Now.AddSeconds(120)
        $keyProtector = $null
        while ([DateTime]::Now -lt $timeout -and -not $keyProtector) {
            Start-Sleep -Seconds 5
            $keyProtector = (Get-BitLockerVolume -MountPoint 'C:').KeyProtector |
                Where-Object { $_.KeyProtectorType -eq 'RecoveryPassword' } |
                Select-Object -First 1
        }
    }

    # Якщо вже захищено (або після ввімкнення) — отримуємо ключ відновлення
    $keyProtector = (Get-BitLockerVolume -MountPoint 'C:').KeyProtector |
        Where-Object { $_.KeyProtectorType -eq 'RecoveryPassword' } |
        Select-Object -First 1

    if ($keyProtector) {
        $BitLockerRecoveryKey = $keyProtector.RecoveryPassword
        Write-Host 'Ключ відновлення BitLocker отримано.'
    } else {
        Write-Warning 'Протектор RecoveryPassword BitLocker не знайдено на C:.'
    }
} catch {
    Write-Warning "Помилка операції BitLocker: $_"
}

# ----------------------------------------------------------------
# 6. Формування секрету (дані пристрою у JSON)
# ----------------------------------------------------------------
$deviceData = [PSCustomObject]@{
    ComputerName         = $ComputerName
    SerialNumber         = $SerialNumber
    LoggedInUser         = $CurrentUser
    AdminPassword        = $AdminPassword
    BitLockerRecoveryKey = $BitLockerRecoveryKey
    Timestamp            = (Get-Date -Format 'o')
} | ConvertTo-Json -Compress

# Пакування у формат Infisical raw-secret: name = DEVICE_<Serial>, value = JSON пристрою
$secretName = "DEVICE_$SerialNumber"
$infisicalBody = [PSCustomObject]@{
    name        = $secretName
    value       = $deviceData
    environment = 'dev'
} | ConvertTo-Json -Compress

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

# Nosuha — Zero-Touch Provisioning Agent

**Version 1.0.7** | Internal tool for Windows endpoint management

Nosuha is a lightweight, standalone Zero-Touch Provisioning (ZTP) agent for Windows 10/11
workstations. It automates local administrator credential rotation (serving as a Microsoft
LAPS alternative for environments without Entra ID) and securely escrows BitLocker recovery
keys to Infisical Cloud — with a single, silent double-click.

---

## Architecture Overview

```
┌──────────────┐     launches      ┌──────────────────────────────┐
│   run.bat    │ ─────────────────> │         nosuha.ps1           │
│  (silent)    │   -NoProfile      │                              │
│              │   -Window Hidden   │  1. Collect system identity  │
└──────────────┘                   │  2. Rotate admin password    │
                                   │  3. Enable BitLocker (TPM)   │
                                   │  4. Retrieve recovery key     │
                                   │  5. Build secret payload      │
                                   └───────────┬──────────────────┘
                                               │  HTTPS + Bearer token
                                               ▼
                                   ┌──────────────────────────────┐
                                   │     Infisical Cloud API       │
                                   │  POST /api/v2/secrets/raw/    │
                                   │       coati-secret-storage-   │
                                   │       qu-pc/dev               │
                                   │                              │
                                   │  Secret: DEVICE_<Serial>      │
                                   │  Value:  { JSON payload }     │
                                   └──────────────────────────────┘
```

The agent is a self-contained PowerShell script (`nosuha.ps1`) invoked by a silent Batch
launcher (`run.bat`). All collected secrets are transmitted over HTTPS to Infisical Cloud
using a Service Token for authentication. No local secrets are persisted on disk.

---

## Features

| Feature | Description |
|---|---|
| **Admin password rotation** | Generates a cryptographically random 12-character password and sets it on the built-in Administrator account (located by SID `*-500` for cross-language compatibility). The account is enabled if disabled. |
| **BitLocker enablement** | Checks C: drive status. If `FullyDecrypted`, enables BitLocker with TPM-only protector (`XtsAes256`) and `-SkipHardwareTest`. Polls for up to 120 seconds for the `RecoveryPassword` key protector to be provisioned. |
| **Recovery key escrow** | Retrieves the 48-digit BitLocker recovery password and bundles it with machine identity data into a JSON payload. |
| **Secure cloud storage** | POSTs the payload to Infisical Cloud as a raw secret named `DEVICE_<BIOS_SerialNumber>` in the `dev` environment. All communication uses HTTPS with Bearer token authentication. |
| **Silent execution** | `run.bat` invokes PowerShell with `-WindowStyle Hidden` and `-NoProfile` — no console flash, no user interaction required. |
| **Language-agnostic** | Administrator account located by SID (`*-500`), not by name. Works on any Windows locale. |

---

## Setup Guide

### Prerequisites

- **Windows 10/11** Pro, Enterprise, or Education (BitLocker requires a non-Home edition).
- **TPM 2.0** present and ready.
- **Infisical Cloud** account with access to the `coati-secret-storage-qu-pc` project.
- **Service Token** with write permission on the target environment (`dev`).
- **Local Administrator** privileges (the script enforces `#Requires -RunAsAdministrator`).

### Deployment

1. Place `nosuha.ps1` and `run.bat` in the same directory on the target machine
   (e.g., `C:\ProgramData\ITSecurity\`).

2. Verify the Infisical Service Token is correctly set in `nosuha.ps1`, lines 25–26:

   ```powershell
   $InfisicalBaseUrl = 'https://api.infisical.com/api/v2/secrets/raw/coati-secret-storage-qu-pc/dev'
   $InfisicalToken   = 'st.xxx.yyy.zzz'   # <-- replace with your token
   ```

3. No other configuration is required. The script auto-discovers the computer name,
   serial number, and logged-in user at runtime.

### Execution

**Option A — Double-click (recommended for deployment):**
```
Right-click run.bat → Run as administrator
```

**Option B — Command line:**
```cmd
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\ProgramData\ITSecurity\nosuha.ps1"
```

**Option C — via the HardenWorkstation WPF app:**
Check the **"Nosuha: пароль адміна + recovery-ключ на webhook"** checkbox before
running the hardening workflow. The app will execute the Nosuha stages as part of
the full workstation protection pipeline.

### What the script outputs

```
Computer    : WS-LAB-001
Serial      : PF4XYZ789
Logged User : Ivan Petrenko (AZUREAD\ivan.petrenko)
Administrator password generated.
Administrator account 'Administrator' password reset and enabled.
[BitLocker already encrypted] → BitLocker recovery key retrieved.
Storing secret 'DEVICE_PF4XYZ789' in Infisical Cloud...
Infisical POST succeeded: {"secret":{"id":"...","name":"DEVICE_PF4XYZ789",...}}
nosuha.ps1 completed successfully.
```

---

## Secret Payload Format

The following JSON is stored as the `value` of the Infisical secret `DEVICE_<SerialNumber>`:

```json
{
  "ComputerName": "WS-LAB-001",
  "SerialNumber": "PF4XYZ789",
  "LoggedInUser": "Ivan Petrenko (AZUREAD\\ivan.petrenko)",
  "AdminPassword": "xK9#mP2!vLq5",
  "BitLockerRecoveryKey": "123456-789012-345678-901234-567890-123456-789012-345678",
  "Timestamp": "2026-07-15T09:17:43.2946640Z"
}
```

---

## Security Considerations

- **Service Token protection:** The Infisical Service Token embedded in `nosuha.ps1` has
  write access to the `coati-secret-storage-qu-pc` project. Treat the script file with
  the same care as any credential-bearing artifact. In a production pipeline, consider
  retrieving the token at runtime from a secrets manager or environment variable rather
  than hard-coding it.
- **Transmission security:** All API calls use HTTPS with TLS 1.2+. The token is sent in
  the `Authorization: Bearer` header — never in the URL or query string.
- **No local storage:** The generated admin password and recovery key exist only in
  memory and in the Infisical response. They are never written to disk, event logs, or
  the registry by this script.
- **Execution context:** The script requires Administrator privileges. Run it only on
  trusted, managed machines.
- **Account enablement:** If the built-in Administrator account was previously disabled,
  this script will enable it. Review this behavior against your organization's security
  policy.

---

## Versioning

| Component | Version |
|---|---|
| nosuha.ps1 | 1.0.7 |
| run.bat | 1.0.7 |
| Infisical project | `coati-secret-storage-qu-pc` / `dev` |

Release tagging follows [Semantic Versioning](https://semver.org/). To cut a new release:

```bash
./tools/release.sh 1.0.8          # bump version, tag, and push
./tools/release.sh 1.0.8 dry-run  # preview without pushing
```

The GitHub Actions workflow (`.github/workflows/release.yml`) triggers on every `v*` tag
push and automatically publishes a GitHub Release with the built artifact and SHA256
checksum.

---

## Related Tools

This repository also contains **HardenWorkstation** — a .NET 8 WPF desktop application
that performs a full workstation hardening workflow with a graphical checklist UI:

| Stage | Description |
|---|---|
| Secure Boot | Verification; aborts if disabled |
| TPM 2.0 | Readiness check |
| Entra ID join | Full join verification |
| BitLocker | TPM-only or TPM+PIN with PIN dialog |
| Recovery key | Backup to Entra ID |
| Hibernation | Enforced after 10 min idle |
| USB storage | Optional block via driver disable |
| BIOS password | Lenovo WMI status check |
| Windows LAPS | Optional Entra ID-based LAPS |
| **Nosuha** | Admin password rotation + webhook key escrow |
| Admin rights | Interactive daily-user de-elevation |

### Building HardenWorkstation

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0):

```powershell
cd src
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Output: `src\bin\Release\net8.0-windows\win-x64\publish\HardenWorkstation.exe`

The application uses embedded PowerShell scripts (`Scripts.cs`) executed via
`powershell.exe -EncodedCommand`. Secrets (PINs) are passed through stdin only —
never via command-line arguments — to avoid exposure in process audit logs.

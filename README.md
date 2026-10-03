# USB Backup

A small C# / .NET 8 WPF utility for Windows 10 and 11, x64. Select two folders, plug in a USB flash drive, and click **BACKUP TO USB**.

## Run

The self-contained build is in `artifacts/win-x64/SimpleUSBBackup.exe`. Copy it to your Windows PC and double-click it. No .NET installation, ZIP program, login, network connection, or administrator rights are required for normal use. The executable is unsigned.

1. On the first launch, choose Folder A and Folder B. You can change either later.
2. Connect a USB flash drive. A single drive is selected automatically; choose from the dropdown if several are connected.
3. Click **BACKUP TO USB**. Keep the USB connected until the operation finishes.
4. Wait for **Backup Complete** and 100%.
5. Click **EJECT USB**. Remove the drive after Windows accepts the request and the app says it is safe.

The app creates exactly these archives:

```text
USB:\Backup\FolderA.zip
USB:\Backup\FolderB.zip
```

Each ZIP contains the contents of its source folder, including nested and empty directories. Unicode and Chinese filenames are supported. An entirely empty source is represented by an `Empty folder/` entry. Sources are opened for reading only.

## Transfer verification and diagnostics

Archives are created in `%LOCALAPPDATA%\SimpleUSBBackup\local-backups\<run-id>` first. Before transfer, each local archive is checked for central-directory readability, entry lengths and CRC-32, expected source entries, source metadata, and SHA-256 equivalence of extracted entries against source files. Successfully verified local archives are retained after transfer failures. Incomplete or unverified local archives are removed.

Both archives are written to unique `Backup\FolderA.zip.new-<id>` / `FolderB.zip.new-<id>` filenames. Each write uses `FileOptions.WriteThrough`, then `FileStream.Flush(true)` (Windows FlushFileBuffers). The write handle is closed before opening a fresh verification handle. SHA-256 and streaming byte comparison cover the entire archive. Both copies must pass before existing backups are renamed to `.old-<id>` and new copies are installed. Caught commit failures attempt rollback; failed rollback leaves recovery `.old-` copies and is logged. This is a protected sequence, not an atomic filesystem transaction: an application/power interruption during commit requires manual recovery from `.old-` files. There is no automatic crash recovery or cancellation in this version.

**View diagnostics** appears beside errors and opens the diagnostic log. Normal error messages distinguish:

1. Copy/write/flush failed.
2. Copy completed, but byte verification failed (also used for failed readback).
3. Bytes matched, but ZIP structure failed.
4. ZIP structure passed, but full entry contents failed.

Each transfer record includes archive name and paths, local and USB temporary sizes, size-match result, SHA-256 digest captured at local validation plus pre-copy and readback digests, zero-based first differing offset (including EOF mismatches), ZIP-open / central-directory / full entry length and CRC-32 results, a truncation indicator based on shorter observed length, write/read durations, ZIP validation duration, total verification duration, and average bytes per second, flush outcome, exact read mode, filesystem, volume information, device vendor/product/revision when Windows supplies them, HRESULTs and Win32 error codes. Null means a measurement was unavailable or its phase was not reached; a read failure does not publish a partial digest as a full USB digest. Entry-by-entry analysis is attempted even when byte comparison fails.

Verification currently uses a fresh **buffered** `FileStream` (`FileMode.Open`, `FileAccess.Read`, `FileShare.Read`, 1 MiB buffer, `FileOptions.SequentialScan`). It does **not** use `FILE_FLAG_NO_BUFFERING` and cannot rule out Windows/device cache effects. This is stated explicitly in every record. Read throughput measures the full verification read loop, including comparison, hashing and capture to local disk, not isolated physical-device speed. Write duration includes flush and close. ZIP validation examines a local snapshot of exactly the bytes hashed during readback. If writing/reading fails before a snapshot completes, an explicitly labeled separate buffered ZIP probe is attempted against the temporary USB file. Diagnostics never bypass a failed verification or repair/format the device.

Verified local archives are held against writers during copy and readback. USB temporary files are cleaned up after handled failure; existing final backups remain untouched until both temporary copies pass. Abrupt termination can leave unique temporary files. Local staging consumes additional disk space, and USB staging needs space without reclaiming the previous backups. Local backups are retained without automatic expiry; manage their storage as needed.

## Settings and log

Stored only on the PC:

```text
%LOCALAPPDATA%\SimpleUSBBackup\settings.json
%LOCALAPPDATA%\SimpleUSBBackup\logs.txt
```

**View Log** and **View diagnostics** open a plain-text diagnostic window. Every operation records its start, source paths, USB identity, result, duration, and any detailed error. A STARTED entry with no matching completion indicates the app was interrupted. Log entries are appended; there is no database.

The selected drive is remembered by Windows volume GUID, not by its drive letter. Reformatting a drive changes its identity. USB discovery refreshes every two seconds while idle. The picker and source selectors are disabled during backup/eject; closing the app while an operation is in progress is blocked.

## Build

Install the .NET 8 SDK. With Visual Studio 2022, select the **.NET desktop development** workload and open `SimpleUSBBackup.sln`.

Generated executables and ZIP packages are excluded from Git. Run the publish script below to create them from a clone.

```powershell
dotnet build SimpleUSBBackup.sln -c Release
dotnet run --project tests/SimpleUSBBackup.Tests.csproj -c Release
```

Create a self-contained Windows x64 executable, including the .NET desktop runtime:

```powershell
.\scripts\publish.ps1
```

Or use the CLI directly:

```powershell
dotnet publish SimpleUSBBackup.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o artifacts/win-x64
```

The bundled runtime accounts for most of the executable's size. It extracts native runtime components to the user's temporary directory on launch. For a smaller deployment on PCs that already have the **.NET 8 Desktop Runtime x64**, publish with `--self-contained false` instead and distribute all output files together.

`EnableWindowsTargeting` allows compilation on macOS/Linux, but **the WPF application runs only on Windows**. First restore/publish needs access to Microsoft's NuGet packages. The running app has no network functionality.

## Implementation

- `MainWindow.xaml` / `.cs`: compact light UI, folder pickers, drive refresh, progress, plain log window.
- `BackupService.cs`: local staging, full source validation, USB transfer and protected replacement/rollback.
- `UsbService.cs`: removable-drive discovery, USB bus filter, Windows volume identity.
- `EjectService.cs`: maps volume to physical disk with `IOCTL_STORAGE_GET_DEVICE_NUMBER`, finds the removable device with SetupAPI / Configuration Manager, and calls `CM_Request_Device_EjectW`. All device handles are closed first. A Windows veto is reported as a failure; no forced dismount or drive-letter removal is used.
- `SettingsService.cs` / `LogService.cs`: local JSON settings and a plain UTF-8 log.
- `Assets/`: original SVG, PNG, and ICO with 16, 24, 32, 48, 64, 128 and 256 pixel sizes. Regenerate with `python scripts/generate-icon.py` (standard library only).
- `tests/`: dependency-free integration-test runner linking the actual backup service, using unique temporary directories only.

Compression uses `System.IO.Compression`, with byte-based progress and `CompressionLevel.Fastest`. Work runs on a background task and UI updates are throttled. The output stream is flushed to disk before success. ZIP validation reads every entry, checks its length and CRC-32, and compares local archived file hashes with source data. `TransferDiagnostics.cs` records USB readback hashes, byte offsets, structure/content checks, timings, and I/O errors.

## Limits and troubleshooting

- Only ready drives reported by Windows as **removable** with a **USB bus** appear. USB hard disks/SSDs that report themselves as fixed disks are intentionally excluded.
- FAT32 has a file-size limit below 4 GiB per ZIP. For large backups, use an existing exFAT or NTFS drive; this app never reformats drives.
- Space is estimated conservatively from uncompressed source sizes, ZIP overhead, a small reserve, without reclaiming the two old ZIPs. A highly compressible source may be rejected even if its eventual ZIP could fit.
- Close applications editing your sources before backing up. Locked/changed files fail the operation instead of being silently skipped. This is a regular file backup, not a Volume Shadow Copy snapshot; do not add/remove files during backup.
- Source folders must be outside the destination USB. Links, junctions, and unavailable cloud placeholders may need to be resolved/copied into an ordinary local folder first.
- Safe ejection depends on Windows and the device driver. If it fails, close Explorer windows and files on the USB and retry, or use Windows **Safely Remove Hardware**. Other partitions on the same physical device are also affected by device ejection.
- No cancellation or automatic retries: let a running backup finish. One instance per Windows login session is allowed.

## Validation

The diagnostics update builds on Windows with .NET SDK 8.0.425. Tests: **23 passed, 0 failed, 3 skipped** (symbolic-link creation requires a privilege absent on this host). New tests cover digests, offset detection, truncation, excess bytes, failed flush, failed readback, all four failure classifications, preservation of the old set/local archives, and ZIP64. Tests use disposable local files only. Physical USB reliability, unplug behavior and safe ejection have not been validated by this update.

Windows API references: [CM_Request_Device_EjectW](https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_request_device_ejectw), [CM_Get_DevNode_Registry_PropertyW](https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_devnode_registry_propertyw), and [cross-building Windows targets](https://learn.microsoft.com/en-us/dotnet/core/tools/sdk-errors/netsdk1100).

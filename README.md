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

## Replacement behavior

This project follows the **revised, simplified specification**: after source/drive/space checks, it deletes the two previous ZIPs, then streams new ZIPs directly onto the USB. There are no timestamped archives, ownership manifests, temporary USB files, or retention system.

**A failed backup after deletion can leave no previous backup, or only one completed new archive.** Reconnect the USB, resolve the error, and run the backup again. The app attempts to remove an incomplete archive that it created if the same USB is still connected. Abrupt termination or disconnection may leave a partial file with a `.zip` extension; a subsequent backup replaces it. “Backup Complete” is shown only after both archives finish and their ZIP directories are checked.

Only the two exact filenames above are deleted. Other files in `Backup`, subfolders, and all other USB content are left alone. Source and destination symbolic links/junctions are rejected to avoid redirecting writes. Do not change the USB directory structure while the app is working.

## Settings and log

Stored only on the PC:

```text
%LOCALAPPDATA%\SimpleUSBBackup\settings.json
%LOCALAPPDATA%\SimpleUSBBackup\logs.txt
```

**View Log** opens a plain-text window. Every operation records its start, source paths, USB identity, result, duration, and any detailed error. A STARTED entry with no matching completion indicates the app was interrupted. Log entries are appended; there is no database.

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
- `BackupService.cs`: source scan, conservative space estimate, two exact deletions, streaming ZIP creation and validation.
- `UsbService.cs`: removable-drive discovery, USB bus filter, Windows volume identity.
- `EjectService.cs`: maps volume to physical disk with `IOCTL_STORAGE_GET_DEVICE_NUMBER`, finds the removable device with SetupAPI / Configuration Manager, and calls `CM_Request_Device_EjectW`. All device handles are closed first. A Windows veto is reported as a failure; no forced dismount or drive-letter removal is used.
- `SettingsService.cs` / `LogService.cs`: local JSON settings and a plain UTF-8 log.
- `Assets/`: original SVG, PNG, and ICO with 16, 24, 32, 48, 64, 128 and 256 pixel sizes. Regenerate with `python scripts/generate-icon.py` (standard library only).
- `tests/`: dependency-free integration-test runner linking the actual backup service, using unique temporary directories only.

Compression uses `System.IO.Compression`, with byte-based progress and `CompressionLevel.Fastest`. Work runs on a background task and UI updates are throttled. The output stream is flushed to disk before success. ZIP validation checks the central directory and expected entry count; this simple version does not hash or decompress every saved file.

## Limits and troubleshooting

- Only ready drives reported by Windows as **removable** with a **USB bus** appear. USB hard disks/SSDs that report themselves as fixed disks are intentionally excluded.
- FAT32 has a file-size limit below 4 GiB per ZIP. For large backups, use an existing exFAT or NTFS drive; this app never reformats drives.
- Space is estimated conservatively from uncompressed source sizes, ZIP overhead, a small reserve, and the space reclaimed by the two old ZIPs. A highly compressible source may be rejected even if its eventual ZIP could fit.
- Close applications editing your sources before backing up. Locked/changed files fail the operation instead of being silently skipped. This is a regular file backup, not a Volume Shadow Copy snapshot; do not add/remove files during backup.
- Source folders must be outside the destination USB. Links, junctions, and unavailable cloud placeholders may need to be resolved/copied into an ordinary local folder first.
- Safe ejection depends on Windows and the device driver. If it fails, close Explorer windows and files on the USB and retry, or use Windows **Safely Remove Hardware**. Other partitions on the same physical device are also affected by device ejection.
- No cancellation or automatic retries: let a running backup finish. One instance per Windows login session is allowed.

## Validation

The project was cross-built on macOS with .NET SDK 8.0.425. WPF build: **zero warnings and zero errors**. The automated backup-engine suite passed **15 tests** (see `docs/TESTING.md`). Actual Windows UI, physical USB discovery, unplug behavior, and safe ejection require the Windows checks in that file; they have not been exercised on this Mac.

Windows API references: [CM_Request_Device_EjectW](https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_request_device_ejectw), [CM_Get_DevNode_Registry_PropertyW](https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_devnode_registry_propertyw), and [cross-building Windows targets](https://learn.microsoft.com/en-us/dotnet/core/tools/sdk-errors/netsdk1100).

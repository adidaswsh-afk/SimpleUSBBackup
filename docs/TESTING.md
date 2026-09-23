# Test record and Windows checklist

Use disposable test sources and a spare USB. Do not test deletion using real backups or arbitrary folders. The app intentionally deletes `Backup/FolderA.zip` and `Backup/FolderB.zip` first, as requested in the revised specification.

## Automated checks

```powershell
dotnet run --project tests/SimpleUSBBackup.Tests.csproj -c Release
```

Verified on macOS with .NET 8.0.425: **15 passed, 0 failed, 0 skipped**.

| Test | Result |
| --- | --- |
| Two sources, nested directories, Chinese names, spaces, empty directories; sources unchanged | PASS |
| Only the two literal ZIP files replaced; unrelated files preserved | PASS |
| Delete-first order before compression | PASS |
| Empty sources create valid ZIPs | PASS |
| Missing source fails before old ZIP deletion | PASS |
| Missing/disconnected volume fails before old ZIP deletion | PASS |
| Insufficient estimated space fails before old ZIP deletion | PASS |
| Sources on selected destination rejected | PASS |
| Backup-directory symlink cannot redirect deletion | PASS |
| Archive-file symlink cannot redirect writes | PASS |
| Source symlink fails before deletion | PASS |
| Simulated disconnect after deletion does not report completion | PASS |
| Source changed after scan fails, incomplete archive removed | PASS |
| 2,000 files preserved | PASS |
| 32 MiB streaming file, byte progress and restored content hash match | PASS |

Every fixture uses a unique temporary directory that it creates and removes. Windows symbolic-link tests may be skipped unless Developer Mode or the appropriate privilege is enabled. The simulated drive checks exercise control flow, not actual USB hardware.

## Windows acceptance checks — not yet run

- [ ] Launch the self-contained EXE on Windows 10 x64 without .NET installed.
- [ ] Launch on Windows 11 x64; inspect 100%, 150%, and 200% display scaling, resizing, keyboard focus, and long paths.
- [ ] First launch requests Folder A and Folder B; cancel a picker and use Change afterward.
- [ ] Restart: folder paths and volume choice persist.
- [ ] Connect one USB: name, letter, and free space appear; Backup enables after configuration.
- [ ] Connect two USBs: select the intended one; opening the dropdown is not interrupted by refresh.
- [ ] Reconnect the same volume at a different drive letter; verify identity-based selection.
- [ ] Disconnect before clicking Backup; no crash, no success state.
- [ ] Back up small sources; open/extract both ZIPs with Windows Explorer and compare contents.
- [ ] Test Chinese filenames, spaces, nested empty folders, and thousands of files.
- [ ] Test a large file on exFAT/NTFS and a ZIP exceeding the FAT32 limit on a disposable FAT32 drive.
- [ ] Repeat backup with existing ZIPs; verify the old pair is deleted before compression and only the latest filenames remain.
- [ ] Place unrelated root files, other ZIPs, and nested `FolderA.zip` files on the spare USB; verify all stay unchanged.
- [ ] Use a nearly full USB; preflight failure leaves existing ZIPs untouched when the estimate cannot fit even after reclaiming the two targets.
- [ ] Hold a source file open with sharing denied; get a readable failure and detailed log, without changing the source.
- [ ] Hold either destination ZIP open with sharing denied; fail before deletion when the initial exclusive-open check detects it.
- [ ] Deny write access or enable write protection; show an error and keep the UI responsive.
- [ ] Unplug during compression/writing; never show Backup Complete. Reconnect and retry successfully.
- [ ] Try closing/changing folders/ejecting during backup; controls are disabled and closing is deferred.
- [ ] Read View Log after success and failure; verify date, sources, volume, duration, result and errors.
- [ ] Eject after success: Windows accepts, the app says safe to remove, and the volume is no longer selectable.
- [ ] Keep a USB file open so Windows vetoes ejection: the app reports failure and never says safe to remove.
- [ ] Try a USB with multiple partitions and confirm Windows handles removal of the whole device appropriately.
- [ ] Launch a second instance in the same session: it reports the existing instance and exits.

## Build checks

- WPF cross-build succeeded with zero warnings and errors.
- Release self-contained publishing is performed for `win-x64`.
- Icon source is original; ICO includes all seven requested/common sizes.
- Cross-compilation and engine tests do **not** validate WPF rendering, Windows driver interactions, or real hardware removal.

# Test record and Windows checklist

Use disposable test sources and a spare USB. The app stages and verifies both archives before replacing existing backups. Never use valuable backups for hardware fault testing.

## Automated checks

```powershell
dotnet run --project tests/SimpleUSBBackup.Tests.csproj -c Release
```

Diagnostics update verified on Windows with .NET 8.0.425: **23 passed, 0 failed, 3 skipped**. The skipped tests require symbolic-link creation privilege.

| Test | Result |
| --- | --- |
| Two sources, nested directories, Chinese names, spaces, empty directories; sources unchanged | PASS |
| Only the two literal ZIP files replaced; unrelated files preserved | PASS |
| Previous backup pair remains intact during local compression and verification | PASS |
| Empty sources create valid ZIPs | PASS |
| Missing source fails before old ZIP deletion | PASS |
| Missing/disconnected volume fails before old ZIP deletion | PASS |
| Insufficient estimated space fails before old ZIP deletion | PASS |
| Sources on selected destination rejected | PASS |
| Backup-directory symlink cannot redirect deletion | SKIP: Windows privilege 1314 |
| Archive-file symlink cannot redirect writes | SKIP: Windows privilege 1314 |
| Source symlink fails before deletion | SKIP: Windows privilege 1314 |
| Simulated disconnection preserves previous backups and never reports success | PASS |
| Source changed after scan fails, incomplete archive removed | PASS |
| 2,000 files preserved | PASS |
| 32 MiB streaming file, byte progress and restored content hash match | PASS |

Every fixture uses a unique temporary directory that it creates and removes. Windows symbolic-link tests may be skipped unless Developer Mode or the appropriate privilege is enabled. The simulated drive checks exercise control flow, not actual USB hardware.

## Windows acceptance checks

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
- [ ] Repeat backup with existing ZIPs; verify the old pair remains until both temporary USB copies pass verification.
- [ ] Place unrelated root files, other ZIPs, and nested `FolderA.zip` files on the spare USB; verify all stay unchanged.
- [ ] Use a nearly full USB; preflight failure leaves existing ZIPs untouched when the new temporary pair cannot fit without reclaiming the existing targets.
- [ ] Hold a source file open with sharing denied; get a readable failure and detailed log, without changing the source.
- [ ] Hold either destination ZIP open with sharing denied; verify commit fails safely and preserves or restores the old pair.
- [ ] Deny write access or enable write protection; show an error and keep the UI responsive.
- [ ] Unplug during compression/writing; never show Backup Complete. Reconnect and retry successfully.
- [ ] Try closing/changing folders/ejecting during backup; controls are disabled and closing is deferred.
- [ ] Read View Log after success and failure; verify date, sources, volume, duration, result and errors.
- [ ] Eject after success: Windows accepts, the app says safe to remove, and the volume is no longer selectable.
- [ ] Keep a USB file open so Windows vetoes ejection: the app reports failure and never says safe to remove.
- [ ] Try a USB with multiple partitions and confirm Windows handles removal of the whole device appropriately.
- [ ] Launch a second instance in the same session: it reports the existing instance and exits.

## Build checks

- WPF Windows build succeeded with zero warnings and errors.
- Release self-contained publishing is performed for `win-x64`.
- Icon source is original; ICO includes all seven requested/common sizes.
- The self-contained Windows x64 EXE launched and remained running with a USB Backup window. The in-app header uses vector shapes matching the original SVG.
- Automated tests and launch smoke checks do **not** validate display scaling, Windows driver interactions, or physical USB reliability.

## Transfer diagnostic tests

All eleven passed with disposable local files: complete successful measurements; modified byte with offset and CRC failure; truncated copy; extra bytes; flush failure with Win32 code; read failure with Win32 code; matching invalid ZIP structure; matching bytes with invalid entry CRC; changed local archive blocked before copy; corrupted second USB copy preserves existing backups and verified local pair; ZIP64 central-directory validation with 65,536 entries.

Verification uses fresh buffered Windows reads, not unbuffered physical-media reads. No physical USB was written or tested in this update, and no hardware-corruption fix is claimed.

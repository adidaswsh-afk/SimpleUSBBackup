using System.IO.Compression;
using System.Security.Cryptography;
using SimpleUSBBackup;

internal static class DiagnosticsTests
{
    internal static async Task<int> Run()
    {
        (string Name, Func<string, Task> Run)[] tests =
        [
            ("Successful readback includes complete measurements", async root =>
            {
                var d = await Transfer(root, new(), null);
                Check(d.SizesMatch == true && d.BytesMatch == true && d.FullEntryValidationPassed == true, "Verification failed");
                Check(d.LocalSha256 == d.UsbReadbackSha256 && d.LocalSha256?.Length == 64, "Digests missing");
                Check(d.WriteSeconds > 0 && d.ReadSeconds > 0 && d.WriteBytesPerSecond > 0 && d.ReadBytesPerSecond > 0, "Timings missing");
                Check(d.FlushResult.StartsWith("Succeeded") && d.FirstDifferingByteOffset is null, "Flush/offset incorrect");
                Check(d.ReadMode.Contains("cached") && d.VolumeInformation.Contains("Root="), "Read mode/volume missing");
                Check(d.ToLog().Contains("ArchiveName") && d.ToLog().Contains("DestinationFilesystem"), "Log fields missing");
            }),
            ("Modified byte includes offset and failed CRC", async root =>
            {
                var d = await Transfer(root, new() { AfterWrite = p => ChangeByte(p, 40) }, TransferFailure.ByteVerificationFailed);
                Check(d.FirstDifferingByteOffset == 40 && d.SizesMatch == true && d.LocalSha256 != d.UsbReadbackSha256, "Mismatch diagnostics incorrect");
                Check(d.CentralDirectoryReadable == true && d.FullEntryValidationPassed == false, "Entry CRC corruption missed");
            }),
            ("Truncated copy is identified with full readback digest", async root =>
            {
                var d = await Transfer(root, new() { AfterWrite = p => { using var s = File.OpenWrite(p); s.SetLength(50); } }, TransferFailure.ByteVerificationFailed);
                Check(d.AppearsTruncated == true && d.SizesMatch == false && d.FirstDifferingByteOffset == 50, "Truncation missing");
                Check(d.UsbReadbackSha256 == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "usb.tmp")))), "USB digest incorrect");
                Check(d.CentralDirectoryReadable == false, "Truncated directory accepted");
            }),
            ("Extra USB bytes report first offset without truncation", async root =>
            {
                var d = await Transfer(root, new() { AfterWrite = p => { using var s = new FileStream(p, FileMode.Append); s.WriteByte(42); } }, TransferFailure.ByteVerificationFailed);
                Check(d.FirstDifferingByteOffset == d.LocalArchiveSize && d.AppearsTruncated == false, "Extra-byte classification incorrect");
            }),
            ("Flush failure is a write failure with Win32 code", async root =>
            {
                var d = await Transfer(root, new() { Flush = _ => throw new System.ComponentModel.Win32Exception(1117) }, TransferFailure.WriteFailed);
                Check(d.FlushResult == "Failed" && d.Errors.Any(e => e.Contains("Win32=1117")), "Flush failure details missing");
                Check(d.UsbReadbackSha256 is null && d.ZipCanOpen == true, "Failure phases misreported");
            }),
            ("Read I/O failure is distinguished from write failure", async root =>
            {
                var d = await Transfer(root, new() { OpenReadback = _ => throw new IOException("device disconnected", unchecked((int)0x80070015)), ReadModeOverride = "Injected failing read" }, TransferFailure.ByteVerificationFailed);
                Check(d.Errors.Any(e => e.Contains("Win32=21")) && d.ReadMode == "Injected failing read", "Read error details missing");
                Check(d.FlushResult.StartsWith("Succeeded") && d.UsbReadbackSha256 is null, "Read failure phases incorrect");
            }),
            ("Matching invalid ZIP bytes classify as structure failure", async root =>
            {
                File.WriteAllText(Path.Combine(root, "local.zip"), "not a ZIP");
                var d = await Transfer(root, new(), TransferFailure.ZipStructureFailed, false);
                Check(d.BytesMatch == true && d.CentralDirectoryReadable == false, "Structure failure classification incorrect");
            }),
            ("Matching bytes with bad CRC classify as contents failure", async root =>
            {
                CreateZip(Path.Combine(root, "local.zip")); ChangeByte(Path.Combine(root, "local.zip"), 40);
                var d = await Transfer(root, new(), TransferFailure.ArchiveContentsFailed, false);
                Check(d.BytesMatch == true && d.CentralDirectoryReadable == true && d.FullEntryValidationPassed == false, "Contents failure classification incorrect");
            }),
            ("Changed verified local archive blocks transfer", async root =>
            {
                string local = Path.Combine(root, "local.zip"), usb = Path.Combine(root, "usb.tmp");
                CreateZip(local);
                string verifiedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(local)));
                ChangeByte(local, 40); TransferDiagnostics? d = null;
                try { await new ArchiveTransfer().CopyAndVerifyAsync(local, usb, "NTFS", value => d = value, () => { }, verifiedHash); throw new Exception("Changed local archive accepted"); }
                catch (TransferException e) { Check(e.Failure == TransferFailure.WriteFailed, "Wrong failure classification"); }
                Check(!File.Exists(usb) && d?.VerifiedLocalSha256 == verifiedHash && d.LocalSha256 != verifiedHash, "Verified digest not retained");
            }),
            ("Corrupt second USB archive preserves previous set and verified local pair", async root =>
            {
                string a = Path.Combine(root, "a"), b = Path.Combine(root, "b"), usb = Path.Combine(root, "usb"), stage = Path.Combine(root, "stage");
                Directory.CreateDirectory(a); Directory.CreateDirectory(b); Directory.CreateDirectory(Path.Combine(usb, "Backup"));
                File.WriteAllText(Path.Combine(a, "x"), "source A"); File.WriteAllText(Path.Combine(b, "x"), "source B");
                string finalA = Path.Combine(usb, "Backup", "FolderA.zip"), finalB = Path.Combine(usb, "Backup", "FolderB.zip");
                File.WriteAllText(finalA, "old A"); File.WriteAllText(finalB, "old B");
                var records = new List<TransferDiagnostics>();
                var engine = new BackupService { StagingRoot = stage, DiagnosticLog = records.Add,
                    Transfer = new() { AfterWrite = p => { if (Path.GetFileName(p).StartsWith("FolderB")) ChangeByte(p, 0); } } };
                try { await engine.RunAsync(a, b, usb, "NTFS", () => { }, () => long.MaxValue, new QuietProgress()); throw new Exception("Corruption accepted"); }
                catch (TransferException e) { Check(e.Failure == TransferFailure.ByteVerificationFailed, "Wrong failure"); }
                Check(File.ReadAllText(finalA) == "old A" && File.ReadAllText(finalB) == "old B", "Previous backups changed");
                Check(Directory.GetFiles(stage, "*.zip", SearchOption.AllDirectories).Length == 2, "Local archives lost");
                Check(Directory.GetFiles(Path.Combine(usb, "Backup")).Length == 2 && records.Count == 2, "Cleanup/log failure");
                Check(records.All(d => d.VerifiedLocalSha256 == d.LocalSha256), "Verified digest chain missing");
            }),
            ("ZIP64 central directory CRC validation", root =>
            {
                string path = Path.Combine(root, "zip64.zip");
                using (var z = ZipFile.Open(path, ZipArchiveMode.Create))
                    for (int i = 0; i < 65536; i++) z.CreateEntry($"{i}/");
                var d = new TransferDiagnostics(); ArchiveTransfer.DiagnoseZip(path, d);
                Check(d.FullEntryValidationPassed == true, "ZIP64 rejected: " + string.Join("; ", d.Errors));
                return Task.CompletedTask;
            })
        ];
        int failed = 0;
        foreach (var test in tests)
        {
            string root = Path.Combine(Path.GetTempPath(), "usb-diagnostics-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { await test.Run(root); Console.WriteLine("PASS " + test.Name); }
            catch (Exception e) { failed++; Console.WriteLine($"FAIL {test.Name}: {e}"); }
            finally { Directory.Delete(root, true); }
        }
        Console.WriteLine($"Diagnostics: {tests.Length - failed} passed, {failed} failed."); return failed;
    }
    private static async Task<TransferDiagnostics> Transfer(string root, ArchiveTransfer transfer, TransferFailure? expected, bool create = true)
    {
        string local = Path.Combine(root, "local.zip"); if (create) CreateZip(local);
        TransferDiagnostics? d = null;
        try
        {
            await transfer.CopyAndVerifyAsync(local, Path.Combine(root, "usb.tmp"), "NTFS", value => d = value, () => { });
            Check(expected is null, "Expected failure " + expected);
        }
        catch (TransferException e) { Check(e.Failure == expected, "Unexpected failure " + e.Failure); }
        Check(d is not null, "No diagnostics recorded"); return d!;
    }
    private static void CreateZip(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var output = zip.CreateEntry("data.bin", CompressionLevel.NoCompression).Open();
        output.Write(RandomNumberGenerator.GetBytes(4096));
    }
    private static void ChangeByte(string path, long offset)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        s.Position = offset; int value = s.ReadByte(); s.Position = offset; s.WriteByte((byte)(value ^ 0xff));
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class QuietProgress : IProgress<BackupProgress> { public void Report(BackupProgress p) { } }
}

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace SimpleUSBBackup;

public enum TransferFailure { WriteFailed, ByteVerificationFailed, ZipStructureFailed, ArchiveContentsFailed }
public sealed class TransferException(TransferFailure failure, Exception? inner = null) : IOException(failure switch
{
    TransferFailure.WriteFailed => "The USB copy could not be written or flushed. Check the connection and free space.",
    TransferFailure.ByteVerificationFailed when inner is not null => "The USB copy could not be read back for verification. Check the USB connection.",
    TransferFailure.ByteVerificationFailed => "The USB copy differs from the verified local backup. Try another USB drive or port.",
    TransferFailure.ZipStructureFailed => "The copied bytes matched, but the ZIP directory is invalid.",
    _ => "The ZIP directory is readable, but archive contents failed validation."
}, inner)
{
    public TransferFailure Failure { get; } = failure;
}

public sealed class TransferDiagnostics
{
    public string ArchiveName { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string UsbTemporaryPath { get; set; } = "";
    public long? LocalArchiveSize { get; set; }
    public long? UsbTemporaryArchiveSize { get; set; }
    public string? LocalSha256 { get; set; }
    public string? VerifiedLocalSha256 { get; set; }
    public string? UsbReadbackSha256 { get; set; }
    public bool? SizesMatch { get; set; }
    public long? FirstDifferingByteOffset { get; set; }
    public bool? BytesMatch { get; set; }
    public bool? ZipCanOpen { get; set; }
    public bool? CentralDirectoryReadable { get; set; }
    public bool? FullEntryValidationPassed { get; set; }
    public string ZipAnalysisReadMode { get; set; } = "Buffered local snapshot of verification readback";
    public bool? AppearsTruncated { get; set; }
    public long WrittenBytes { get; set; }
    public long ReadBytes { get; set; }
    public double WriteSeconds { get; set; }
    public double ReadSeconds { get; set; }
    public double ZipValidationSeconds { get; set; }
    public double VerificationSeconds => ReadSeconds + ZipValidationSeconds;
    public double WriteBytesPerSecond => WriteSeconds > 0 ? WrittenBytes / WriteSeconds : 0;
    public double ReadBytesPerSecond => ReadSeconds > 0 ? ReadBytes / ReadSeconds : 0;
    public string FlushResult { get; set; } = "Not attempted";
    public string ReadMode { get; set; } = "Fresh FileStream; FileMode.Open, FileAccess.Read, FileShare.Read, 1 MiB buffer, FileOptions.SequentialScan. Windows cached I/O; no FILE_FLAG_NO_BUFFERING. Readback captured locally for ZIP analysis.";
    public string DestinationFilesystem { get; set; } = "";
    public string VolumeInformation { get; set; } = "Unavailable";
    public string DriveModel { get; set; } = "Unavailable";
    public string Result { get; set; } = "Not completed";
    public List<string> Errors { get; } = [];
    public void RecordError(string stage, Exception e)
    {
        for (Exception? current = e; current is not null; current = current.InnerException)
        {
            int? win32 = current is Win32Exception w ? w.NativeErrorCode :
                ((uint)current.HResult & 0xffff0000) == 0x80070000 ? current.HResult & 0xffff : null;
            Errors.Add($"{stage}: {current.GetType().Name}: {current.Message}; HRESULT=0x{current.HResult:X8}; Win32={(win32?.ToString() ?? "not available")}");
        }
    }
    public string ToLog() => "USB TRANSFER DIAGNOSTICS (null = not measured / not reached; offsets are zero-based)\n" +
        JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}

public sealed class ArchiveTransfer
{
    // Injection points are used by disposable tests to simulate failed writes and corrupted storage.
    public Action<string>? AfterWrite { get; init; }
    public Action<FileStream> Flush { get; init; } = stream => stream.Flush(true);
    public Func<string, Stream> OpenReadback { get; init; } = path => new FileStream(path, FileMode.Open,
        FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
    public string? ReadModeOverride { get; init; }

    public async Task CopyAndVerifyAsync(string local, string temporary, string filesystem,
        Action<TransferDiagnostics> log, Action checkDrive, string? verifiedLocalSha256 = null)
    {
        var d = new TransferDiagnostics { ArchiveName = Path.GetFileName(local), LocalPath = local,
            UsbTemporaryPath = temporary, DestinationFilesystem = filesystem, VerifiedLocalSha256 = verifiedLocalSha256 };
        if (ReadModeOverride is not null) d.ReadMode = ReadModeOverride;
        string snapshot = Path.Combine(Path.GetDirectoryName(local)!, "readback-" + Guid.NewGuid().ToString("N"));
        try
        {
            var info = new DriveInfo(Path.GetPathRoot(temporary)!);
            try { d.DestinationFilesystem = info.DriveFormat; d.VolumeInformation = $"Root={info.Name}; Label={info.VolumeLabel}; Type={info.DriveType}; TotalBytes={info.TotalSize}; FreeBytes={info.AvailableFreeSpace}"; }
            catch (Exception e) { d.RecordError("Volume information", e); }
            if (OperatingSystem.IsWindows())
                try { d.DriveModel = UsbService.GetModel(info.Name); d.VolumeInformation += "; VolumeId=" + UsbService.GetVolumeId(info.Name); }
                catch (Exception e) { d.RecordError("Device information", e); }

            // Hold the verified local file against writers/deletion for hashing, copy, and comparison.
            using var source = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            d.LocalArchiveSize = source.Length;
            d.LocalSha256 = Convert.ToHexString(SHA256.HashData(source));
            if (verifiedLocalSha256 is not null && d.LocalSha256 != verifiedLocalSha256)
                throw new InvalidDataException("Local archive changed since source validation; USB transfer was not started.");
            source.Position = 0;
            var watch = Stopwatch.StartNew();
            try
            {
                using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    1024 * 1024, FileOptions.WriteThrough))
                {
                    byte[] buffer = new byte[1024 * 1024];
                    int count;
                    while ((count = await source.ReadAsync(buffer)) > 0)
                    {
                        checkDrive();
                        await target.WriteAsync(buffer.AsMemory(0, count));
                        d.WrittenBytes += count;
                    }
                    d.FlushResult = "Attempted: FileStream.Flush(flushToDisk: true), Windows FlushFileBuffers";
                    try { Flush(target); d.FlushResult = "Succeeded: FileStream.Flush(true) / Windows FlushFileBuffers"; }
                    catch (Exception e) { d.FlushResult = "Failed"; d.RecordError("Flush / FlushFileBuffers", e); throw; }
                }
            }
            catch (Exception e) { d.RecordError("Copy/write", e); d.Result = nameof(TransferFailure.WriteFailed); throw new TransferException(TransferFailure.WriteFailed, e); }
            finally { d.WriteSeconds = watch.Elapsed.TotalSeconds; }
            AfterWrite?.Invoke(temporary);
            source.Position = 0;
            watch.Restart();
            try
            {
                d.UsbTemporaryArchiveSize = new FileInfo(temporary).Length;
                d.SizesMatch = d.LocalArchiveSize == d.UsbTemporaryArchiveSize;
                d.AppearsTruncated = d.UsbTemporaryArchiveSize < d.LocalArchiveSize;
                using var readback = OpenReadback(temporary);
                using var captured = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] usb = new byte[1024 * 1024], expected = new byte[usb.Length];
                int count;
                while ((count = await readback.ReadAsync(usb)) > 0)
                {
                    checkDrive();
                    int length = 0, read;
                    while (length < count && (read = await source.ReadAsync(expected.AsMemory(length, count - length))) > 0) length += read;
                    for (int i = 0; i < count && d.FirstDifferingByteOffset is null; i++)
                        if (i >= length || usb[i] != expected[i]) d.FirstDifferingByteOffset = d.ReadBytes + i;
                    hash.AppendData(usb, 0, count);
                    await captured.WriteAsync(usb.AsMemory(0, count));
                    d.ReadBytes += count;
                }
                if (d.ReadBytes != d.LocalArchiveSize && d.FirstDifferingByteOffset is null)
                    d.FirstDifferingByteOffset = Math.Min(d.ReadBytes, d.LocalArchiveSize.Value);
                d.UsbReadbackSha256 = Convert.ToHexString(hash.GetHashAndReset());
                d.AppearsTruncated |= d.ReadBytes < d.LocalArchiveSize;
                d.BytesMatch = d.SizesMatch == true && d.ReadBytes == d.LocalArchiveSize && d.LocalSha256 == d.UsbReadbackSha256;
            }
            catch (Exception e) { d.RecordError("Verification/read", e); d.Result = nameof(TransferFailure.ByteVerificationFailed); throw new TransferException(TransferFailure.ByteVerificationFailed, e); }
            finally { d.ReadSeconds = watch.Elapsed.TotalSeconds; }
            // Diagnose even a byte mismatch, from precisely the bytes used for the USB digest.
            DiagnoseZip(snapshot, d);
            TransferFailure? failure = d.BytesMatch != true ? TransferFailure.ByteVerificationFailed :
                d.CentralDirectoryReadable != true ? TransferFailure.ZipStructureFailed :
                d.FullEntryValidationPassed != true ? TransferFailure.ArchiveContentsFailed : null;
            d.Result = failure?.ToString() ?? "Passed";
            if (failure is { } failed) throw new TransferException(failed);
        }
        catch (TransferException) { throw; }
        catch (Exception e) { d.RecordError("Transfer preparation", e); d.Result = nameof(TransferFailure.WriteFailed); throw new TransferException(TransferFailure.WriteFailed, e); }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    d.UsbTemporaryArchiveSize = new FileInfo(temporary).Length;
                    d.SizesMatch = d.LocalArchiveSize == d.UsbTemporaryArchiveSize;
                    d.AppearsTruncated = d.AppearsTruncated == true || d.UsbTemporaryArchiveSize < d.LocalArchiveSize;
                    if (d.ZipCanOpen is null)
                    {
                        d.ZipAnalysisReadMode = "Separate buffered ZIP probe of USB temporary file after incomplete write/read; not a substitute for byte verification";
                        DiagnoseZip(temporary, d);
                    }
                }
            }
            catch (Exception e) { d.RecordError("Final size probe", e); }
            try { File.Delete(snapshot); } catch (Exception e) { d.RecordError("Readback snapshot cleanup", e); }
            log(d);
        }
    }

    public static void DiagnoseZip(string path, TransferDiagnostics d)
    {
        var watch = Stopwatch.StartNew();
        ZipArchive? zip = null;
        try
        {
            zip = ZipFile.OpenRead(path); d.ZipCanOpen = true;
            _ = zip.Entries.Count;
            var crcs = ReadCentralCrcs(path);
            if (crcs.Count != zip.Entries.Count) throw new InvalidDataException("Central directory count differs.");
            d.CentralDirectoryReadable = true;
            try
            {
                byte[] buffer = new byte[1024 * 1024];
                for (int i = 0; i < zip.Entries.Count; i++)
                {
                    using var entry = zip.Entries[i].Open();
                    long bytes = 0; uint crc = 0xffffffff; int read;
                    while ((read = entry.Read(buffer)) > 0)
                    {
                        bytes += read;
                        for (int j = 0; j < read; j++) crc = CrcTable[(crc ^ buffer[j]) & 255] ^ (crc >> 8);
                    }
                    if (bytes != zip.Entries[i].Length || ~crc != crcs[i]) throw new InvalidDataException($"Entry {i}: length or CRC-32 mismatch.");
                }
                d.FullEntryValidationPassed = true;
            }
            catch (Exception e) { d.FullEntryValidationPassed = false; d.RecordError("ZIP contents", e); }
        }
        catch (Exception e)
        {
            d.ZipCanOpen ??= false; d.CentralDirectoryReadable = false;
            d.RecordError("ZIP structure", e);
        }
        finally { zip?.Dispose(); d.ZipValidationSeconds = watch.Elapsed.TotalSeconds; }
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i =>
    { uint c = (uint)i; for (int bit = 0; bit < 8; bit++) c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1; return c; }).ToArray();

    // Read CRCs from the central directory; .NET's decompression alone does not verify ZIP CRCs.
    private static List<uint> ReadCentralCrcs(string path)
    {
        using var stream = File.OpenRead(path); using var r = new BinaryReader(stream);
        int tailSize = (int)Math.Min(stream.Length, 65557);
        stream.Position = stream.Length - tailSize; byte[] tail = r.ReadBytes(tailSize);
        int eocd = -1;
        for (int i = tail.Length - 22; i >= 0; i--)
            if (BitConverter.ToUInt32(tail, i) == 0x06054b50 && i + 22 + BitConverter.ToUInt16(tail, i + 20) == tail.Length) { eocd = i; break; }
        if (eocd < 0) throw new InvalidDataException("End of central directory missing (possibly truncated).");
        if (BitConverter.ToUInt16(tail, eocd + 4) != 0 || BitConverter.ToUInt16(tail, eocd + 6) != 0) throw new InvalidDataException("Multi-disk ZIP unsupported.");
        long count = BitConverter.ToUInt16(tail, eocd + 10), offset = BitConverter.ToUInt32(tail, eocd + 16);
        if (count == ushort.MaxValue || offset == uint.MaxValue)
        {
            stream.Position = stream.Length - tailSize + eocd - 20;
            if (r.ReadUInt32() != 0x07064b50 || r.ReadUInt32() != 0) throw new InvalidDataException("ZIP64 locator missing.");
            long zip64 = checked((long)r.ReadUInt64());
            if (r.ReadUInt32() != 1) throw new InvalidDataException("Multi-disk ZIP64 unsupported.");
            stream.Position = zip64;
            if (r.ReadUInt32() != 0x06064b50) throw new InvalidDataException("ZIP64 directory missing.");
            stream.Position = zip64 + 32; count = checked((long)r.ReadUInt64());
            _ = r.ReadUInt64(); offset = checked((long)r.ReadUInt64());
        }
        if (count > stream.Length / 46 || offset < 0 || offset > stream.Length) throw new InvalidDataException("Invalid central directory bounds.");
        stream.Position = offset; var result = new List<uint>();
        for (long i = 0; i < count; i++)
        {
            long start = stream.Position;
            if (r.ReadUInt32() != 0x02014b50) throw new InvalidDataException("Invalid central directory entry.");
            stream.Position = start + 16; result.Add(r.ReadUInt32());
            stream.Position = start + 28;
            int skip = r.ReadUInt16() + r.ReadUInt16() + r.ReadUInt16();
            stream.Position = checked(start + 46 + skip);
            if (stream.Position > stream.Length) throw new InvalidDataException("Truncated central directory entry.");
        }
        return result;
    }
}

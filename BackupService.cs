using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SimpleUSBBackup;

public sealed record BackupProgress(double Percent, string Stage, string File = "");
public sealed record SourceEntry(string Path, string Name, long Bytes, DateTime Modified, bool IsDirectory);
public sealed record SourcePlan(string Root, List<SourceEntry> Entries)
{
    public long Bytes => Entries.Sum(x => x.Bytes);
    public long EstimatedBytes => checked(Bytes + Bytes / 100 + Entries.Count * 2048L + 65536);
}

public sealed class BackupService
{
    public string StagingRoot { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimpleUSBBackup", "local-backups");
    public Action<TransferDiagnostics> DiagnosticLog { get; init; } = d => LogService.Append(d.ToLog());
    public ArchiveTransfer Transfer { get; init; } = new();

    public async Task RunAsync(string folderA, string folderB, string usbRoot, string format,
        Action checkDrive, Func<long> freeBytes, IProgress<BackupProgress> progress)
    {
        void Report(double value, string stage, string file = "") => progress.Report(new(value, stage, file));
        checkDrive();
        foreach (string source in new[] { folderA, folderB })
            if (IsWithin(source, usbRoot) || IsWithin(usbRoot, source))
                throw new IOException("Choose source folders outside the selected USB drive.");
        var plans = new[] { Scan(folderA), Scan(folderB) };
        string destination = Path.Combine(Path.GetFullPath(usbRoot), "Backup");
        RejectLinks(destination);
        Directory.CreateDirectory(destination);
        string[] paths = [Path.Combine(destination, "FolderA.zip"), Path.Combine(destination, "FolderB.zip")];
        foreach (string path in paths) RejectLinks(path);
        long estimate = checked(plans.Sum(x => x.EstimatedBytes) + 8 * 1024 * 1024);
        if (freeBytes() < estimate) throw new IOException("Not enough USB space to safely stage the new backups. Existing backups were preserved.");
        string staging = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
        RejectLinks(staging);
        Directory.CreateDirectory(staging);
        string[] local = paths.Select(p => Path.Combine(staging, Path.GetFileName(p))).ToArray();
        string[] verifiedHashes = new string[2];
        string id = Guid.NewGuid().ToString("N");
        string[] temporary = paths.Select(p => p + ".new-" + id).ToArray();
        try
        {
            for (int i = 0; i < 2; i++)
            {
                int index = i;
                string stage = $"Compressing Folder {(i == 0 ? "A" : "B")}...";
                Report(i == 0 ? 10 : 45, stage);
                await CreateZipAsync(plans[i], local[i], () => { }, (ratio, file) =>
                    Report((index == 0 ? 10 : 45) + 30 * ratio, stage, file));
                Report(i == 0 ? 40 : 75, "Verifying local archive...", Path.GetFileName(local[i]));
                try { verifiedHashes[i] = VerifyLocal(local[i], plans[i]); }
                catch { File.Delete(local[i]); throw; }
            }
            for (int i = 0; i < 2; i++)
            {
                checkDrive();
                Report(80 + i * 8, "Copying and verifying USB...", Path.GetFileName(local[i]));
                await Transfer.CopyAndVerifyAsync(local[i], temporary[i], format, DiagnosticLog, checkDrive, verifiedHashes[i]);
            }
            checkDrive();
            Report(97, "Finalizing backup...");
            Commit(paths, temporary, id);
            Report(100, "Backup Complete", "Both folders are saved. You can eject the USB.");
        }
        finally
        {
            foreach (string path in temporary)
                try { checkDrive(); RejectLinks(path); File.Delete(path); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { DiagnosticLog(new TransferDiagnostics { ArchiveName = Path.GetFileName(path), Result = "Temporary cleanup failed", Errors = { e.ToString() } }); }
        }
    }

    private static string VerifyLocal(string path, SourcePlan plan)
    {
        var diagnostic = new TransferDiagnostics();
        ArchiveTransfer.DiagnoseZip(path, diagnostic);
        if (diagnostic.FullEntryValidationPassed != true) throw new InvalidDataException("Local ZIP validation failed: " + string.Join("; ", diagnostic.Errors));
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count != Math.Max(1, plan.Entries.Count)) throw new InvalidDataException("Local ZIP entry count differs from source.");
        foreach (var item in plan.Entries)
        {
            var entry = zip.GetEntry(item.Name + (item.IsDirectory ? "/" : "")) ?? throw new InvalidDataException("Local ZIP entry missing.");
            if (item.IsDirectory) continue;
            RejectLinks(item.Path);
            using var source = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archived = entry.Open();
            if (source.Length != item.Bytes || File.GetLastWriteTimeUtc(item.Path) != item.Modified ||
                !System.Security.Cryptography.SHA256.HashData(source).SequenceEqual(System.Security.Cryptography.SHA256.HashData(archived)) ||
                File.GetLastWriteTimeUtc(item.Path) != item.Modified)
                throw new IOException("Source changed during backup. Close editing apps and try again.");
        }
        var current = Scan(plan.Root);
        if (!current.Entries.OrderBy(x => x.Name).SequenceEqual(plan.Entries.OrderBy(x => x.Name)))
            throw new IOException("Source changed during backup. Close editing apps and try again.");
        using var verified = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(verified));
    }

    private static void Commit(string[] paths, string[] temporary, string id)
    {
        string[] old = paths.Select(p => p + ".old-" + id).ToArray();
        bool[] saved = new bool[2], installed = new bool[2];
        try
        {
            for (int i = 0; i < 2; i++)
            {
                RejectLinks(paths[i]);
                if (File.Exists(paths[i])) { File.Move(paths[i], old[i]); saved[i] = true; }
            }
            for (int i = 0; i < 2; i++) { File.Move(temporary[i], paths[i]); installed[i] = true; }
        }
        catch (Exception commitError)
        {
            var errors = new List<Exception> { commitError };
            for (int i = 1; i >= 0; i--)
                try
                {
                    if (installed[i]) File.Delete(paths[i]);
                    if (saved[i]) File.Move(old[i], paths[i]);
                }
                catch (Exception e) { errors.Add(e); }
            throw new IOException("USB replacement failed. Previous backups were restored where possible; recovery copies have .old- names. View diagnostics.", new AggregateException(errors));
        }
        // Cleanup cannot roll back a successfully installed set after one old copy has been removed.
        foreach (string path in old)
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public static SourcePlan Scan(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) throw new DirectoryNotFoundException($"Source folder not found: {root}");
        root = Path.GetFullPath(root);
        RejectLinks(root);
        var entries = new List<SourceEntry>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            RejectLinks(directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Links and junctions are not supported: {path}");
                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                var file = new FileInfo(path);
                entries.Add(new(path, Path.GetRelativePath(root, path).Replace('\\', '/'), isDirectory ? 0 : file.Length,
                    isDirectory ? Directory.GetLastWriteTimeUtc(path) : file.LastWriteTimeUtc, isDirectory));
                if (isDirectory) pending.Push(path);
            }
        }
        return new(root, entries);
    }

    private static async Task CreateZipAsync(SourcePlan plan, string path, Action checkDrive, Action<double, string> progress)
    {
        RejectLinks(path);
        bool created = false;
        try
        {
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 1024, true);
            created = true;
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true, Encoding.UTF8))
            {
                if (plan.Entries.Count == 0) zip.CreateEntry("Empty folder/");
                long done = 0;
                double total = Math.Max(1, plan.Bytes + plan.Entries.Count);
                byte[] buffer = new byte[1024 * 1024];
                var volumeCheck = Stopwatch.StartNew();
                foreach (var item in plan.Entries)
                {
                    RejectLinks(item.Path);
                    var entry = zip.CreateEntry(item.Name + (item.IsDirectory ? "/" : ""), CompressionLevel.Fastest);
                    if (item.Modified.Year is >= 1980 and <= 2107) entry.LastWriteTime = new DateTimeOffset(item.Modified);
                    if (!item.IsDirectory)
                    {
                        await using var source = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, true);
                        if (source.Length != item.Bytes || File.GetLastWriteTimeUtc(item.Path) != item.Modified)
                            throw new IOException($"Source changed during backup: {item.Path}. Close editing apps and try again.");
                        await using var target = entry.Open();
                        int read;
                        while ((read = await source.ReadAsync(buffer)) > 0)
                        {
                            if (volumeCheck.ElapsedMilliseconds >= 500) { checkDrive(); volumeCheck.Restart(); }
                            await target.WriteAsync(buffer.AsMemory(0, read));
                            done += read;
                            progress(Math.Min(1, done / total), item.Name);
                        }
                        if (File.GetLastWriteTimeUtc(item.Path) != item.Modified)
                            throw new IOException($"Source changed during backup: {item.Path}");
                    }
                    done++;
                    progress(Math.Min(1, done / total), item.Name);
                }
            }
            output.Flush(true);
            progress(1, "Archive saved");
        }
        catch
        {
            // Remove only the incomplete file created by this call, if the same USB is still present.
            if (created)
                try { checkDrive(); RejectLinks(path); File.Delete(path); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    public static bool IsWithin(string path, string parent)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        parent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        return path.Equals(parent, comparison) || path.StartsWith(parent + Path.DirectorySeparatorChar, comparison);
    }

    public static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"A symbolic link or junction is not supported: {current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int i = 0;
        while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
        return $"{value:0.#} {units[i]}";
    }
}

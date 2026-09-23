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
    // Delegates keep the engine testable with controlled temporary folders, without a storage abstraction layer.
    public async Task RunAsync(string folderA, string folderB, string usbRoot, string format,
        Action checkDrive, Func<long> freeBytes, IProgress<BackupProgress> progress)
    {
        void Report(double value, string stage, string file = "") => progress.Report(new(value, stage, file));
        Report(0, "Preparing...", "Checking folders and USB");
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
        long reclaimable = paths.Where(File.Exists).Sum(x => new FileInfo(x).Length);
        long estimate = checked(plans.Sum(x => x.EstimatedBytes) + 8 * 1024 * 1024);
        if (checked(freeBytes() + reclaimable) < estimate)
            throw new IOException($"Not enough USB space. About {FormatBytes(estimate)} is needed. The old backups have not been deleted.");
        // Open both exact targets exclusively before deleting either, catching common lock/permission errors early.
        var opened = new List<FileStream>();
        try
        {
            foreach (string path in paths)
                if (File.Exists(path)) opened.Add(new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
        }
        finally { foreach (var stream in opened) stream.Dispose(); }

        Report(5, "Deleting old backup...");
        foreach (string path in paths)
        {
            checkDrive();
            RejectLinks(path);
            File.Delete(path); // Only these two literal filenames; no wildcards or recursive deletion.
        }
        Report(10, "Preparing new archives...");
        if (freeBytes() < estimate) throw new IOException("The USB does not have enough free space for the new backup.");

        for (int i = 0; i < 2; i++)
        {
            checkDrive();
            int index = i;
            string stage = $"Compressing Folder {(i == 0 ? "A" : "B")}...";
            Report(i == 0 ? 10 : 45, stage);
            await CreateZipAsync(plans[i], paths[i], checkDrive, (ratio, file) =>
                Report((index == 0 ? 10 : 45) + 35 * ratio, stage, file));
        }
        Report(80, "Finishing...", "Checking both ZIP archives");
        for (int i = 0; i < 2; i++)
        {
            checkDrive();
            RejectLinks(paths[i]);
            if (new FileInfo(paths[i]).Length == 0) throw new InvalidDataException("An archive is empty.");
            using var zip = ZipFile.OpenRead(paths[i]);
            if (zip.Entries.Count != Math.Max(1, plans[i].Entries.Count)) throw new InvalidDataException("The ZIP directory is incomplete.");
            Report(80 + (i + 1) * 9, "Finishing...", $"Folder{(i == 0 ? "A" : "B")}.zip checked");
        }
        checkDrive();
        Report(100, "Backup Complete", "Both folders are saved. You can eject the USB.");
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

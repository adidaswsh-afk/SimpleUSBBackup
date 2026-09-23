using System.IO.Compression;
using System.Security.Cryptography;
using SimpleUSBBackup;

// Dependency-free integration tests: all deletion is confined to a new temporary fixture.
var tests = new (string Name, Func<Fixture, Task> Run)[]
{
    ("Two folders, nested paths, Chinese names, spaces and empty directory", async f =>
    {
        Directory.CreateDirectory(Path.Combine(f.A, "物理", "Unit 4", "空文件夹"));
        File.WriteAllText(Path.Combine(f.A, "物理", "Unit 4", "Lesson 12 练习.txt"), "你好，物理！");
        File.WriteAllText(Path.Combine(f.B, "questions.txt"), "question bank");
        var before = f.SourceHashes();
        await f.Backup();
        using var a = ZipFile.OpenRead(f.ZipA);
        var file = a.GetEntry("物理/Unit 4/Lesson 12 练习.txt");
        Require(file is not null, "Unicode entry missing");
        using var reader = new StreamReader(file!.Open());
        Require(reader.ReadToEnd() == "你好，物理！", "Unicode content damaged");
        Require(a.GetEntry("物理/Unit 4/空文件夹/") is not null, "Empty directory missing");
        Require(before.SequenceEqual(f.SourceHashes()), "Source files changed");
        Require(f.Progress[^1].Percent == 100, "Progress did not complete");
        Require(f.Progress.Zip(f.Progress.Skip(1)).All(x => x.First.Percent <= x.Second.Percent), "Progress regressed");
    }),
    ("Only the two exact archives are replaced", async f =>
    {
        f.SeedOld();
        File.WriteAllText(Path.Combine(f.Usb, "important.txt"), "keep root");
        File.WriteAllText(Path.Combine(f.BackupDirectory, "unrelated.zip"), "keep unrelated zip");
        File.WriteAllText(Path.Combine(f.BackupDirectory, "FolderA.zip.part"), "keep part");
        Directory.CreateDirectory(Path.Combine(f.BackupDirectory, "nested"));
        File.WriteAllText(Path.Combine(f.BackupDirectory, "nested", "FolderA.zip"), "keep nested");
        File.WriteAllText(Path.Combine(f.A, "new.txt"), "new version");
        await f.Backup();
        using var a = ZipFile.OpenRead(f.ZipA);
        Require(a.GetEntry("new.txt") is not null, "New archive missing");
        Require(File.ReadAllText(Path.Combine(f.Usb, "important.txt")) == "keep root", "Root file changed");
        Require(File.ReadAllText(Path.Combine(f.BackupDirectory, "unrelated.zip")) == "keep unrelated zip", "Unrelated ZIP changed");
        Require(File.ReadAllText(Path.Combine(f.BackupDirectory, "FolderA.zip.part")) == "keep part", "Unrelated part changed");
        Require(File.ReadAllText(Path.Combine(f.BackupDirectory, "nested", "FolderA.zip")) == "keep nested", "Nested ZIP changed");
    }),
    ("Delete-first order follows the revised spec", async f =>
    {
        f.SeedOld();
        bool checkedOrder = false;
        f.OnProgress = p =>
        {
            if (p.Stage == "Compressing Folder A..." && !checkedOrder)
            {
                Require(!File.Exists(f.ZipA) && !File.Exists(f.ZipB), "Old archives still exist before compression");
                checkedOrder = true;
            }
        };
        await f.Backup();
        Require(checkedOrder, "Compression stage not observed");
    }),
    ("Empty sources produce readable ZIPs", async f =>
    {
        await f.Backup();
        using var a = ZipFile.OpenRead(f.ZipA);
        using var b = ZipFile.OpenRead(f.ZipB);
        Require(a.Entries.Count == 1 && b.Entries.Count == 1, "Empty source placeholder missing");
    }),
    ("Missing source fails before deleting old ZIPs", async f =>
    {
        f.SeedOld();
        Directory.Delete(f.B);
        await MustFail<DirectoryNotFoundException>(() => f.Backup());
        f.AssertOld();
    }),
    ("Disconnected USB fails before deletion", async f =>
    {
        f.SeedOld();
        f.Check = () => throw new IOException("USB disconnected");
        await MustFail<IOException>(() => f.Backup());
        f.AssertOld();
    }),
    ("Clearly insufficient space fails before deletion", async f =>
    {
        f.SeedOld();
        f.Free = () => 0;
        await MustFail<IOException>(() => f.Backup());
        f.AssertOld();
    }),
    ("Source on target USB is rejected", async f =>
    {
        f.SeedOld();
        await MustFail<IOException>(() => new BackupService().RunAsync(f.Usb, f.B, f.Usb, "exFAT", f.Check, f.Free, f));
        f.AssertOld();
    }),
    ("Redirected backup directory cannot delete outside targets", async f =>
    {
        string outside = Path.Combine(f.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "FolderA.zip"), "do not delete");
        Directory.CreateSymbolicLink(f.BackupDirectory, outside);
        await MustFail<IOException>(() => f.Backup());
        Require(File.ReadAllText(Path.Combine(outside, "FolderA.zip")) == "do not delete", "Link target modified");
    }),
    ("Redirected archive cannot overwrite outside files", async f =>
    {
        Directory.CreateDirectory(f.BackupDirectory);
        string outside = Path.Combine(f.Root, "outside.zip");
        File.WriteAllText(outside, "do not modify");
        File.CreateSymbolicLink(f.ZipA, outside);
        await MustFail<IOException>(() => f.Backup());
        Require(File.ReadAllText(outside) == "do not modify", "Linked file modified");
    }),
    ("Source symbolic links fail before deleting backups", async f =>
    {
        f.SeedOld();
        File.WriteAllText(Path.Combine(f.B, "target.txt"), "safe");
        File.CreateSymbolicLink(Path.Combine(f.A, "link.txt"), Path.Combine(f.B, "target.txt"));
        await MustFail<IOException>(() => f.Backup());
        f.AssertOld();
    }),
    ("Failure after old deletion never reports success", async f =>
    {
        f.SeedOld();
        bool disconnected = false;
        f.OnProgress = p => { if (p.Stage == "Compressing Folder A...") disconnected = true; };
        f.Check = () => { if (disconnected) throw new IOException("USB disconnected"); };
        await MustFail<IOException>(() => f.Backup());
        Require(!f.Progress.Any(p => p.Percent == 100), "Failure reported success");
        Require(!File.Exists(f.ZipB), "Unexpected second archive");
    }),
    ("Changed source fails and incomplete archive is removed", async f =>
    {
        string file = Path.Combine(f.A, "changing.txt");
        File.WriteAllText(file, "before");
        f.OnProgress = p => { if (p.Stage == "Compressing Folder A...") File.WriteAllText(file, "changed length during backup"); };
        await MustFail<IOException>(() => f.Backup());
        Require(!File.Exists(f.ZipA), "Incomplete archive was not removed");
        Require(!f.Progress.Any(p => p.Percent == 100), "Changed source reported success");
    }),
    ("Thousands of files are all preserved", async f =>
    {
        for (int i = 0; i < 2000; i++) File.WriteAllText(Path.Combine(f.A, $"file {i:0000}.txt"), $"content {i}");
        await f.Backup();
        using var zip = ZipFile.OpenRead(f.ZipA);
        Require(zip.Entries.Count == 2000, "File count mismatch");
        Require(f.Progress.Count > 1000, "Progress was not file based");
    }),
    ("Streaming large file has byte progress and matching content", async f =>
    {
        string file = Path.Combine(f.A, "large.bin");
        byte[] block = RandomNumberGenerator.GetBytes(1024 * 1024);
        using (var output = File.Create(file)) for (int i = 0; i < 32; i++) output.Write(block);
        string expected;
        using (var source = File.OpenRead(file)) expected = Convert.ToHexString(SHA256.HashData(source));
        await f.Backup();
        using var zip = ZipFile.OpenRead(f.ZipA);
        using var entry = zip.GetEntry("large.bin")!.Open();
        Require(Convert.ToHexString(SHA256.HashData(entry)) == expected, "Large file content differs");
        Require(f.Progress.Count(p => p.Percent > 10 && p.Percent < 45) >= 20, "No within-file progress");
    })
};

int failures = 0, passed = 0, skipped = 0;
foreach (var test in tests)
{
    using var fixture = new Fixture();
    try { await test.Run(fixture); Console.WriteLine($"PASS {test.Name}"); passed++; }
    catch (UnauthorizedAccessException) when (test.Name.Contains("link", StringComparison.OrdinalIgnoreCase) || test.Name.Contains("Redirected"))
    { Console.WriteLine($"SKIP {test.Name}: enable Windows Developer Mode for symbolic-link tests."); skipped++; }
    catch (Exception e) { Console.WriteLine($"FAIL {test.Name}\n{e}"); failures++; }
}
Console.WriteLine($"\n{passed} passed, {failures} failed, {skipped} skipped.");
return failures == 0 ? 0 : 1;

static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task MustFail<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}, but operation succeeded.");
}

sealed class Fixture : IProgress<BackupProgress>, IDisposable
{
    public string Root { get; } = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "SimpleUSBBackup-test-" + Guid.NewGuid().ToString("N"));
    public string A => Path.Combine(Root, "Source A");
    public string B => Path.Combine(Root, "Source B");
    public string Usb => Path.Combine(Root, "USB");
    public string BackupDirectory => Path.Combine(Usb, "Backup");
    public string ZipA => Path.Combine(BackupDirectory, "FolderA.zip");
    public string ZipB => Path.Combine(BackupDirectory, "FolderB.zip");
    public List<BackupProgress> Progress { get; } = [];
    public Action<BackupProgress>? OnProgress { get; set; }
    public Action Check { get; set; } = () => { };
    public Func<long> Free { get; set; } = () => 1024L * 1024 * 1024 * 1024;
    public Fixture() { Directory.CreateDirectory(A); Directory.CreateDirectory(B); Directory.CreateDirectory(Usb); }
    public Task Backup() => new BackupService().RunAsync(A, B, Usb, "exFAT", Check, Free, this);
    public void Report(BackupProgress value) { Progress.Add(value); OnProgress?.Invoke(value); }
    public void SeedOld() { Directory.CreateDirectory(BackupDirectory); File.WriteAllText(ZipA, "old A"); File.WriteAllText(ZipB, "old B"); }
    public void AssertOld()
    {
        if (File.ReadAllText(ZipA) != "old A" || File.ReadAllText(ZipB) != "old B") throw new Exception("Previous backups unexpectedly changed");
    }
    public string[] SourceHashes() => new[] { A, B }.SelectMany(p => Directory.GetFiles(p, "*", SearchOption.AllDirectories)).Order()
        .Select(p => p + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
    public void Dispose() => Directory.Delete(Root, true);
}

using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SimpleUSBBackup;

public sealed record UsbDrive(string Root, string VolumeId, string Label, long FreeBytes, string Format)
{
    public string Name => string.IsNullOrWhiteSpace(Label) ? "USB flash drive" : Label;
    public string Details => $"{Root}  ·  {BackupService.FormatBytes(FreeBytes)} free";
    public string Display => $"{Name}  ({Root})";
}

public static class UsbService
{
    public static List<UsbDrive> Detect()
    {
        var result = new List<UsbDrive>();
        foreach (var info in DriveInfo.GetDrives())
        {
            try
            {
                if (info.DriveType != DriveType.Removable || !info.IsReady || !IsUsb(info.Name)) continue;
                string identity = GetVolumeId(info.Name);
                result.Add(new(info.Name, identity, info.VolumeLabel, info.AvailableFreeSpace, info.DriveFormat));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Removal can race with discovery. */ }
        }
        return result;
    }

    public static void Check(UsbDrive drive)
    {
        var info = new DriveInfo(drive.Root);
        if (!info.IsReady || info.DriveType != DriveType.Removable ||
            !GetVolumeId(drive.Root).Equals(drive.VolumeId, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The selected USB was disconnected or replaced. Reconnect it and try again.");
    }
    public static long FreeBytes(UsbDrive drive) { Check(drive); return new DriveInfo(drive.Root).AvailableFreeSpace; }
    public static string GetVolumeId(string root)
    {
        var name = new StringBuilder(1024);
        if (!GetVolumeNameForVolumeMountPoint(root, name, name.Capacity))
            throw new IOException("The USB volume is no longer available.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        return name.ToString();
    }
    private static bool IsUsb(string root)
    {
        using var handle = NativeStorage.Open(@"\\.\" + root.TrimEnd('\\'));
        if (handle.IsInvalid) return false;
        byte[] query = new byte[12]; // StorageDeviceProperty, PropertyStandardQuery
        byte[] descriptor = new byte[1024];
        return NativeStorage.DeviceIoControl(handle, 0x2D1400, query, query.Length, descriptor, descriptor.Length, out int bytes, IntPtr.Zero)
            && bytes >= 32 && BitConverter.ToInt32(descriptor, 28) == 7; // BusTypeUsb
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeNameForVolumeMountPointW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, int bufferLength);
}

internal static class NativeStorage
{
    internal static SafeFileHandle Open(string path) => CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);

    internal static uint DeviceNumber(string path)
    {
        using var handle = Open(path);
        if (handle.IsInvalid) throw new IOException("Cannot access the USB device.");
        byte[] buffer = new byte[12];
        if (!DeviceIoControl(handle, 0x2D1080, null, 0, buffer, buffer.Length, out int bytes, IntPtr.Zero) || bytes < 12)
            throw new IOException("Windows could not identify the USB disk.");
        return BitConverter.ToUInt32(buffer, 4);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, int inputSize,
        byte[] output, int outputSize, out int bytes, IntPtr overlapped);
}

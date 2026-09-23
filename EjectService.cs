using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SimpleUSBBackup;

public static class EjectService
{
    // Identify the physical disk behind the selected volume. All handles are closed before requesting removal.
    // No drive-letter removal, forced dismount, PowerShell, or third-party executable is used.
    public static void Eject(UsbDrive drive)
    {
        UsbService.Check(drive);
        uint number = NativeStorage.DeviceNumber(@"\\.\" + drive.Root.TrimEnd('\\'));
        uint node = FindDiskNode(number);
        uint removable = FindRemovableUsbNode(node);
        UsbService.Check(drive);
        var vetoName = new StringBuilder(512);
        uint result = CM_Request_Device_Eject(removable, out uint veto, vetoName, vetoName.Capacity, 0);
        if (result != 0)
            throw new IOException($"Windows rejected safe removal (code 0x{result:X}, veto {veto}: {vetoName}).");
        // CR_SUCCESS is the documented successful Windows safe-removal result.
    }

    private static uint FindDiskNode(uint number)
    {
        Guid diskClass = new("53f56307-b6bf-11d0-94f2-00a0c91efb8b"); // GUID_DEVINTERFACE_DISK
        IntPtr set = SetupDiGetClassDevs(ref diskClass, null, IntPtr.Zero, 0x12); // PRESENT | DEVICEINTERFACE
        if (set == new IntPtr(-1)) throw new IOException("Windows could not enumerate USB devices.");
        try
        {
            for (uint index = 0; ; index++)
            {
                var item = new DeviceInterfaceData { Size = Marshal.SizeOf<DeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref diskClass, index, ref item))
                {
                    if (Marshal.GetLastWin32Error() == 259) break; // NO_MORE_ITEMS
                    throw new IOException("Windows could not read the USB device list.");
                }
                var data = new DeviceInfoData { Size = Marshal.SizeOf<DeviceInfoData>() };
                SetupDiGetDeviceInterfaceDetail(set, ref item, IntPtr.Zero, 0, out uint required, ref data);
                if (required < 8) continue;
                IntPtr detail = Marshal.AllocHGlobal(checked((int)required));
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref item, detail, required, out _, ref data)) continue;
                    string? path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                    if (path is null) continue;
                    uint candidate;
                    try { candidate = NativeStorage.DeviceNumber(path); }
                    catch (IOException) { continue; }
                    if (candidate == number) return data.DevInst;
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        throw new IOException("The selected USB disk could not be identified for safe removal.");
    }

    private static uint FindRemovableUsbNode(uint node)
    {
        // Walk only the disk's USB storage chain. Never request removal of a hub or controller.
        for (int depth = 0; depth < 8; depth++)
        {
            var id = new StringBuilder(512);
            if (CM_Get_Device_ID(node, id, id.Capacity, 0) != 0) break;
            string name = id.ToString();
            bool storage = name.StartsWith("USBSTOR\\", StringComparison.OrdinalIgnoreCase) || name.StartsWith("SCSI\\", StringComparison.OrdinalIgnoreCase);
            bool usbDevice = name.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase);
            if (!storage && !usbDevice) break;
            uint length = 4;
            uint status = CM_Get_DevNode_Registry_Property(node, 0x10, out _, out uint capabilities, ref length, 0);
            // Removable capability. The USB service property additionally excludes USB hubs.
            var service = new byte[1024];
            uint serviceSize = (uint)service.Length;
            CM_Get_Service_Property(node, 0x05, out _, service, ref serviceSize, 0);
            string driver = Encoding.Unicode.GetString(service).TrimEnd('\0');
            if (driver.Contains("hub", StringComparison.OrdinalIgnoreCase)) break;
            if (status == 0 && (capabilities & 4) != 0) return node;
            if (CM_Get_Parent(out uint parent, node, 0) != 0) break;
            node = parent;
        }
        throw new IOException("Windows did not expose a removable USB device. Use Safely Remove Hardware in the taskbar.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData { public int Size; public Guid ClassGuid; public uint Flags; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoData { public int Size; public Guid ClassGuid; public uint DevInst; public UIntPtr Reserved; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr data, ref Guid guid, uint index, ref DeviceInterfaceData item);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref DeviceInterfaceData item, IntPtr detail, uint size, out uint required, ref DeviceInfoData data);
    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Request_Device_EjectW")]
    private static extern uint CM_Request_Device_Eject(uint node, out uint veto, StringBuilder name, int length, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_IDW")]
    private static extern uint CM_Get_Device_ID(uint node, StringBuilder buffer, int length, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_DevNode_Registry_PropertyW")]
    private static extern uint CM_Get_DevNode_Registry_Property(uint node, uint property, out uint type, out uint value, ref uint length, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_DevNode_Registry_PropertyW")]
    private static extern uint CM_Get_Service_Property(uint node, uint property, out uint type, byte[] value, ref uint length, uint flags);
}

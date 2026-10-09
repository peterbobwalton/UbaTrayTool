using System.Runtime.InteropServices;

namespace UbaTrayTool.Trace;

internal static partial class Native
{
    public const uint FILE_MAP_READ = 0x0004;
    public const uint SYNCHRONIZE = 0x00100000;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint WAIT_OBJECT_0 = 0;
    public const uint WAIT_TIMEOUT = 0x102;

    [LibraryImport("kernel32.dll", EntryPoint = "OpenFileMappingW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr OpenFileMapping(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr MapViewOfFile(IntPtr mapping, uint desiredAccess, uint offsetHigh, uint offsetLow, nuint bytesToMap);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnmapViewOfFile(IntPtr baseAddress);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    public const uint MEM_COMMIT = 0x1000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nuint VirtualQuery(IntPtr address, out MEMORY_BASIC_INFORMATION buffer, nuint length);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageName(IntPtr process, uint flags, [Out] char[] exeName, ref uint size);
}

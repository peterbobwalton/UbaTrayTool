using System.IO.MemoryMappedFiles;

namespace UbaTrayTool.Trace;

/// <summary>
/// The UBA trace channel (Common/Private/UbaTraceChannel.cpp, Windows path): a 256-byte named
/// file mapping (named after the channel, "Default" unless UBT was told otherwise) holding the
/// name of the trace currently being written, guarded by the mutex "Uba{ch}Channel{ch}Mutex".
/// A listener holds the mutex "Uba{ch}Listener"; while one exists UBA reuses it instead of
/// starting UbaVisualizer.exe.
/// </summary>
internal sealed class TraceChannel : IDisposable
{
    private const int ChannelBytes = 256;

    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private readonly Mutex _mutex;
    private Mutex? _listener;

    public string Name { get; }

    public TraceChannel(string channelName)
    {
        Name = channelName;
        _mapping = MemoryMappedFile.CreateOrOpen(channelName, ChannelBytes, MemoryMappedFileAccess.ReadWrite);
        _view = _mapping.CreateViewAccessor(0, ChannelBytes, MemoryMappedFileAccess.Read);
        _mutex = new Mutex(false, $"Uba{channelName}Channel{channelName}Mutex");
    }

    /// <summary>Name of the trace being written right now, or empty when no build is running.</summary>
    public string ReadTraceName()
    {
        bool owned = false;
        try
        {
            try
            {
                owned = _mutex.WaitOne(1000);
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }
            if (!owned)
                return "";

            var chars = new char[ChannelBytes / 2];
            _view.ReadArray(0, chars, 0, chars.Length);
            int length = Array.IndexOf(chars, '\0');
            return new string(chars, 0, length < 0 ? chars.Length : length);
        }
        finally
        {
            if (owned)
                _mutex.ReleaseMutex();
        }
    }

    /// <summary>
    /// When on, UBA sees a live listener on this channel and skips launching UbaVisualizer
    /// (only matters when UBT is configured to launch it).
    /// </summary>
    public bool ActAsListener
    {
        get => _listener is not null;
        set
        {
            if (value == ActAsListener)
                return;
            if (value)
            {
                _listener = new Mutex(false, $"Uba{Name}Listener");
            }
            else
            {
                _listener!.Dispose();
                _listener = null;
            }
        }
    }

    public void Dispose()
    {
        _listener?.Dispose();
        _view.Dispose();
        _mapping.Dispose();
        _mutex.Dispose();
    }
}

/// <summary>
/// A trace that a UBA host is writing into a named, page-file backed mapping. The first u32 is
/// the number of bytes published so far; everything before it is complete entries. The host
/// commits pages as it goes (SEC_RESERVE), so we only ever touch bytes below the published size.
/// </summary>
internal sealed unsafe class NamedTraceMapping : IDisposable
{
    private IntPtr _mapping;
    private byte* _view;
    private IntPtr _host;
    private uint _hostPid;
    private long _nextPidCheck;

    public string Name { get; }

    private NamedTraceMapping(string name, IntPtr mapping, byte* view)
    {
        Name = name;
        _mapping = mapping;
        _view = view;
    }

    public static NamedTraceMapping? TryOpen(string name)
    {
        IntPtr mapping = Native.OpenFileMapping(Native.FILE_MAP_READ, false, name);
        if (mapping == IntPtr.Zero)
            return null;
        IntPtr view = Native.MapViewOfFile(mapping, Native.FILE_MAP_READ, 0, 0, 0);
        if (view == IntPtr.Zero)
        {
            Native.CloseHandle(mapping);
            return null;
        }
        return new NamedTraceMapping(name, mapping, (byte*)view);
    }

    /// <summary>Bytes published by the writer so far.</summary>
    public int PublishedSize
    {
        get
        {
            if (_view == null)
                return 0;
            uint size = Volatile.Read(ref *(uint*)_view);
            return (int)Math.Min(size, int.MaxValue);
        }
    }

    /// <summary>Copies [from, to) out of the mapping.</summary>
    public void CopyTo(byte[] destination, int from, int to)
    {
        if (to <= from)
            return;
        fixed (byte* dst = destination)
            Buffer.MemoryCopy(_view + from, dst + from, destination.Length - from, to - from);
    }

    /// <summary>Starts watching the process that writes the trace (pid from the trace header).</summary>
    public void TrackHost(uint pid)
    {
        if (_host != IntPtr.Zero || pid == 0)
            return;
        _hostPid = pid;
        _host = Native.OpenProcess(Native.SYNCHRONIZE | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (_host == IntPtr.Zero)
            _host = Native.OpenProcess(Native.SYNCHRONIZE, false, pid);
    }

    /// <summary>True once the host process has exited.</summary>
    public bool HostExited
    {
        get
        {
            if (_host != IntPtr.Zero)
                return Native.WaitForSingleObject(_host, 0) == Native.WAIT_OBJECT_0;
            if (_hostPid == 0 || Environment.TickCount64 < _nextPidCheck)
                return false;
            // No handle (no access to the host): fall back to checking the pid still exists.
            _nextPidCheck = Environment.TickCount64 + 2000;
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById((int)_hostPid);
                return false;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }
    }

    public string? HostImagePath()
    {
        if (_host == IntPtr.Zero)
            return null;
        var buffer = new char[1024];
        uint size = (uint)buffer.Length;
        return Native.QueryFullProcessImageName(_host, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
    }

    public void Dispose()
    {
        if (_view != null)
        {
            Native.UnmapViewOfFile((IntPtr)_view);
            _view = null;
        }
        if (_mapping != IntPtr.Zero)
        {
            Native.CloseHandle(_mapping);
            _mapping = IntPtr.Zero;
        }
        if (_host != IntPtr.Zero)
        {
            Native.CloseHandle(_host);
            _host = IntPtr.Zero;
        }
    }
}

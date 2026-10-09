namespace UbaTrayTool.Trace;

internal enum TraceProcessKind : byte { Normal, CacheFetch, Task }

internal sealed class TraceProcess
{
    public uint Id;
    public int SessionIndex;            // raw trace session index
    public TraceSession? Session;       // row it is shown on (reconnected helpers reuse theirs)
    public TraceProcessKind Kind;
    public string Description = "";
    public double Start;                // seconds since trace start
    public double Stop = double.NaN;    // NaN while running
    public uint ExitCode = uint.MaxValue;
    public bool IsRemote;
    public bool IsRacing;
    public string? ReturnedReason;
    public string? KillReason;
    public ProcessStatsSummary Stats;
    public List<(byte Type, string Text)>? LogLines;

    public bool IsRunning => double.IsNaN(Stop);
    public bool Failed => !IsRunning && Kind == TraceProcessKind.Normal && ExitCode != 0 && ReturnedReason is null;
}

internal sealed class TraceSession
{
    public string Name = "";
    public string MachineId = "";
    public uint ClientUid;
    public readonly List<string> Infos = [];
    public string? Notification;
    public string? ProxyName;
    public List<string>? Summary;

    public bool Disconnected;
    public int ActiveCount;
    public int ExitedCount;
    public int MaxConcurrent;

    // Latest SessionUpdate values.
    public double LastUpdateTime = double.NaN;
    public float CpuLoad;
    public ulong MemAvail;
    public ulong MemTotal;
    public ulong TotalSend;
    public ulong TotalRecv;
    public ulong Ping;
    public int ConnectionCount;
    public double SendPerSecond;
    public double RecvPerSecond;

    public ulong FetchedBytes;
    public uint FetchedCount;
    public ulong StoredBytes;
    public uint StoredCount;
    public readonly Dictionary<string, ulong> FetchingActive = [];   // CasKey -> size
}

internal readonly record struct TraceStatus(string Text, byte Type, string? Link);

/// <summary>Everything parsed out of one trace so far (the C# side of uba::TraceView).</summary>
internal sealed class TraceModel
{
    public uint Version;
    public uint HostProcessId;
    public ulong Frequency = 1;
    public DateTimeOffset? StartUtc;
    public double LastEventTime;
    public bool Finished;
    public bool HadSummary;

    public readonly List<TraceSession> Sessions = [];
    public readonly List<string> Strings = [];
    public readonly List<TraceProcess> Processes = [];
    public readonly Dictionary<uint, TraceProcess> Active = [];
    public readonly SortedDictionary<ulong, TraceStatus> Status = [];
    public readonly List<(double Time, int Count)> ActiveCounts = [];

    public uint ProgressTotal;
    public uint ProgressDone;
    public uint ProgressErrors;
    public int MaxActive;
    public int ActiveWork;
    public bool RemoteExecutionDisabled;
    public bool SpawnPaused;
    public string? LastKillReason;
    public int CacheHits;
    public int CacheMisses;
    public List<List<string>> CacheSummaries = [];

    public TraceProcess? LastFailure;

    public int TotalActive;     // normal processes only
    public int TotalExited;

    public double Seconds(ulong ticks) => ticks / (double)Frequency;
}

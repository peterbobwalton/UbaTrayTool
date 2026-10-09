namespace UbaTrayTool.Trace;

internal enum BuildState { Idle, Running, Succeeded, Failed, Unreadable }

internal sealed record RunningAction(uint Id, string Description, double Seconds, bool Remote, string Where, bool Racing);

internal sealed record FailedAction(string Description, uint ExitCode, string? Message, bool Remote, string Where);

internal sealed record HelperSession(
    string Name, string Info, bool IsLocal, bool Disconnected,
    int Active, int Done, int MaxConcurrent,
    float CpuLoad, ulong MemUsed, ulong MemTotal,
    double SendPerSecond, double RecvPerSecond,
    string? Notification);

/// <summary>Immutable view of the current build for the UI thread.</summary>
internal sealed record BuildSnapshot
{
    public static readonly BuildSnapshot Empty = new() { State = BuildState.Idle };

    public BuildState State { get; init; }
    public string? Error { get; init; }
    public bool IsLive { get; init; }
    public string TraceName { get; init; } = "";
    public string? FilePath { get; init; }
    public string? VisualizerPath { get; init; }
    public uint Version { get; init; }
    public DateTimeOffset? StartUtc { get; init; }
    public TimeSpan Elapsed { get; init; }
    public long Sequence { get; init; }

    public uint Total { get; init; }
    public uint Done { get; init; }
    public uint Errors { get; init; }
    public int Running { get; init; }
    public int RunningRemote { get; init; }
    public int Finished { get; init; }
    public int MaxActive { get; init; }
    public int CacheHits { get; init; }
    public int CacheMisses { get; init; }
    public bool RemoteDisabled { get; init; }

    public IReadOnlyList<RunningAction> RunningActions { get; init; } = [];
    public IReadOnlyList<FailedAction> Failures { get; init; } = [];
    public int FailureCount { get; init; }
    public IReadOnlyList<HelperSession> Sessions { get; init; } = [];
    public IReadOnlyList<string> StatusLines { get; init; } = [];
    /// <summary>Running-process count over the build, bucketed for a small chart.</summary>
    public IReadOnlyList<float> Concurrency { get; init; } = [];

    /// <summary>0..1 when the total is known.</summary>
    public double? Fraction => Total > 0 ? Math.Clamp(Done / (double)Total, 0, 1) : null;

    public bool HasBuild => State is not BuildState.Idle;
}

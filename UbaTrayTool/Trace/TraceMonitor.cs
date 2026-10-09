namespace UbaTrayTool.Trace;

/// <summary>
/// Background poller: follows the UBA trace channel, reads whichever trace is live (or a trace
/// file the user opened) and publishes <see cref="BuildSnapshot"/>s.
/// </summary>
internal sealed class TraceMonitor : IDisposable
{
    public const string DefaultChannel = "Default";
    private const int PollMs = 500;
    /// <summary>One card per logical processor, as UbaVisualizer has one lane per core.</summary>
    public static int MaxRunningRows { get; } = Math.Clamp(Environment.ProcessorCount, 8, 256);
    private const int MaxFailureRows = 10;
    private const int ConcurrencyBuckets = 120;

    private readonly object _lock = new();
    private readonly System.Threading.Timer _timer;
    private TraceChannel? _channel;
    private string _channelName;
    private bool _actAsListener;

    // Current trace (live or file).
    private NamedTraceMapping? _live;
    private string _lastLiveName = "";
    private byte[] _buffer = new byte[1 << 20];
    private int _length;
    private TraceParser? _parser;
    private string _traceName = "";
    private string? _filePath;
    private bool _isLive;
    private bool _previewRunning;
    private string? _visualizerPath;
    private bool _visualizerSearched;
    private long _sequence;
    private bool _disposed;

    public event Action<BuildSnapshot>? Updated;
    /// <summary>Raised once when a live build ends.</summary>
    public event Action<BuildSnapshot>? BuildEnded;
    /// <summary>Raised when a new live build starts.</summary>
    public event Action<BuildSnapshot>? BuildStarted;

    public BuildSnapshot Current { get; private set; } = BuildSnapshot.Empty;

    public TraceMonitor(string channelName = DefaultChannel)
    {
        _channelName = channelName;
        _timer = new System.Threading.Timer(_ => Poll(), null, 0, PollMs);
    }

    public bool ActAsListener
    {
        get => _actAsListener;
        set
        {
            lock (_lock)
            {
                _actAsListener = value;
                if (_channel is not null)
                    _channel.ActAsListener = value;
            }
        }
    }

    /// <summary>Loads a saved .uba trace. It is shown until the next live build starts.</summary>
    /// <param name="previewFraction">For previews/screenshots: read only this share of the file and show it as a running build.</param>
    public void OpenFile(string path, double? previewFraction = null)
    {
        byte[] data = File.ReadAllBytes(path);
        lock (_lock)
        {
            CloseLive();
            _buffer = data;
            _length = data.Length;
            _parser = new TraceParser();
            _previewRunning = previewFraction is not null;
            _parser.Parse(_buffer, previewFraction is { } f ? (int)(_length * Math.Clamp(f, 0, 1)) : _length);
            if (!_previewRunning)
                _parser.StopAllActive();
            _traceName = Path.GetFileNameWithoutExtension(path);
            _filePath = path;
            _isLive = false;
            Publish();
        }
    }

    /// <summary>Plain-text dump of what was parsed, for checking the parser against UbaVisualizer.</summary>
    public string Describe()
    {
        lock (_lock)
        {
            if (_parser is null)
                return "No trace.";
            TraceModel m = _parser.Model;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Trace: {_traceName}  version {m.Version}  host pid {m.HostProcessId}  freq {m.Frequency}");
            sb.AppendLine($"Start: {m.StartUtc?.ToLocalTime():yyyy-MM-dd HH:mm:ss}  duration {m.LastEventTime:0.00}s  parsed {_parser.Position}/{_length} bytes  summary {m.HadSummary}");
            if (_parser.Error is not null)
                sb.AppendLine($"ERROR: {_parser.Error}");
            sb.AppendLine($"Progress: {m.ProgressDone}/{m.ProgressTotal}  errors {m.ProgressErrors}  exited {m.TotalExited}  active {m.TotalActive}  peak {m.MaxActive}");
            sb.AppendLine($"Processes: {m.Processes.Count} (normal {m.Processes.Count(p => p.Kind == TraceProcessKind.Normal)}, cache {m.Processes.Count(p => p.Kind == TraceProcessKind.CacheFetch)}, task {m.Processes.Count(p => p.Kind == TraceProcessKind.Task)})  cache hits {m.CacheHits} misses {m.CacheMisses}  strings {m.Strings.Count}");
            foreach (TraceSession s in m.Sessions)
                sb.AppendLine($"Session '{s.Name}' [{string.Join(" | ", s.Infos)}] done {s.ExitedCount} max {s.MaxConcurrent} cpu {s.CpuLoad:P0} mem {s.MemAvail}/{s.MemTotal} disc {s.Disconnected}");
            foreach (var st in m.Status)
                sb.AppendLine($"Status {st.Key >> 32},{st.Key & 0xffffffff}: {st.Value.Text}");
            foreach (TraceProcess p in m.Processes.Where(p => p.Failed).Take(20))
                sb.AppendLine($"FAILED {p.Description} exit {p.ExitCode}: {p.LogLines?.FirstOrDefault().Text}");
            foreach (TraceProcess p in m.Processes.OrderByDescending(p => p.Stop - p.Start).Take(15))
                sb.AppendLine($"  {p.Stop - p.Start,8:0.00}s  {p.Kind,-10} {p.Description}");
            return sb.ToString();
        }
    }

    /// <summary>Writes the current trace bytes as a .uba file UbaVisualizer can open.</summary>
    public bool SaveTrace(string path)
    {
        lock (_lock)
        {
            if (_parser is null || _length == 0)
                return false;
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            fs.Write(_buffer, 0, _length);
            return true;
        }
    }

    private void Poll()
    {
        if (!Monitor.TryEnter(_lock))
            return;     // previous poll still running
        try
        {
            if (_disposed)
                return;
            bool changed = PollChannel();
            changed |= PollLive();
            if (changed || (_isLive && _live is not null))
                Publish();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"UbaTrayTool poll failed: {ex}");
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    private bool PollChannel()
    {
        try
        {
            _channel ??= new TraceChannel(_channelName) { ActAsListener = _actAsListener };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            return false;
        }

        string name = _channel.ReadTraceName();
        if (name.Length == 0 || name == _lastLiveName)
            return false;

        NamedTraceMapping? mapping = NamedTraceMapping.TryOpen(name);
        if (mapping is null)
            return false;   // writer gone already, or not created yet: retry next poll

        CloseLive();
        _previewRunning = false;
        _lastLiveName = name;
        _live = mapping;
        _length = 0;
        _parser = new TraceParser();
        _traceName = name;
        _filePath = null;
        _isLive = true;
        _visualizerPath = null;
        _visualizerSearched = false;
        // Announce the build before reading it, so BuildStarted always comes before BuildEnded.
        BuildSnapshot started = Publish();
        BuildStarted?.Invoke(started);
        return true;
    }

    private bool PollLive()
    {
        if (_live is null || _parser is null)
            return false;

        bool hostExited = _live.HostExited;     // check before reading so no final bytes are missed
        int size = _live.PublishedSize;
        bool changed = false;
        if (size > _length)
        {
            EnsureCapacity(size);
            _live.CopyTo(_buffer, _length, size);
            _length = size;
            changed = _parser.Parse(_buffer, _length);
        }

        if (_parser.Model.HostProcessId != 0)
        {
            _live.TrackHost(_parser.Model.HostProcessId);
            if (!_visualizerSearched)
            {
                // Walks folders, so only once per build.
                _visualizerSearched = true;
                _visualizerPath = VisualizerLocator.FromHost(_live.HostImagePath());
            }
        }

        bool ended = hostExited || _parser.Model.HadSummary || _parser.Error is not null;
        if (ended)
        {
            if (!_parser.Model.Finished)
                _parser.StopAllActive();
            CloseLive();
            BuildSnapshot final = Publish();
            BuildEnded?.Invoke(final);
            return true;
        }
        return changed;
    }

    private void EnsureCapacity(int size)
    {
        if (size <= _buffer.Length)
            return;
        int newSize = _buffer.Length;
        while (newSize < size)
            newSize = newSize < (1 << 30) ? newSize * 2 : int.MaxValue;
        Array.Resize(ref _buffer, newSize);
    }

    private void CloseLive()
    {
        _live?.Dispose();
        _live = null;
    }

    private BuildSnapshot Publish()
    {
        BuildSnapshot snapshot = BuildSnapshotFromModel();
        Current = snapshot;
        Updated?.Invoke(snapshot);
        return snapshot;
    }

    private BuildSnapshot BuildSnapshotFromModel()
    {
        if (_parser is null)
            return BuildSnapshot.Empty with { Sequence = ++_sequence };

        TraceModel m = _parser.Model;
        bool running = (_live is not null || _previewRunning) && !m.Finished;

        double now = m.LastEventTime;
        if (running && !_previewRunning && m.StartUtc is { } start)
            now = Math.Max(now, (DateTimeOffset.UtcNow - start).TotalSeconds);

        var failures = new List<FailedAction>();
        int failureCount = 0;
        for (int i = m.Processes.Count - 1; i >= 0; i--)
        {
            TraceProcess p = m.Processes[i];
            if (!p.Failed)
                continue;
            failureCount++;
            if (failures.Count < MaxFailureRows)
                failures.Add(new FailedAction(p.Description, p.ExitCode, FirstError(p), p.IsRemote, SessionName(p)));
        }

        var runningActions = m.Active.Values
            .Where(p => p.Kind != TraceProcessKind.CacheFetch && !p.IsIdle)
            .OrderBy(p => p.Start)
            .Take(MaxRunningRows)
            .Select(p => new RunningAction(p.Id, p.Description, Math.Max(0, now - p.Start), p.IsRemote, SessionName(p), p.IsRacing))
            .ToList();

        var sessions = new List<HelperSession>();
        for (int i = 0; i < m.Sessions.Count; i++)
        {
            TraceSession s = m.Sessions[i];
            if (s.Name.Length == 0 && s.ExitedCount == 0 && s.ActiveCount == 0)
                continue;
            ulong used = s.MemTotal > s.MemAvail ? s.MemTotal - s.MemAvail : 0;
            sessions.Add(new HelperSession(
                s.Name.Length > 0 ? s.Name : "Local", string.Join(" ", s.Infos), i == 0, s.Disconnected,
                s.ActiveCount, s.ExitedCount, s.MaxConcurrent, s.CpuLoad, used, s.MemTotal,
                s.SendPerSecond, s.RecvPerSecond, s.Notification));
        }

        BuildState state;
        if (_parser.Error is not null)
            state = BuildState.Unreadable;
        else if (running)
            state = BuildState.Running;
        else if (m.ProgressErrors > 0 || failureCount > 0)
            state = BuildState.Failed;
        else if (m.StoppedEarly)
            state = BuildState.Stopped;
        else
            state = BuildState.Succeeded;

        int remoteRunning = m.Active.Values.Count(p => p.IsRemote && p.Kind == TraceProcessKind.Normal && !p.IsIdle);

        return new BuildSnapshot
        {
            State = state,
            Error = _parser.Error,
            IsLive = _isLive,
            TraceName = _traceName,
            FilePath = _filePath,
            VisualizerPath = _visualizerPath,
            Version = m.Version,
            StartUtc = m.StartUtc,
            Elapsed = TimeSpan.FromSeconds(Math.Max(0, now)),
            Sequence = ++_sequence,
            Total = m.ProgressTotal,
            Done = m.ProgressDone,
            Errors = Math.Max(m.ProgressErrors, (uint)failureCount),
            Running = m.TotalActive,
            RunningRemote = remoteRunning,
            Finished = m.TotalExited,
            MaxActive = m.MaxActive,
            CacheHits = m.CacheHits,
            CacheMisses = m.CacheMisses,
            RemoteDisabled = m.RemoteExecutionDisabled,
            RunningActions = runningActions,
            Failures = failures,
            FailureCount = failureCount,
            Sessions = sessions,
            StatusLines = m.Status.Values.Select(s => s.Text.Trim()).Where(t => t.Length > 0).ToList(),
            Concurrency = Bucket(m.ActiveCounts, now),
        };
    }

    private static string SessionName(TraceProcess p)
    {
        if (p.Session is { Name.Length: > 0 } s)
            return s.Name;
        return p.SessionIndex == 0 ? "Local" : $"Helper {p.SessionIndex}";
    }

    private static string? FirstError(TraceProcess p)
    {
        if (p.LogLines is null || p.LogLines.Count == 0)
            return p.KillReason;
        foreach ((byte type, string text) in p.LogLines)
            if (type == 0 && !string.IsNullOrWhiteSpace(text))
                return text.Trim();
        foreach ((_, string text) in p.LogLines)
            if (text.Contains("error", StringComparison.OrdinalIgnoreCase))
                return text.Trim();
        return p.LogLines[0].Text.Trim();
    }

    private static float[] Bucket(List<(double Time, int Count)> counts, double duration)
    {
        var buckets = new float[ConcurrencyBuckets];
        if (counts.Count == 0 || duration <= 0)
            return buckets;
        double width = duration / ConcurrencyBuckets;
        int last = 0;
        int index = 0;
        for (int b = 0; b < ConcurrencyBuckets; b++)
        {
            double end = (b + 1) * width;
            int max = last;
            while (index < counts.Count && counts[index].Time <= end)
            {
                last = counts[index].Count;
                max = Math.Max(max, last);
                index++;
            }
            buckets[b] = max;
        }
        return buckets;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _timer.Dispose();
            CloseLive();
            _channel?.Dispose();
            _channel = null;
        }
    }
}

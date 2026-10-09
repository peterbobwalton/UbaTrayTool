namespace UbaTrayTool.Trace;

/// <summary>uba::TraceType (Common/Public/UbaTrace.h). Append-only, so the values are stable.</summary>
internal enum TraceType : byte
{
    SessionAdded, SessionUpdate, ProcessAdded, ProcessExited, ProcessReturned,
    FileFetchBegin, FileFetchEnd, FileStoreBegin, FileStoreEnd, Summary,
    WorkBegin, WorkEnd, String, SessionSummary, ProcessEnvironmentUpdated,
    SessionDisconnect, ProxyCreated, ProxyUsed, FileFetchLight, FileStoreLight,
    StatusUpdate, SessionNotification, CacheBeginFetch, CacheEndFetch, CacheBeginWrite,
    CacheEndWrite, ProgressUpdate, RemoteExecutionDisabled, FileFetchSize, ProcessBreadcrumbs,
    WorkHint, DriveUpdate, CacheSummary, TaskBegin, TaskHint,
    TaskEnd, SchedulerUpdate, SchedulerKillProcess, SessionInfo, StringAdd,
    StatusUpdate2, TableUpdate, ProcessRace,
}

/// <summary>
/// Incremental port of uba::TraceReader::ReadTrace (Visualizer/Private/UbaTraceReader.cpp).
/// Feed it the trace bytes as they are published; it keeps its place between calls.
/// </summary>
internal sealed class TraceParser
{
    /// <summary>Newest trace format this port was written against (UE 5.8, uba::TraceVersion).</summary>
    public const uint MaxVersion = 53;
    /// <summary>Oldest format handled; older ones use layouts this port doesn't implement.</summary>
    public const uint MinVersion = 40;

    // Header: u32 size, u32 version, u32 pid, 7bit systemStartUs, 7bit frequency, 7bit startTime.
    private const int MinHeaderBytes = 12 + 3;

    private readonly List<int> _sessionIndexToSession = [];
    private int _position;      // next unread byte
    private bool _headerRead;

    public TraceModel Model { get; } = new();

    /// <summary>Set when the stream couldn't be parsed; no further data is read.</summary>
    public string? Error { get; private set; }

    public int Position => _position;

    /// <summary>
    /// Parses every complete entry in <paramref name="data"/>[0, <paramref name="length"/>).
    /// For live traces <paramref name="length"/> is the published size, so every entry before it is complete.
    /// Returns true if anything was read.
    /// </summary>
    public bool Parse(byte[] data, int length)
    {
        if (Error is not null || Model.HadSummary)
            return false;
        int start = _position;
        try
        {
            if (!_headerRead)
            {
                if (length < MinHeaderBytes || !HeaderComplete(data, length))
                    return false;
                var header = new TraceByteReader(data, 0, length);
                header.ReadU32();                          // published size
                Model.Version = header.ReadU32();
                if (Model.Version < MinVersion || Model.Version > MaxVersion)
                {
                    Error = Model.Version > MaxVersion
                        ? $"Trace version {Model.Version} is newer than this tool understands ({MaxVersion}). Update UbaTrayTool."
                        : $"Trace version {Model.Version} is too old (needs {MinVersion}+).";
                    return false;
                }
                Model.HostProcessId = header.ReadU32();
                ulong systemStartUs = header.Read7Bit();
                Model.Frequency = Math.Max(1, header.Read7Bit());
                header.Read7Bit();                          // host GetTime() at start
                if (systemStartUs != 0)
                    Model.StartUtc = DateTimeOffset.FromUnixTimeMilliseconds((long)(systemStartUs / 1000));
                _position = header.Position;
                _headerRead = true;
                Model.Sessions.Add(new TraceSession());     // placeholder for session 0, as TraceReader does
            }

            var reader = new TraceByteReader(data, _position, length);
            while (reader.Left > 0)
            {
                int entryStart = reader.Position;
                try
                {
                    if (!ReadEntry(reader))
                    {
                        _position = reader.Position;
                        break;
                    }
                }
                catch (TraceEndOfDataException)
                {
                    // Incomplete trailing entry (file cut short, or not published yet): retry later.
                    reader.Position = entryStart;
                    break;
                }
                _position = reader.Position;
            }
        }
        catch (TraceFormatException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex) when (ex is not TraceEndOfDataException and not OutOfMemoryException)
        {
            Error = $"Could not parse trace at byte {_position}: {ex.Message}";
        }
        return _position != start;
    }

    /// <summary>Marks everything still running as stopped (host gone / end of file).</summary>
    public void StopAllActive()
    {
        foreach (TraceProcess p in Model.Active.Values)
        {
            p.Stop = Model.LastEventTime;
            p.CutOff = true;
            if (p.Kind == TraceProcessKind.Normal && !p.IsIdle)
                Model.StoppedEarly = true;
        }
        Model.Active.Clear();
        Model.TotalActive = 0;
        foreach (TraceSession s in Model.Sessions)
            s.ActiveCount = 0;
        Model.Finished = true;
    }

    private TraceSession Session(int sessionIndex)
    {
        if (sessionIndex < 0 || sessionIndex >= _sessionIndexToSession.Count)
            throw new TraceFormatException($"Unknown session index {sessionIndex}.");
        return Model.Sessions[_sessionIndexToSession[sessionIndex]];
    }

    private TraceSession? SessionByClient(uint clientUid)
    {
        foreach (TraceSession s in Model.Sessions)
            if (s.ClientUid == clientUid)
                return s;
        return null;
    }

    private string StringAt(ulong index) =>
        index < (ulong)Model.Strings.Count ? Model.Strings[(int)index] : "";

    private bool ReadEntry(TraceByteReader r)
    {
        uint v = Model.Version;
        var type = (TraceType)r.ReadByte();
        double time = 0;
        if (type != TraceType.String && type != TraceType.DriveUpdate)
        {
            time = Model.Seconds(r.Read7Bit());
            if (time > Model.LastEventTime)
                Model.LastEventTime = time;
        }

        switch (type)
        {
            case TraceType.SessionAdded:
            {
                string name = r.ReadString();
                string machineId = r.ReadString();
                uint clientUid = r.Read7BitU32();
                int sessionIndex = (int)r.ReadU32();

                int virtualIndex = sessionIndex;
                for (int i = 0; i < Model.Sessions.Count; i++)
                {
                    TraceSession old = Model.Sessions[i];
                    if (old.MachineId != machineId)
                        continue;
                    if (!old.Disconnected)
                        break;
                    // Same helper reconnected: reuse its row.
                    old.Disconnected = false;
                    old.ProxyName = null;
                    old.Notification = null;
                    old.Infos.Clear();
                    old.LastUpdateTime = double.NaN;
                    virtualIndex = i;
                    break;
                }

                while (Model.Sessions.Count <= virtualIndex)
                    Model.Sessions.Add(new TraceSession());
                while (_sessionIndexToSession.Count <= sessionIndex)
                    _sessionIndexToSession.Add(0);
                _sessionIndexToSession[sessionIndex] = virtualIndex;

                TraceSession session = Session(sessionIndex);
                session.Name = name;
                session.MachineId = machineId;
                session.ClientUid = clientUid;
                if (v < 48 && machineId.Length > 0)
                    session.Infos.Add(machineId);
                break;
            }
            case TraceType.SessionInfo:
            {
                int sessionIndex = (int)r.ReadU32();
                string info = r.ReadString();
                Session(sessionIndex).Infos.Add(info);
                break;
            }
            case TraceType.SessionUpdate:
            {
                int sessionIndex = (int)r.Read7Bit();
                int connectionCount = (int)r.Read7Bit();
                ulong send = r.Read7Bit();
                ulong recv = r.Read7Bit();
                ulong ping = r.Read7Bit();
                ulong memAvail = r.Read7Bit();
                ulong memTotal = r.Read7Bit();
                float cpu = BitConverter.UInt32BitsToSingle(r.ReadU32());

                TraceSession s = Session(sessionIndex);
                if (!double.IsNaN(s.LastUpdateTime) && time > s.LastUpdateTime)
                {
                    double dt = time - s.LastUpdateTime;
                    s.SendPerSecond = send >= s.TotalSend ? (send - s.TotalSend) / dt : 0;
                    s.RecvPerSecond = recv >= s.TotalRecv ? (recv - s.TotalRecv) / dt : 0;
                }
                s.LastUpdateTime = time;
                s.ConnectionCount = connectionCount;
                s.TotalSend = Math.Max(send, s.TotalSend);
                s.TotalRecv = Math.Max(recv, s.TotalRecv);
                s.Ping = ping;
                s.MemAvail = memAvail;
                s.MemTotal = memTotal;
                s.CpuLoad = cpu;
                break;
            }
            case TraceType.SessionDisconnect:
            {
                Session((int)r.ReadU32()).Disconnected = true;
                break;
            }
            case TraceType.SessionNotification:
            {
                int sessionIndex = (int)r.ReadU32();
                Session(sessionIndex).Notification = r.ReadString();
                break;
            }
            case TraceType.SessionSummary:
            {
                int sessionIndex = (int)r.ReadU32();
                uint lineCount = r.ReadU32();
                var lines = new List<string>((int)Math.Min(lineCount, 4096));
                for (uint i = 0; i < lineCount; i++)
                    lines.Add(r.ReadString());
                Session(sessionIndex).Summary = lines;
                break;
            }
            case TraceType.ProcessAdded:
            {
                int sessionIndex = (int)r.ReadU32();
                uint id = r.ReadU32();
                string desc = r.ReadString();
                ReadBreadcrumbs(r);
                ProcessBegin(sessionIndex, id, time, desc, TraceProcessKind.Normal);
                break;
            }
            case TraceType.ProcessExited:
            {
                uint id = r.ReadU32();
                uint exitCode = r.ReadU32();
                TraceProcess? p = ProcessEnd(id, time);
                ProcessStatsSummary stats = TraceStats.ReadProcessStats(r, v);
                // Remote: session, storage, kernel. Local (v36+): the same.
                TraceStats.SkipSessionStats(r);
                TraceStats.SkipStorageStats(r);
                TraceStats.SkipKernelStats(r, v);

                List<(byte, string)>? lines = null;
                while (true)
                {
                    byte lineType = r.ReadByte();
                    if (lineType == 255)
                        break;
                    string text = r.ReadString();
                    (lines ??= []).Add((lineType, text));
                }
                if (p is not null)
                {
                    p.ExitCode = exitCode;
                    p.Stats = stats;
                    p.LogLines = lines;
                }
                break;
            }
            case TraceType.ProcessEnvironmentUpdated:
            {
                uint id = r.ReadU32();
                string reason = r.ReadString();
                Model.Active.TryGetValue(id, out TraceProcess? p);
                bool remote = p?.IsRemote ?? false;
                TraceStats.ReadProcessStats(r, v);
                if (remote)
                    TraceStats.SkipSessionStats(r);
                TraceStats.SkipStorageStats(r);
                TraceStats.SkipKernelStats(r, v);
                ReadBreadcrumbs(r);
                if (p is null)
                    break;

                // The process host is reused for a new action: close the old one, open the next.
                p.ExitCode = 0;
                p.Stop = time;
                var next = new TraceProcess
                {
                    Id = id,
                    SessionIndex = p.SessionIndex,
                    Session = p.Session,
                    Kind = TraceProcessKind.Normal,
                    Description = reason == "IDLE" ? "(idle)" : reason,
                    IsIdle = reason == "IDLE",
                    Start = time,
                    IsRemote = p.IsRemote,
                };
                Model.Processes.Add(next);
                Model.Active[id] = next;
                Session(p.SessionIndex).ExitedCount++;
                Model.TotalExited++;
                break;
            }
            case TraceType.ProcessReturned:
            {
                uint id = r.ReadU32();
                string reason = r.ReadString();
                if (reason.Length == 0)
                    reason = "Unknown";
                if (Model.Active.Remove(id, out TraceProcess? p))
                {
                    TraceSession s = Session(p.SessionIndex);
                    if (p.Kind == TraceProcessKind.Normal)
                    {
                        s.ActiveCount = Math.Max(0, s.ActiveCount - 1);
                        Model.TotalActive = Math.Max(0, Model.TotalActive - 1);
                    }
                    p.ExitCode = 0;
                    p.Stop = time;
                    p.ReturnedReason = reason;
                    RecordActiveCount(time);
                }
                break;
            }
            case TraceType.ProcessRace:
            {
                uint id = r.ReadU32();
                r.ReadU32();
                if (Model.Active.TryGetValue(id, out TraceProcess? p))
                    p.IsRacing = true;
                break;
            }
            case TraceType.FileFetchBegin:
            {
                uint client = r.Read7BitU32();
                string key = r.ReadCasKey();
                ulong size = v < 36 ? r.Read7Bit() : 0;
                r.Read7Bit();                               // hint string index
                SessionByClient(client)?.FetchingActive.TryAdd(key, size);
                break;
            }
            case TraceType.FileFetchLight:
            {
                uint client = r.Read7BitU32();
                ulong size = r.Read7Bit();
                if (SessionByClient(client) is { } s)
                {
                    s.FetchedBytes += size;
                    s.FetchedCount++;
                }
                break;
            }
            case TraceType.ProxyCreated:
            case TraceType.ProxyUsed:
            {
                uint client = r.Read7BitU32();
                string proxy = r.ReadString();
                if (SessionByClient(client) is { } s)
                    s.ProxyName = proxy;
                break;
            }
            case TraceType.FileFetchSize:
            {
                uint client = r.Read7BitU32();
                string key = r.ReadCasKey();
                ulong size = r.Read7Bit();
                if (SessionByClient(client) is { } s && s.FetchingActive.ContainsKey(key))
                    s.FetchingActive[key] = size;
                break;
            }
            case TraceType.FileFetchEnd:
            {
                uint client = r.Read7BitU32();
                string key = r.ReadCasKey();
                if (SessionByClient(client) is { } s && s.FetchingActive.Remove(key, out ulong size))
                {
                    s.FetchedBytes += size;
                    s.FetchedCount++;
                }
                break;
            }
            case TraceType.FileStoreBegin:
            {
                uint client = r.Read7BitU32();
                r.SkipCasKey();
                ulong size = r.Read7Bit();
                r.Read7Bit();                               // hint string index
                if (SessionByClient(client) is { } s)
                {
                    s.StoredBytes += size;
                    s.StoredCount++;
                }
                break;
            }
            case TraceType.FileStoreLight:
            {
                uint client = r.Read7BitU32();
                ulong size = r.Read7Bit();
                if (SessionByClient(client) is { } s)
                {
                    s.StoredBytes += size;
                    s.StoredCount++;
                }
                break;
            }
            case TraceType.FileStoreEnd:
            {
                r.Read7Bit();
                r.SkipCasKey();
                break;
            }
            case TraceType.Summary:
            {
                Model.HadSummary = true;
                StopAllActive();
                return false;
            }
            case TraceType.WorkBegin:
            {
                r.Read7Bit();                               // work index
                r.Read7Bit();                               // description string index
                if (v >= 38)
                    r.ReadU32();                            // colour
                Model.ActiveWork++;
                break;
            }
            case TraceType.WorkEnd:
            {
                r.Read7Bit();
                Model.ActiveWork = Math.Max(0, Model.ActiveWork - 1);
                break;
            }
            case TraceType.WorkHint:
            {
                r.Read7Bit();
                r.Read7Bit();
                r.Read7Bit();
                break;
            }
            case TraceType.TaskBegin:
            {
                uint id = r.Read7BitU32();
                string desc = r.ReadString();
                r.ReadString();                             // details
                r.ReadU32();                                // colour
                ProcessBegin(0, id, time, desc, TraceProcessKind.Task);
                break;
            }
            case TraceType.TaskHint:
            {
                r.Read7Bit();
                r.ReadString();
                break;
            }
            case TraceType.TaskEnd:
            {
                uint id = r.Read7BitU32();
                bool success = r.ReadBool();
                if (ProcessEnd(id, time) is { } p)
                    p.ExitCode = success ? 0u : uint.MaxValue - 1;
                break;
            }
            case TraceType.ProgressUpdate:
            {
                Model.ProgressTotal = r.Read7BitU32();
                Model.ProgressDone = r.Read7BitU32();
                Model.ProgressErrors = r.Read7BitU32();
                break;
            }
            case TraceType.DriveUpdate:
            {
                r.ReadByte();                               // drive letter
                r.ReadByte();                               // busy %
                r.Read7Bit();
                r.Read7Bit();
                r.Read7Bit();
                r.Read7Bit();
                break;
            }
            case TraceType.StatusUpdate:
            {
                ulong row = r.Read7Bit();
                ulong column = r.Read7Bit();
                string text = r.ReadString();
                byte statusType = r.ReadByte();
                string link = r.ReadString();
                Model.Status[(row << 32) | column] = new TraceStatus(text, statusType, link.Length > 0 ? link : null);
                break;
            }
            case TraceType.StatusUpdate2:
            {
                ulong row = r.Read7Bit();
                ulong column = r.Read7Bit();
                string text = StringAt(r.Read7Bit());
                Model.Status[(row << 32) | column] = new TraceStatus(text, 2, null);
                break;
            }
            case TraceType.TableUpdate:
            {
                r.Read7Bit();                               // name
                r.Read7Bit();                               // row char width
                r.Read7Bit();                               // row max count
                ulong count = r.Read7Bit();
                for (ulong i = 0; i < count; i++)
                    r.Read7Bit();
                break;
            }
            case TraceType.ProcessBreadcrumbs:
            {
                r.ReadU32();
                ReadBreadcrumbs(r);
                r.ReadBool();
                break;
            }
            case TraceType.RemoteExecutionDisabled:
            {
                Model.RemoteExecutionDisabled = true;
                break;
            }
            case TraceType.String:
            {
                Model.Strings.Add(r.ReadString());
                break;
            }
            case TraceType.CacheBeginFetch:
            {
                uint id = r.Read7BitU32();
                string desc = r.ReadString();
                ProcessBegin(0, id, time, desc, TraceProcessKind.CacheFetch);
                break;
            }
            case TraceType.CacheEndFetch:
            {
                uint id = r.Read7BitU32();
                bool success = r.ReadBool();
                TraceProcess? p = ProcessEnd(id, time);
                TraceStats.ReadCacheFetchStats(r, v);
                if (success || v >= 29)
                {
                    TraceStats.SkipStorageStats(r);
                    TraceStats.SkipKernelStats(r, v);
                }
                if (p is not null)
                {
                    p.ExitCode = 0;
                    if (!success)
                        p.ReturnedReason = "Cache miss";
                }
                if (success)
                    Model.CacheHits++;
                else
                    Model.CacheMisses++;
                break;
            }
            case TraceType.CacheBeginWrite:
            {
                r.Read7Bit();
                break;
            }
            case TraceType.CacheEndWrite:
            {
                r.Read7Bit();
                r.ReadBool();
                if (v < 45)
                    r.Read7Bit();
                else
                    TraceStats.SkipCacheSendStats(r, v);
                break;
            }
            case TraceType.CacheSummary:
            {
                uint lineCount = r.ReadU32();
                var lines = new List<string>((int)Math.Min(lineCount, 4096));
                for (uint i = 0; i < lineCount; i++)
                    lines.Add(r.ReadString());
                Model.CacheSummaries.Add(lines);
                break;
            }
            case TraceType.SchedulerUpdate:
            {
                Model.SpawnPaused = r.ReadBool();
                break;
            }
            case TraceType.SchedulerKillProcess:
            {
                uint id = r.ReadU32();
                string? reason = v >= 52 ? StringAt(r.Read7Bit()) : null;
                Model.LastKillReason = reason;
                if (Model.Active.TryGetValue(id, out TraceProcess? p))
                    p.KillReason = reason ?? "Killed";
                break;
            }
            case TraceType.StringAdd:
            default:
                throw new TraceFormatException($"Unknown trace entry type {(byte)type} (newer UBA than UbaTrayTool?).");
        }
        return true;
    }

    /// <summary>True once the whole header (3 x u32 + 3 x 7-bit values) has been published.</summary>
    private static bool HeaderComplete(byte[] data, int length)
    {
        int pos = 12;
        for (int value = 0; value < 3; value++)
        {
            while (true)
            {
                if (pos >= length)
                    return false;
                if ((data[pos++] & 0x80) == 0)
                    break;
            }
        }
        return true;
    }

    private void ReadBreadcrumbs(TraceByteReader r)
    {
        if (Model.Version >= 42)
            r.ReadLongString();
        else if (Model.Version >= 38)
        {
            if (r.ReadBool())
                r.SkipString();
            else
            {
                r.Read7Bit();
                r.Skip(r.Read7Bit());
            }
        }
        else if (Model.Version >= 35)
            r.SkipString();
    }

    private void ProcessBegin(int sessionIndex, uint id, double time, string description, TraceProcessKind kind)
    {
        TraceSession session = Session(sessionIndex);
        var p = new TraceProcess
        {
            Id = id,
            SessionIndex = sessionIndex,
            Session = session,
            Kind = kind,
            Description = description,
            Start = time,
            IsRemote = sessionIndex != 0,
        };
        Model.Processes.Add(p);
        Model.Active.TryAdd(id, p);

        if (kind == TraceProcessKind.Normal)
        {
            session.ActiveCount++;
            session.MaxConcurrent = Math.Max(session.MaxConcurrent, session.ActiveCount);
            Model.TotalActive++;
        }
        RecordActiveCount(time);
    }

    private TraceProcess? ProcessEnd(uint id, double time)
    {
        if (!Model.Active.Remove(id, out TraceProcess? p))
            return null;
        if (p.Kind == TraceProcessKind.Normal)
        {
            TraceSession session = Session(p.SessionIndex);
            session.ExitedCount++;
            session.ActiveCount = Math.Max(0, session.ActiveCount - 1);
            Model.TotalExited++;
            Model.TotalActive = Math.Max(0, Model.TotalActive - 1);
        }
        p.Stop = time;
        RecordActiveCount(time);
        return p;
    }

    private void RecordActiveCount(double time)
    {
        Model.ActiveCounts.Add((time, Model.TotalActive));
        Model.MaxActive = Math.Max(Model.MaxActive, Model.TotalActive);
    }
}

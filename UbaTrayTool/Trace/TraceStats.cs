namespace UbaTrayTool.Trace;

/// <summary>The few per-process numbers the tray tool shows; everything else in the stats blob is skipped.</summary>
internal readonly record struct ProcessStatsSummary(ulong WallTime, ulong CpuTime, ulong PeakMemory);

/// <summary>
/// Readers for the stats blobs embedded in ProcessExited / ProcessEnvironmentUpdated / cache entries.
/// The entries carry no length prefix, so the blobs have to be walked field by field exactly as
/// Common/Private/UbaStats.cpp reads them. Only trace versions >= 30 (bitmask encoding) are handled.
/// </summary>
internal static class TraceStats
{
    private static void SkipTimer(TraceByteReader r)
    {
        r.Read7Bit();   // time
        r.Read7Bit();   // count
    }

    private static void SkipTimeAndBytes(TraceByteReader r, uint version)
    {
        r.Read7Bit();
        r.Read7Bit();
        if (version >= 30)
            r.Read7Bit();
    }

    /// <summary>ProcessStats::Read.</summary>
    public static ProcessStatsSummary ReadProcessStats(TraceByteReader r, uint version)
    {
        SkipTimer(r);                       // waitOnResponse
        ulong bits = r.Read7Bit();
        for (int i = 0; i < 64; i++)        // every UBA_PROCESS_STAT is a Timer
            if ((bits & (1ul << i)) != 0)
                SkipTimer(r);

        ulong wallTime = 0, cpuTime = 0, peakMemory = 0;
        if (version >= 37)
        {
            r.Read7Bit();                   // startupTime
            r.Read7Bit();                   // exitTime
            wallTime = r.Read7Bit();
            cpuTime = r.Read7Bit();
            if (version >= 49)
            {
                ulong peakProcess = r.Read7Bit();
                ulong peakJob = r.Read7Bit();
                peakMemory = Math.Max(peakProcess, peakJob);
            }
            r.Read7Bit();                   // detoursMemory
            peakMemory = Math.Max(peakMemory, r.Read7Bit());
            if (version >= 39)
            {
                r.Read7Bit();               // iopsRead
                r.Read7Bit();               // iopsWrite
                r.Read7Bit();               // iopsOther
            }
            r.Read7Bit();                   // hostTotalTime
        }
        else
        {
            throw new TraceFormatException($"Trace version {version} is too old.");
        }
        return new ProcessStatsSummary(wallTime, cpuTime, peakMemory);
    }

    /// <summary>SessionStats::Read: u16 mask, then a Timer per set bit.</summary>
    public static void SkipSessionStats(TraceByteReader r)
    {
        ushort bits = r.ReadU16();
        for (int i = 0; i < 16; i++)
            if ((bits & (1 << i)) != 0)
                SkipTimer(r);
    }

    // StorageStats field types, in UBA_STORAGE_STATS order (true = AtomicU64, false = Timer).
    private static readonly bool[] StorageIsCounter =
    [
        false, false, false, false, false, false, false, false, false, false, false, false,  // timers
        true, true, true, true,             // send/recv cas bytes
        false,                              // createCas
        true, true,                         // createCas bytes
    ];

    /// <summary>StorageStats::Read.</summary>
    public static void SkipStorageStats(TraceByteReader r)
    {
        ulong bits = r.Read7Bit();
        for (int i = 0; i < 64; i++)
        {
            if ((bits & (1ul << i)) == 0)
                continue;
            if (i >= StorageIsCounter.Length)
                throw new TraceFormatException("Unknown storage stat in trace (newer UBA?).");
            if (StorageIsCounter[i])
                r.Read7Bit();
            else
                SkipTimer(r);
        }
    }

    // UBA_KERNEL_STATS indices that are TimeAndBytes (the rest are ExtendedTimer, written as a Timer).
    private static readonly HashSet<int> KernelTimeAndBytes = [2, 3, 4, 14, 22];
    private const int KernelStatCount = 24;

    /// <summary>KernelStats::Read.</summary>
    public static void SkipKernelStats(TraceByteReader r, uint version)
    {
        ulong bits = version < 43 ? r.ReadU16() : r.Read7Bit();
        for (int i = 0; i < 64; i++)
        {
            if ((bits & (1ul << i)) == 0)
                continue;
            if (i >= KernelStatCount)
                throw new TraceFormatException("Unknown kernel stat in trace (newer UBA?).");
            if (KernelTimeAndBytes.Contains(i))
                SkipTimeAndBytes(r, version);
            else
                SkipTimer(r);
        }
    }

    /// <summary>CacheFetchStats::Read. Returns true when fetchEntries had any count.</summary>
    public static bool ReadCacheFetchStats(TraceByteReader r, uint version)
    {
        r.Read7Bit();                               // fetchEntries.time
        bool hadEntries = r.Read7Bit() != 0;        // fetchEntries.count
        SkipTimer(r);                               // fetchCasTable
        if (version >= 28)
            SkipTimer(r);                           // normalizeFile
        SkipTimer(r);                               // testEntry
        SkipTimer(r);                               // fetchOutput
        if (version >= 26)
        {
            r.Read7Bit();                           // fetchBytesRaw
            r.Read7Bit();                           // fetchBytesComp
        }
        return hadEntries;
    }

    /// <summary>CacheSendStats::Read.</summary>
    public static void SkipCacheSendStats(TraceByteReader r, uint version)
    {
        for (int i = 0; i < 6; i++)
            SkipTimer(r);
        SkipTimeAndBytes(r, version);               // sendFile
        SkipTimeAndBytes(r, version);               // sendNormalizedFile
    }
}

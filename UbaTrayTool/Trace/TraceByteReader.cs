namespace UbaTrayTool.Trace;

/// <summary>Thrown when an entry runs past the end of the data that has been published so far.</summary>
internal sealed class TraceEndOfDataException : Exception
{
    public TraceEndOfDataException() : base("Trace entry runs past the end of the available data.") { }
}

/// <summary>Thrown when the trace stream holds something this reader does not understand.</summary>
internal sealed class TraceFormatException(string message) : Exception(message);

/// <summary>
/// Port of uba::BinaryReader (Core/Public/UbaBinaryReaderWriter.h): little-endian fixed-size
/// values, LEB128-style "7-bit encoded" integers, and strings stored as a UTF-16 character
/// count followed by each character in 1-3 UTF-8 style bytes.
/// </summary>
internal sealed class TraceByteReader(byte[] data, int position, int end)
{
    private readonly byte[] _data = data;

    public int Position { get; set; } = position;
    public int End { get; } = end;
    public int Left => End - Position;

    private void Need(int count)
    {
        if (Position + count > End)
            throw new TraceEndOfDataException();
    }

    public byte ReadByte()
    {
        Need(1);
        return _data[Position++];
    }

    public bool ReadBool() => ReadByte() != 0;

    public ushort ReadU16()
    {
        Need(2);
        ushort v = BitConverter.ToUInt16(_data, Position);
        Position += 2;
        return v;
    }

    public uint ReadU32()
    {
        Need(4);
        uint v = BitConverter.ToUInt32(_data, Position);
        Position += 4;
        return v;
    }

    public ulong ReadU64()
    {
        Need(8);
        ulong v = BitConverter.ToUInt64(_data, Position);
        Position += 8;
        return v;
    }

    public ulong Read7Bit()
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            byte b = ReadByte();
            if (shift < 64)
                result |= (ulong)(b & 0x7f) << shift;
            shift += 7;
            if ((b & 0x80) == 0)
                return result;
            if (shift > 70)
                throw new TraceFormatException("Corrupt 7-bit encoded value.");
        }
    }

    public uint Read7BitU32() => (uint)Read7Bit();

    public void Skip(ulong count)
    {
        if (count > (ulong)Left)
            throw new TraceEndOfDataException();
        Position += (int)count;
    }

    public void SkipCasKey() => Skip(20);

    /// <summary>A 20 byte CasKey, returned as a string so it can key a dictionary.</summary>
    public string ReadCasKey()
    {
        Need(20);
        string key = Convert.ToHexString(_data, Position, 20);
        Position += 20;
        return key;
    }

    public string ReadString()
    {
        ulong charCount = Read7Bit();
        if (charCount > (ulong)Left)   // every char takes at least one byte
            throw new TraceEndOfDataException();
        if (charCount == 0)
            return string.Empty;

        var chars = new char[(int)charCount];
        for (int i = 0; i < chars.Length; i++)
        {
            byte a = ReadByte();
            if (a <= 127)
            {
                chars[i] = (char)a;
                continue;
            }
            byte b = ReadByte();
            if (a >= 192 && a <= 223)
            {
                chars[i] = (char)((a - 192) * 64 + (b - 128));
                continue;
            }
            byte c = ReadByte();
            chars[i] = a >= 224 && a <= 239
                ? (char)((a - 224) * 4096 + (b - 128) * 64 + (c - 128))
                : '�';
        }
        return new string(chars);
    }

    public void SkipString() => ReadString();

    /// <summary>
    /// BinaryReader::ReadLongString. Short strings are stored as normal strings; long ones are
    /// Oodle-compressed, which we can't decode here, so they come back as null (only used for
    /// breadcrumbs, which this tool doesn't need).
    /// </summary>
    public string? ReadLongString()
    {
        byte charSize = ReadByte();
        if (charSize == 0)
            return ReadString();
        Read7Bit();                 // character count
        ulong compressedSize = Read7Bit();
        Skip(compressedSize);
        return null;
    }
}

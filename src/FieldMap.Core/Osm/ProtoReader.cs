namespace FieldMap.Core.Osm;

/// <summary>A minimal protocol-buffers wire-format reader over a byte array (enough for OSM PBF and Mapbox Vector Tiles).</summary>
internal struct ProtoReader
{
    private readonly byte[] _buf;
    private int _pos;
    private readonly int _end;

    public ProtoReader(byte[] buffer) : this(buffer, 0, buffer.Length)
    {
    }

    public ProtoReader(byte[] buffer, int offset, int length)
    {
        _buf = buffer;
        _pos = offset;
        _end = offset + length;
    }

    public bool Next(out int field, out int wireType)
    {
        if (_pos >= _end)
        {
            field = wireType = 0;
            return false;
        }
        var key = ReadVarint();
        field = (int)(key >> 3);
        wireType = (int)(key & 7);
        return true;
    }

    public ulong ReadVarint()
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            if (_pos >= _end)
                throw new InvalidDataException("Truncated varint.");
            var b = _buf[_pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;
            shift += 7;
            if (shift > 63)
                throw new InvalidDataException("Varint too long.");
        }
    }

    public long ReadSInt64()
    {
        var v = ReadVarint();
        return (long)(v >> 1) ^ -(long)(v & 1);
    }

    public long ReadInt64() => (long)ReadVarint();

    public int ReadInt32() => (int)ReadVarint();

    public uint ReadUInt32() => (uint)ReadVarint();

    public double ReadDouble()
    {
        var v = BitConverter.ToDouble(_buf, _pos);
        _pos += 8;
        return v;
    }

    public float ReadFloat()
    {
        var v = BitConverter.ToSingle(_buf, _pos);
        _pos += 4;
        return v;
    }

    /// <summary>A length-delimited field as a sub-reader.</summary>
    public ProtoReader ReadMessage()
    {
        var len = (int)ReadVarint();
        if (_pos + len > _end)
            throw new InvalidDataException("Truncated message.");
        var r = new ProtoReader(_buf, _pos, len);
        _pos += len;
        return r;
    }

    public (int Offset, int Length) ReadBytesRange()
    {
        var len = (int)ReadVarint();
        if (_pos + len > _end)
            throw new InvalidDataException("Truncated bytes.");
        var r = (_pos, len);
        _pos += len;
        return r;
    }

    public byte[] ReadBytes()
    {
        var (o, l) = ReadBytesRange();
        return _buf.AsSpan(o, l).ToArray();
    }

    public string ReadString()
    {
        var (o, l) = ReadBytesRange();
        return System.Text.Encoding.UTF8.GetString(_buf, o, l);
    }

    public byte[] Buffer => _buf;

    public void Skip(int wireType)
    {
        switch (wireType)
        {
            case 0: ReadVarint(); break;
            case 1: _pos += 8; break;
            case 2: _pos += (int)ReadVarint(); break;
            case 5: _pos += 4; break;
            default: throw new InvalidDataException($"Unsupported wire type {wireType}.");
        }
    }

    public bool AtEnd => _pos >= _end;
}

/// <summary>A minimal protocol-buffers writer (for Mapbox Vector Tiles).</summary>
internal sealed class ProtoWriter
{
    private readonly MemoryStream _ms = new();

    public void Varint(ulong v)
    {
        while (v >= 0x80)
        {
            _ms.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }
        _ms.WriteByte((byte)v);
    }

    public void Key(int field, int wireType) => Varint((ulong)((field << 3) | wireType));

    public void UInt(int field, ulong v)
    {
        Key(field, 0);
        Varint(v);
    }

    public void SInt(int field, long v)
    {
        Key(field, 0);
        Varint(ZigZag(v));
    }

    public static ulong ZigZag(long v) => (ulong)((v << 1) ^ (v >> 63));

    public void Bytes(int field, ReadOnlySpan<byte> data)
    {
        Key(field, 2);
        Varint((ulong)data.Length);
        _ms.Write(data);
    }

    public void String(int field, string s) => Bytes(field, System.Text.Encoding.UTF8.GetBytes(s));

    public void Double(int field, double v)
    {
        Key(field, 1);
        _ms.Write(BitConverter.GetBytes(v));
    }

    public void Message(int field, ProtoWriter inner) => Bytes(field, inner.ToArray());

    public void PackedUInt(int field, IEnumerable<uint> values)
    {
        var inner = new ProtoWriter();
        foreach (var v in values)
            inner.Varint(v);
        Bytes(field, inner.ToArray());
    }

    public byte[] ToArray() => _ms.ToArray();
}

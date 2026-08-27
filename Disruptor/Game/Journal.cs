using System.Buffers;
using System.Buffers.Binary;

namespace Disruptor.Game;

/// <summary>이벤트를 바이트로 직렬화합니다.</summary>
public interface IJournalSerializer<in T>
{
    /// <summary>
    /// 이벤트를 직렬화합니다.
    /// </summary>
    /// <returns>쓴 바이트 수. 공간이 부족하면 -1을 반환하면 호출자가 더 큰 버퍼로 재시도합니다.</returns>
    int Serialize(T evt, Span<byte> destination);
}

/// <summary>바이트에서 이벤트를 복원합니다.</summary>
public interface IJournalDeserializer<in T>
{
    void Deserialize(ReadOnlySpan<byte> source, T target);
}

/// <summary>저널 레코드 한 건.</summary>
/// <param name="Sequence">이 이벤트의 전역 시퀀스 번호. 이 순서대로 재생하면 상태가 재현됩니다.</param>
/// <param name="TimestampTicks">기록 시각(UTC ticks)</param>
/// <param name="Payload">직렬화된 이벤트</param>
public readonly record struct JournalRecord(long Sequence, long TimestampTicks, ReadOnlyMemory<byte> Payload);

/// <summary>
/// 결정론적 리플레이를 위한 저널 파일 writer.
/// </summary>
/// <remarks>
/// 레코드 형식: <c>[int payloadLength][long sequence][long timestampTicks][payload]</c>.
/// 파일 앞에는 매직 <c>"DJRN"</c> + 버전이 붙습니다.
/// 크래시로 마지막 레코드가 잘려도 리더가 그 지점에서 안전하게 멈춥니다.
/// </remarks>
public sealed class JournalWriter : IDisposable
{
    internal const uint Magic = 0x4E524A44; // "DJRN"
    internal const int Version = 1;
    internal const int HeaderSize = 8;
    internal const int RecordHeaderSize = sizeof(int) + sizeof(long) + sizeof(long);

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly byte[] _headerBuffer = new byte[RecordHeaderSize];

    private long _recordCount;

    public JournalWriter(Stream stream, bool ownsStream = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
            throw new ArgumentException("쓰기 가능한 스트림이어야 합니다.", nameof(stream));

        _stream = stream;
        _ownsStream = ownsStream;

        WriteFileHeader();
    }

    public static JournalWriter CreateFile(string path, int bufferSize = 1 << 16)
    {
        var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read,
                                bufferSize, FileOptions.SequentialScan);
        return new JournalWriter(fs, ownsStream: true);
    }

    public long RecordCount => _recordCount;

    private void WriteFileHeader()
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], Version);
        _stream.Write(header);
    }

    public void Write(long sequence, ReadOnlySpan<byte> payload)
        => Write(sequence, DateTime.UtcNow.Ticks, payload);

    public void Write(long sequence, long timestampTicks, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = _headerBuffer;
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        BinaryPrimitives.WriteInt64LittleEndian(header[4..], sequence);
        BinaryPrimitives.WriteInt64LittleEndian(header[12..], timestampTicks);

        _stream.Write(header);
        _stream.Write(payload);
        _recordCount++;
    }

    /// <summary>버퍼를 디스크로 내려보냅니다. 배치의 끝에서만 호출해 I/O 비용을 1/N로 줄이세요.</summary>
    public void Flush() => _stream.Flush();

    /// <summary>OS 캐시까지 강제로 내려보냅니다. 비용이 크므로 꼭 필요할 때만 사용하세요.</summary>
    public void FlushToDisk()
    {
        _stream.Flush();
        if (_stream is FileStream fs)
            fs.Flush(flushToDisk: true);
    }

    public void Dispose()
    {
        Flush();
        if (_ownsStream)
            _stream.Dispose();
    }
}

/// <summary>
/// 이벤트를 저널에 기록하는 핸들러.
/// </summary>
/// <remarks>
/// <b>로직 핸들러보다 반드시 먼저 실행되어야 하므로</b>
/// <c>HandleEventsWith(journal).Then(logic)</c> 형태로 첫 단계에 두세요.
/// 그래야 "저널에 기록된 뒤에만 로직이 커밋된다"가 보장됩니다.
/// <para>
/// 시퀀스 번호가 곧 전역 실행 순서이므로, 이 저널만으로
/// 버그 재현 / 핫 스탠바이 복제 / 치팅 검증 / 크래시 복구가 모두 가능해집니다.
/// </para>
/// </remarks>
public sealed class JournalHandler<T> : IEventHandler<T> where T : class
{
    private readonly JournalWriter _writer;
    private readonly IJournalSerializer<T> _serializer;
    private readonly bool _flushToDiskOnBatchEnd;

    private byte[] _buffer;
    private long _pendingSinceLastFlush;

    public JournalHandler(
        JournalWriter writer,
        IJournalSerializer<T> serializer,
        int initialBufferSize = 4096,
        bool flushToDiskOnBatchEnd = false)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(serializer);

        _writer = writer;
        _serializer = serializer;
        _flushToDiskOnBatchEnd = flushToDiskOnBatchEnd;
        _buffer = ArrayPool<byte>.Shared.Rent(initialBufferSize);
    }

    public void OnEvent(T data, long sequence, bool endOfBatch)
    {
        int written = Serialize(data);
        _writer.Write(sequence, _buffer.AsSpan(0, written));
        _pendingSinceLastFlush++;

        if (endOfBatch)
            Flush();
    }

    /// <summary>이벤트가 없어도 주기적으로 flush 해서 유휴 구간의 데이터 유실을 막습니다.</summary>
    public void OnTimeout(long lastProcessedSequence)
    {
        if (_pendingSinceLastFlush > 0)
            Flush();
    }

    public void OnShutdown()
    {
        Flush();
        ArrayPool<byte>.Shared.Return(_buffer);
    }

    private void Flush()
    {
        if (_flushToDiskOnBatchEnd)
            _writer.FlushToDisk();
        else
            _writer.Flush();

        _pendingSinceLastFlush = 0;
    }

    private int Serialize(T data)
    {
        while (true)
        {
            int written = _serializer.Serialize(data, _buffer);
            if (written >= 0)
                return written;

            // 버퍼가 부족하면 두 배로 늘려 재시도한다.
            byte[] bigger = ArrayPool<byte>.Shared.Rent(_buffer.Length * 2);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = bigger;
        }
    }
}

/// <summary>
/// 저널을 읽어 이벤트를 재생합니다.
/// </summary>
public static class JournalReader
{
    /// <summary>저널 레코드를 순서대로 읽습니다.</summary>
    /// <remarks>
    /// 반환되는 <see cref="JournalRecord.Payload"/>는 <b>다음 반복에서 재사용되는 버퍼</b>를 가리킵니다.
    /// 보관하려면 복사하세요.
    /// </remarks>
    public static IEnumerable<JournalRecord> Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[JournalWriter.HeaderSize];
        if (!ReadExactly(stream, header))
            yield break;

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        int version = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));

        if (magic != JournalWriter.Magic)
            throw new InvalidDataException("저널 파일이 아닙니다 (매직 불일치).");
        if (version != JournalWriter.Version)
            throw new InvalidDataException($"지원하지 않는 저널 버전입니다: {version}");

        var recordHeader = new byte[JournalWriter.RecordHeaderSize];
        byte[] payload = new byte[4096];

        while (true)
        {
            // 크래시로 잘린 레코드는 여기서 안전하게 종료된다.
            if (!ReadExactly(stream, recordHeader))
                yield break;

            int length = BinaryPrimitives.ReadInt32LittleEndian(recordHeader);
            long sequence = BinaryPrimitives.ReadInt64LittleEndian(recordHeader.AsSpan(4));
            long ticks = BinaryPrimitives.ReadInt64LittleEndian(recordHeader.AsSpan(12));

            if (length < 0)
                throw new InvalidDataException($"저널 레코드 길이가 잘못되었습니다: {length}");

            if (payload.Length < length)
                payload = new byte[Math.Max(length, payload.Length * 2)];

            if (!ReadExactly(stream, payload.AsSpan(0, length)))
                yield break;

            yield return new JournalRecord(sequence, ticks, payload.AsMemory(0, length));
        }
    }

    /// <summary>저널을 읽어 파일로부터 재생합니다.</summary>
    public static IEnumerable<JournalRecord> ReadFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                      1 << 16, FileOptions.SequentialScan);
        foreach (var record in Read(fs))
            yield return record;
    }

    /// <summary>
    /// 저널을 읽어 핸들러에 동일한 순서로 재생합니다. 재생 후 상태는 원본과 동일해야 합니다.
    /// </summary>
    public static long Replay<T>(
        Stream stream,
        IJournalDeserializer<T> deserializer,
        Func<T> eventFactory,
        IEventHandler<T> handler) where T : class
    {
        ArgumentNullException.ThrowIfNull(deserializer);
        ArgumentNullException.ThrowIfNull(eventFactory);
        ArgumentNullException.ThrowIfNull(handler);

        T reusable = eventFactory();
        long count = 0;

        handler.OnStart();
        try
        {
            foreach (var record in Read(stream))
            {
                deserializer.Deserialize(record.Payload.Span, reusable);
                handler.OnEvent(reusable, record.Sequence, endOfBatch: true);
                count++;
            }
        }
        finally
        {
            handler.OnShutdown();
        }

        return count;
    }

    private static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer[total..]);
            if (read == 0)
                return false; // EOF (잘린 레코드 포함)
            total += read;
        }
        return true;
    }
}

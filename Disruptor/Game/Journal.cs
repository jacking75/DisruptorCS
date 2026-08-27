using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;

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

/// <summary>손상된 저널을 만났을 때의 동작.</summary>
public enum JournalReadMode
{
    /// <summary>손상 지점에서 예외를 던집니다. 검증·감사 용도의 기본값입니다.</summary>
    ThrowAtCorruption,

    /// <summary>
    /// 손상 지점에서 조용히 읽기를 종료합니다.
    /// 크래시 복구(마지막 유효 지점까지 재생) 용도입니다.
    /// </summary>
    TruncateAtCorruption,
}

/// <summary>저널 읽기 옵션.</summary>
public sealed record JournalReadOptions
{
    public static JournalReadOptions Default { get; } = new();

    /// <summary>손상 감지 시 동작.</summary>
    public JournalReadMode Mode { get; init; } = JournalReadMode.ThrowAtCorruption;

    /// <summary>
    /// 시퀀스가 단조 증가하는지 검증할지 여부. 파일 이어붙임·중복 기록을 잡아냅니다.
    /// </summary>
    public bool ValidateMonotonicSequence { get; init; } = true;

    /// <summary>체크섬을 검증할지 여부. 끄면 빨라지지만 내용 손상을 감지하지 못합니다.</summary>
    public bool ValidateChecksum { get; init; } = true;
}

/// <summary>
/// 결정론적 리플레이를 위한 저널 파일 writer.
/// </summary>
/// <remarks>
/// <para><b>레코드 형식 (v2)</b></para>
/// <code>
/// 파일 헤더: [magic "DJRN" 4B][version 4B][maxRecordLength 4B][reserved 4B]
/// 레코드   : [payloadLength 4B][sequence 8B][timestampTicks 8B][payload NB][checksum 8B]
/// </code>
/// <para>
/// 체크섬(XxHash3)은 <b>레코드 헤더 20바이트까지 포함</b>해 계산합니다. 길이·시퀀스 필드가
/// 손상되어도 잡아내기 위해서입니다. v1 포맷(체크섬 없음)도 읽을 수 있습니다.
/// </para>
/// <para>
/// 크래시로 마지막 레코드가 잘려도 리더가 그 지점에서 안전하게 멈춥니다.
/// </para>
/// </remarks>
public sealed class JournalWriter : IDisposable
{
    internal const uint Magic = 0x4E524A44; // "DJRN"
    internal const int Version1 = 1;
    internal const int Version2 = 2;
    internal const int CurrentVersion = Version2;

    internal const int FileHeaderSizeV1 = 8;
    internal const int FileHeaderSizeV2 = 16;
    internal const int RecordHeaderSize = sizeof(int) + sizeof(long) + sizeof(long); // 20
    internal const int ChecksumSize = sizeof(ulong);                                  // 8

    /// <summary>기본 레코드 길이 상한 (16MB). 손상된 길이 필드로 인한 대형 할당을 막습니다.</summary>
    public const int DefaultMaxRecordLength = 16 * 1024 * 1024;

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly byte[] _headerBuffer = new byte[RecordHeaderSize];
    private readonly XxHash3 _hash = new();
    private readonly int _maxRecordLength;

    private long _recordCount;

    public JournalWriter(Stream stream, bool ownsStream = false, int maxRecordLength = DefaultMaxRecordLength)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
            throw new ArgumentException("쓰기 가능한 스트림이어야 합니다.", nameof(stream));
        if (maxRecordLength < 1)
            throw new ArgumentOutOfRangeException(nameof(maxRecordLength));

        _stream = stream;
        _ownsStream = ownsStream;
        _maxRecordLength = maxRecordLength;

        WriteFileHeader();
    }

    public static JournalWriter CreateFile(string path, int bufferSize = 1 << 16,
                                           int maxRecordLength = DefaultMaxRecordLength)
    {
        var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read,
                                bufferSize, FileOptions.SequentialScan);
        return new JournalWriter(fs, ownsStream: true, maxRecordLength);
    }

    public long RecordCount => _recordCount;

    /// <summary>레코드 길이 상한.</summary>
    public int MaxRecordLength => _maxRecordLength;

    private void WriteFileHeader()
    {
        Span<byte> header = stackalloc byte[FileHeaderSizeV2];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], CurrentVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], _maxRecordLength);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], 0); // reserved
        _stream.Write(header);
    }

    public void Write(long sequence, ReadOnlySpan<byte> payload)
        => Write(sequence, DateTime.UtcNow.Ticks, payload);

    public void Write(long sequence, long timestampTicks, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > _maxRecordLength)
            throw new ArgumentException(
                $"레코드 길이 {payload.Length} 가 상한 {_maxRecordLength} 을 초과합니다.", nameof(payload));

        Span<byte> header = _headerBuffer;
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        BinaryPrimitives.WriteInt64LittleEndian(header[4..], sequence);
        BinaryPrimitives.WriteInt64LittleEndian(header[12..], timestampTicks);

        // 헤더까지 해시에 포함해 길이/시퀀스 위조도 잡는다.
        _hash.Reset();
        _hash.Append(header);
        _hash.Append(payload);

        Span<byte> checksum = stackalloc byte[ChecksumSize];
        _hash.GetHashAndReset(checksum);

        _stream.Write(header);
        _stream.Write(payload);
        _stream.Write(checksum);
        _recordCount++;
    }

    /// <summary>버퍼를 스트림으로 내려보냅니다. 배치의 끝에서만 호출해 I/O 비용을 1/N로 줄이세요.</summary>
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
public sealed class JournalHandler<T> : IEventHandler<T>, IDisposable where T : class
{
    private readonly JournalWriter _writer;
    private readonly IJournalSerializer<T> _serializer;
    private readonly bool _flushToDiskOnBatchEnd;

    private byte[] _buffer;
    private long _pendingSinceLastFlush;
    private int _disposed;

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
        Dispose();
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

            if (_buffer.Length >= _writer.MaxRecordLength)
                throw new InvalidOperationException(
                    $"직렬화 결과가 레코드 상한({_writer.MaxRecordLength}B)을 초과합니다.");

            // 버퍼가 부족하면 두 배로 늘려 재시도한다.
            byte[] bigger = ArrayPool<byte>.Shared.Rent(_buffer.Length * 2);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = bigger;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        ArrayPool<byte>.Shared.Return(_buffer);
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
    public static IEnumerable<JournalRecord> Read(Stream stream, JournalReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= JournalReadOptions.Default;

        var fileHeader = new byte[JournalWriter.FileHeaderSizeV1];
        if (!ReadExactly(stream, fileHeader))
            yield break; // 빈 파일

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(fileHeader);
        int version = BinaryPrimitives.ReadInt32LittleEndian(fileHeader.AsSpan(4));

        if (magic != JournalWriter.Magic)
            throw new InvalidDataException("저널 파일이 아닙니다 (매직 불일치).");

        int maxRecordLength = JournalWriter.DefaultMaxRecordLength;
        bool hasChecksum;

        switch (version)
        {
            case JournalWriter.Version1:
                hasChecksum = false;
                break;

            case JournalWriter.Version2:
            {
                hasChecksum = true;
                var rest = new byte[JournalWriter.FileHeaderSizeV2 - JournalWriter.FileHeaderSizeV1];
                if (!ReadExactly(stream, rest))
                    yield break;

                maxRecordLength = BinaryPrimitives.ReadInt32LittleEndian(rest);
                if (maxRecordLength < 1)
                    throw new InvalidDataException($"저널 헤더의 레코드 길이 상한이 잘못되었습니다: {maxRecordLength}");
                break;
            }

            default:
                throw new InvalidDataException($"지원하지 않는 저널 버전입니다: {version}");
        }

        var recordHeader = new byte[JournalWriter.RecordHeaderSize];
        var checksumBuffer = new byte[JournalWriter.ChecksumSize];
        byte[] payload = new byte[4096];
        var hash = new XxHash3();

        long lastSequence = long.MinValue;
        long offset = version == JournalWriter.Version1
            ? JournalWriter.FileHeaderSizeV1
            : JournalWriter.FileHeaderSizeV2;

        while (true)
        {
            // 크래시로 잘린 레코드는 여기서 안전하게 종료된다.
            if (!ReadExactly(stream, recordHeader))
                yield break;

            int length = BinaryPrimitives.ReadInt32LittleEndian(recordHeader);
            long sequence = BinaryPrimitives.ReadInt64LittleEndian(recordHeader.AsSpan(4));
            long ticks = BinaryPrimitives.ReadInt64LittleEndian(recordHeader.AsSpan(12));

            // (1) 길이 상한 — 손상된 길이 필드로 인한 대형 할당을 원천 차단한다.
            if ((uint)length > (uint)maxRecordLength)
            {
                if (options.Mode == JournalReadMode.TruncateAtCorruption)
                    yield break;

                throw new InvalidDataException(
                    $"오프셋 {offset}: 레코드 길이 {length} 가 상한 {maxRecordLength} 을 벗어났습니다 (손상 의심).");
            }

            if (payload.Length < length)
                payload = new byte[Math.Max(length, payload.Length * 2)];

            if (!ReadExactly(stream, payload.AsSpan(0, length)))
                yield break;

            if (hasChecksum)
            {
                if (!ReadExactly(stream, checksumBuffer))
                    yield break;

                // (2) 해시 검증 — 내용 손상 감지. 길이는 맞고 내용만 깨진 레코드를 잡는다.
                if (options.ValidateChecksum)
                {
                    hash.Reset();
                    hash.Append(recordHeader);
                    hash.Append(payload.AsSpan(0, length));

                    Span<byte> computed = stackalloc byte[JournalWriter.ChecksumSize];
                    hash.GetHashAndReset(computed);

                    if (!computed.SequenceEqual(checksumBuffer))
                    {
                        if (options.Mode == JournalReadMode.TruncateAtCorruption)
                            yield break;

                        throw new InvalidDataException(
                            $"seq {sequence} (오프셋 {offset}): 체크섬 불일치 — 저널이 손상되었습니다.");
                    }
                }
            }

            // (3) 단조 검증 — 파일 이어붙임·중복 기록 감지.
            if (options.ValidateMonotonicSequence && sequence <= lastSequence)
            {
                if (options.Mode == JournalReadMode.TruncateAtCorruption)
                    yield break;

                throw new InvalidDataException(
                    $"시퀀스 역행: {lastSequence} → {sequence} (오프셋 {offset}). " +
                    "저널 파일이 섞였거나 중복 기록되었습니다.");
            }

            lastSequence = sequence;
            offset += JournalWriter.RecordHeaderSize + length + (hasChecksum ? JournalWriter.ChecksumSize : 0);

            yield return new JournalRecord(sequence, ticks, payload.AsMemory(0, length));
        }
    }

    /// <summary>저널 파일을 읽습니다.</summary>
    public static IEnumerable<JournalRecord> ReadFile(string path, JournalReadOptions? options = null)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                      1 << 16, FileOptions.SequentialScan);
        foreach (var record in Read(fs, options))
            yield return record;
    }

    /// <summary>
    /// 저널을 검증만 합니다(재생하지 않음). 운영 도구·CI 검사에 사용하세요.
    /// </summary>
    public static JournalVerificationResult Verify(Stream stream)
    {
        long count = 0;
        long first = -1;
        long last = -1;

        try
        {
            foreach (var record in Read(stream, JournalReadOptions.Default))
            {
                if (count == 0) first = record.Sequence;
                last = record.Sequence;
                count++;
            }
        }
        catch (InvalidDataException ex)
        {
            return new JournalVerificationResult(false, count, first, last, ex.Message);
        }

        return new JournalVerificationResult(true, count, first, last, null);
    }

    /// <summary>
    /// 저널을 읽어 핸들러에 동일한 순서로 재생합니다. 재생 후 상태는 원본과 동일해야 합니다.
    /// </summary>
    public static long Replay<T>(
        Stream stream,
        IJournalDeserializer<T> deserializer,
        Func<T> eventFactory,
        IEventHandler<T> handler,
        JournalReadOptions? options = null) where T : class
    {
        ArgumentNullException.ThrowIfNull(deserializer);
        ArgumentNullException.ThrowIfNull(eventFactory);
        ArgumentNullException.ThrowIfNull(handler);

        T reusable = eventFactory();
        long count = 0;

        handler.OnStart();
        try
        {
            foreach (var record in Read(stream, options))
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

/// <summary>저널 검증 결과.</summary>
/// <param name="IsValid">손상이 발견되지 않았는가</param>
/// <param name="RecordCount">읽어낸 레코드 수</param>
/// <param name="FirstSequence">첫 레코드의 시퀀스 (없으면 -1)</param>
/// <param name="LastSequence">마지막 레코드의 시퀀스 (없으면 -1)</param>
/// <param name="Error">손상이 발견된 경우의 설명</param>
public readonly record struct JournalVerificationResult(
    bool IsValid,
    long RecordCount,
    long FirstSequence,
    long LastSequence,
    string? Error)
{
    public override string ToString()
        => IsValid
            ? $"유효: {RecordCount:N0}건, 시퀀스 {FirstSequence}~{LastSequence}"
            : $"손상: {RecordCount:N0}건까지 유효 (시퀀스 {FirstSequence}~{LastSequence}). {Error}";
}

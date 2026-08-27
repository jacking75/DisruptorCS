using System.Buffers.Binary;
using Disruptor.Game;
using Xunit;
using Xunit.Abstractions;

namespace Disruptor.Tests;

/// <summary>
/// §3.1 저널 무결성 — 해시 · 길이 상한 · 시퀀스 단조 검증.
/// </summary>
/// <remarks>
/// 이 세 가지가 없으면 손상된 저널이 (1) 최대 2GB 할당을 시도해 OOM 을 내거나,
/// (2) 조용히 잘못된 상태를 재생하거나, (3) 뒤섞인 파일을 눈치채지 못합니다.
/// 결정론적 리플레이의 존재 이유(정확한 재현)를 부정하는 실패 모드입니다.
/// </remarks>
public class JournalIntegrityTests
{
    private readonly ITestOutputHelper _output;

    public JournalIntegrityTests(ITestOutputHelper output) => _output = output;

    private sealed class Codec : IJournalSerializer<TestEvent>, IJournalDeserializer<TestEvent>
    {
        public int Serialize(TestEvent evt, Span<byte> destination)
        {
            if (destination.Length < 16)
                return -1;

            BinaryPrimitives.WriteInt64LittleEndian(destination, evt.Value);
            BinaryPrimitives.WriteInt64LittleEndian(destination[8..], evt.Sequence);
            return 16;
        }

        public void Deserialize(ReadOnlySpan<byte> source, TestEvent target)
        {
            target.Value = BinaryPrimitives.ReadInt64LittleEndian(source);
            target.Sequence = BinaryPrimitives.ReadInt64LittleEndian(source[8..]);
        }
    }

    private static MemoryStream WriteJournal(int count, out Codec codec)
    {
        codec = new Codec();
        var stream = new MemoryStream();

        using (var writer = new JournalWriter(stream, ownsStream: false))
        {
            var evt = new TestEvent();
            Span<byte> buf = stackalloc byte[16];

            for (int i = 0; i < count; i++)
            {
                evt.Value = i * 7;
                evt.Sequence = i;
                int n = codec.Serialize(evt, buf);
                writer.Write(i, buf[..n]);
            }

            writer.Flush();
        }

        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void RoundTrip_PreservesEveryRecord()
    {
        using MemoryStream stream = WriteJournal(5_000, out Codec codec);

        var replayed = new List<(long seq, long value)>();
        long count = JournalReader.Replay(stream, codec, () => new TestEvent(),
                                          new CapturingHandler(replayed));

        Assert.Equal(5_000, count);
        for (int i = 0; i < 5_000; i++)
        {
            Assert.Equal(i, replayed[i].seq);
            Assert.Equal(i * 7, replayed[i].value);
        }
    }

    /// <summary>
    /// 내용만 깨진 레코드는 길이가 맞으므로 체크섬 없이는 감지되지 않습니다 —
    /// 조용히 잘못된 상태를 재생하는 최악의 실패 모드입니다.
    /// </summary>
    [Fact]
    public void SingleBitFlip_InPayload_IsDetected()
    {
        using MemoryStream original = WriteJournal(100, out _);
        byte[] bytes = original.ToArray();

        // 첫 레코드의 페이로드 중간 바이트를 뒤집는다.
        int payloadOffset = JournalWriter.FileHeaderSizeV2 + JournalWriter.RecordHeaderSize + 4;
        bytes[payloadOffset] ^= 0xFF;

        using var corrupted = new MemoryStream(bytes);

        var ex = Assert.Throws<InvalidDataException>(() => JournalReader.Read(corrupted).ToList());
        Assert.Contains("체크섬", ex.Message);
        _output.WriteLine(ex.Message);
    }

    /// <summary>헤더(길이/시퀀스)가 깨져도 체크섬이 헤더를 포함하므로 감지됩니다.</summary>
    [Fact]
    public void CorruptedSequenceField_IsDetected()
    {
        using MemoryStream original = WriteJournal(100, out _);
        byte[] bytes = original.ToArray();

        // 첫 레코드의 sequence 필드를 조작한다 (헤더 오프셋 +4).
        int sequenceOffset = JournalWriter.FileHeaderSizeV2 + 4;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(sequenceOffset), 999_999);

        using var corrupted = new MemoryStream(bytes);

        Assert.Throws<InvalidDataException>(() => JournalReader.Read(corrupted).ToList());
    }

    /// <summary>
    /// 길이 필드가 깨지면 <c>new byte[length]</c> 가 최대 2GB 할당을 시도합니다.
    /// 상한 검사가 그것을 원천 차단해야 합니다.
    /// </summary>
    [Fact]
    public void CorruptedLengthField_IsRejected_WithoutHugeAllocation()
    {
        using MemoryStream original = WriteJournal(10, out _);
        byte[] bytes = original.ToArray();

        // 첫 레코드의 길이 필드를 2GB 근처로 조작한다.
        int lengthOffset = JournalWriter.FileHeaderSizeV2;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(lengthOffset), int.MaxValue - 8);

        using var corrupted = new MemoryStream(bytes);

        var ex = Assert.Throws<InvalidDataException>(() => JournalReader.Read(corrupted).ToList());
        Assert.Contains("상한", ex.Message);
        _output.WriteLine(ex.Message);
    }

    [Fact]
    public void NegativeLengthField_IsRejected()
    {
        using MemoryStream original = WriteJournal(10, out _);
        byte[] bytes = original.ToArray();

        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(JournalWriter.FileHeaderSizeV2), -1);

        using var corrupted = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => JournalReader.Read(corrupted).ToList());
    }

    /// <summary>파일 이어붙임·중복 기록은 시퀀스 역행으로 드러납니다.</summary>
    [Fact]
    public void ConcatenatedJournals_AreDetectedAsSequenceRegression()
    {
        using MemoryStream first = WriteJournal(50, out _);
        using MemoryStream second = WriteJournal(50, out _);

        // 두 저널을 이어붙인다 (두 번째의 파일 헤더는 레코드처럼 보이게 된다).
        byte[] firstBytes = first.ToArray();
        byte[] secondRecords = second.ToArray()[JournalWriter.FileHeaderSizeV2..];

        var combined = new byte[firstBytes.Length + secondRecords.Length];
        firstBytes.CopyTo(combined, 0);
        secondRecords.CopyTo(combined, firstBytes.Length);

        using var stream = new MemoryStream(combined);

        var ex = Assert.Throws<InvalidDataException>(() => JournalReader.Read(stream).ToList());
        Assert.Contains("시퀀스 역행", ex.Message);
        _output.WriteLine(ex.Message);
    }

    /// <summary>크래시로 잘린 파일은 마지막 유효 레코드까지 안전하게 읽혀야 합니다.</summary>
    [Fact]
    public void TruncatedFile_StopsCleanly_AtLastValidRecord()
    {
        using MemoryStream original = WriteJournal(100, out _);
        byte[] bytes = original.ToArray();

        // 마지막 레코드를 반쯤 자른다.
        byte[] truncated = bytes[..^10];
        using var stream = new MemoryStream(truncated);

        var records = JournalReader.Read(stream).ToList();

        Assert.Equal(99, records.Count);
        Assert.Equal(98, records[^1].Sequence);
    }

    /// <summary>복구 모드에서는 손상 지점에서 조용히 멈춰야 합니다.</summary>
    [Fact]
    public void TruncateAtCorruption_RecoversUpToDamage()
    {
        using MemoryStream original = WriteJournal(100, out _);
        byte[] bytes = original.ToArray();

        // 50번째 레코드의 페이로드를 손상시킨다.
        int recordSize = JournalWriter.RecordHeaderSize + 16 + JournalWriter.ChecksumSize;
        int target = JournalWriter.FileHeaderSizeV2 + (recordSize * 50) + JournalWriter.RecordHeaderSize;
        bytes[target] ^= 0xFF;

        using var stream = new MemoryStream(bytes);

        var records = JournalReader.Read(stream,
            new JournalReadOptions { Mode = JournalReadMode.TruncateAtCorruption }).ToList();

        Assert.Equal(50, records.Count); // 0..49 까지만 유효
        _output.WriteLine($"복구된 레코드 {records.Count}건 (손상 이전까지)");
    }

    [Fact]
    public void Verify_ReportsIntegrityWithoutReplaying()
    {
        using MemoryStream healthy = WriteJournal(200, out _);
        JournalVerificationResult ok = JournalReader.Verify(healthy);

        Assert.True(ok.IsValid);
        Assert.Equal(200, ok.RecordCount);
        Assert.Equal(0, ok.FirstSequence);
        Assert.Equal(199, ok.LastSequence);
        _output.WriteLine(ok.ToString());

        using MemoryStream damagedSource = WriteJournal(200, out _);
        byte[] bytes = damagedSource.ToArray();
        bytes[JournalWriter.FileHeaderSizeV2 + JournalWriter.RecordHeaderSize + 2] ^= 0xFF;

        using var damaged = new MemoryStream(bytes);
        JournalVerificationResult bad = JournalReader.Verify(damaged);

        Assert.False(bad.IsValid);
        Assert.NotNull(bad.Error);
        _output.WriteLine(bad.ToString());
    }

    [Fact]
    public void ForeignFile_IsRejected()
    {
        using var stream = new MemoryStream("이것은 저널이 아닙니다. 절대로."u8.ToArray());
        Assert.Throws<InvalidDataException>(() => JournalReader.Read(stream).ToList());
    }

    [Fact]
    public void EmptyStream_YieldsNothing()
    {
        using var stream = new MemoryStream();
        Assert.Empty(JournalReader.Read(stream));
    }

    [Fact]
    public void OversizedRecord_IsRejectedAtWriteTime()
    {
        using var stream = new MemoryStream();
        using var writer = new JournalWriter(stream, ownsStream: false, maxRecordLength: 64);

        Assert.Throws<ArgumentException>(() => writer.Write(0, new byte[128]));
    }

    private sealed class CapturingHandler : IEventHandler<TestEvent>
    {
        private readonly List<(long, long)> _target;

        public CapturingHandler(List<(long, long)> target) => _target = target;

        public void OnEvent(TestEvent data, long sequence, bool endOfBatch)
            => _target.Add((sequence, data.Value));
    }
}

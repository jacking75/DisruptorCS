using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Disruptor;

/// <summary>
/// False sharing을 방지하기 위해 캐시 라인 패딩된 시퀀스.
/// Java의 AtomicLong + 패딩에 해당합니다.
/// </summary>
/// <remarks>
/// Intel의 스트리밍 프리페처는 인접 캐시 라인 쌍(128바이트)을 함께 로드하므로,
/// 64바이트가 아니라 <b>좌우 128바이트</b>를 확보해야 프리페치로 인한 간섭까지 차단됩니다.
/// 시퀀스 객체의 개수는 (핸들러 수 + 시퀀서 수) 수준이므로 메모리 증가는 무시할 수 있습니다.
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = PaddingBytes + sizeof(long) + PaddingBytes)]
public sealed class Sequence
{
    private const int PaddingBytes = 128;

    public const long InitialCursorValue = -1L;

    // 좌측 128바이트 패딩 + 우측 128바이트 패딩.
    // 객체 헤더(16바이트)까지 감안하면 앞뒤 어느 쪽으로도 다른 객체와 캐시 라인을 공유하지 않는다.
    [FieldOffset(PaddingBytes)]
    private long _value;

    public Sequence(long initialValue = InitialCursorValue)
    {
        _value = initialValue;
    }

    /// <summary>volatile 읽기</summary>
    public long Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _value);
    }

    /// <summary>volatile 쓰기</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetValue(long value)
    {
        Volatile.Write(ref _value, value);
    }

    /// <summary>CAS (Compare-And-Swap) 연산</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool CompareAndSet(long expected, long newValue)
    {
        return Interlocked.CompareExchange(ref _value, newValue, expected) == expected;
    }

    /// <summary>원자적 증가 후 반환</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long IncrementAndGet()
    {
        return Interlocked.Increment(ref _value);
    }

    /// <summary>원자적으로 delta만큼 더하고 결과 반환</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long AddAndGet(long delta)
    {
        return Interlocked.Add(ref _value, delta);
    }

    public override string ToString() => Value.ToString();
}

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Disruptor;

/// <summary>
/// 쓰기 경합이 없는 카운터. 각 셀이 캐시라인을 독점하고, 스레드는 자기 셀에만 기록합니다.
/// </summary>
/// <remarks>
/// <para>
/// 모든 프로듀서가 하나의 <c>Interlocked.Increment</c> 대상을 공유하면 그 카운터 하나가
/// 게시 경로의 스케일링을 깎습니다(캐시라인 핑퐁). 스트라이핑하면 스레드별로 다른 라인에 기록하므로
/// 경합이 해시 충돌 시에만 발생합니다.
/// </para>
/// <para>
/// <b>읽기는 스냅샷이 아닙니다.</b> <see cref="Value"/>는 셀을 순회하며 합산하므로 셀 간에 원자적이지
/// 않습니다. 메트릭·진단 용도로는 무해하지만, 정확한 시점 일치가 필요한 곳에는 쓰지 마세요.
/// </para>
/// </remarks>
public sealed class StripedCounter
{
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct Cell
    {
        [FieldOffset(0)] public long Value;
    }

    private readonly Cell[] _cells;
    private readonly int _mask;

    public StripedCounter()
    {
        int stripes = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, Environment.ProcessorCount));
        _cells = new Cell[stripes];
        _mask = stripes - 1;
    }

    /// <summary>스트라이프 수 (2의 거듭제곱).</summary>
    public int Stripes => _cells.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Increment() => Add(1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(long delta)
    {
        // 스레드 -> 셀 고정 매핑. 같은 스레드는 항상 같은 셀에 기록한다.
        int cell = Environment.CurrentManagedThreadId & _mask;
        Interlocked.Add(ref _cells[cell].Value, delta);
    }

    /// <summary>모든 셀의 합. O(스트라이프 수)이며 시점 일치를 보장하지 않습니다.</summary>
    public long Value
    {
        get
        {
            long sum = 0;
            for (int i = 0; i < _cells.Length; i++)
                sum += Volatile.Read(ref _cells[i].Value);
            return sum;
        }
    }

    public void Reset()
    {
        for (int i = 0; i < _cells.Length; i++)
            Volatile.Write(ref _cells[i].Value, 0);
    }
}

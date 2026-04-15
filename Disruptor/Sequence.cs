using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;


namespace Disruptor;

/// <summary>
/// False sharing을 방지하기 위해 캐시 라인(64바이트) 패딩된 시퀀스.
/// Java의 AtomicLong + 패딩에 해당합니다.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 192)]
public sealed class Sequence
{
	public const long InitialCursorValue = -1L;

	// 왼쪽 패딩: FieldOffset(56) + 오브젝트 헤더(~16바이트) = ~72바이트 > 캐시 라인(64)
	[FieldOffset(56)]
	private long _value;
	// 오른쪽 패딩: Size(192) - FieldOffset(56) - sizeof(long)(8) = 128바이트 > 캐시 라인(64)

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

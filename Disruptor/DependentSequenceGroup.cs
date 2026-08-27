using System.Runtime.CompilerServices;

namespace Disruptor;

/// <summary>
/// 대기 전략에 넘길 "의존 대상".
/// 의존 핸들러가 없으면 커서를, 있으면 의존 시퀀스들의 최솟값을 노출합니다.
/// </summary>
/// <remarks>
/// 이 타입이 없으면 대기 전략은 항상 프로듀서 커서만 보게 되고,
/// 하위 컨슈머가 상위 컨슈머를 기다릴 때 블로킹 대신 hot-spin을 하게 됩니다
/// (BlockingWaitStrategy를 써도 코어 하나를 상시 점유하는 현상).
/// 대기 전략이 이 값 하나만 보고 블로킹/스핀을 결정하면 그 문제가 사라집니다.
/// </remarks>
public sealed class DependentSequenceGroup
{
    private readonly Sequence _cursor;
    private readonly Sequence[] _dependencies; // 길이 0 이면 커서만 추적
    private readonly Sequence? _single;        // 길이 1 최적화 (루프/분기 제거)

    public DependentSequenceGroup(Sequence cursor, params Sequence[] dependencies)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(dependencies);

        _cursor = cursor;
        _dependencies = dependencies;
        _single = dependencies.Length == 1 ? dependencies[0] : null;
    }

    /// <summary>의존 대상이 현재 어디까지 진행했는가.</summary>
    public long Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (_dependencies.Length == 0)
                return _cursor.Value;

            if (_single is not null)
                return _single.Value;

            long min = long.MaxValue;
            var deps = _dependencies;
            for (int i = 0; i < deps.Length; i++)
            {
                long v = deps[i].Value;
                if (v < min) min = v;
            }
            return min;
        }
    }

    /// <summary>프로듀서 커서(게시 상한).</summary>
    public long CursorValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _cursor.Value;
    }

    /// <summary>의존 핸들러 없이 커서만 추적하는가.</summary>
    public bool DependsOnCursor => _dependencies.Length == 0;

    /// <summary>의존 핸들러 수.</summary>
    public int DependencyCount => _dependencies.Length;
}

namespace Disruptor;

/// <summary>
/// 연속된 이벤트 구간을 <b>통째로</b> 받는 핸들러.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IEventHandler{T}"/>는 이벤트마다 호출 경계를 통과하지만, 이 인터페이스는
/// 배치당(정확히는 조각당) 1회만 통과합니다. 저널 직렬화, 브로드캐스트 인코딩, 통계 집계처럼
/// <b>배치 전체를 한 번에 다루는 것이 자연스러운 작업</b>에서 이득이 큽니다 —
/// 내부에서 SIMD·일괄 직렬화·버퍼 재사용이 가능해집니다.
/// </para>
/// <para>
/// <b>랩 경계</b>: 배치가 링 버퍼의 끝을 넘으면 <see cref="OnBatch"/>가 두 번 호출됩니다.
/// 호출당 span 은 항상 연속 메모리이며, 마지막 조각에만 <c>endOfBatch = true</c> 입니다.
/// </para>
/// <para>
/// <b>span 유출 금지</b>: <c>events</c> 가 가리키는 슬롯은 호출이 끝나면 재사용됩니다.
/// 필드나 컬렉션에 보관하려면 반드시 복사하세요.
/// </para>
/// <para>
/// <b>예외 정책</b>: <see cref="OnBatch"/> 안에서 예외가 나면 실패한 이벤트를 특정할 수 없으므로
/// 예외 정책이 <b>조각 단위</b>로 적용됩니다(<see cref="EventExceptionAction.Skip"/> = 조각 전체 스킵).
/// 이벤트 단위 정책이 필요하면 <see cref="IEventHandler{T}"/>를 사용하세요.
/// </para>
/// <para>
/// Retry repeats only the failing contiguous segment, so handlers must be idempotent within that
/// segment. Previously completed segments are not replayed. With automatic clearing enabled,
/// completed and skipped segments are cleared; Retry and Halt retain the failing segment.
/// A failure in <see cref="IClearable.Clear"/> halts the processor after reporting the exception,
/// regardless of the recovery action, to avoid repeating side effects or returning a buffer twice.
/// </para>
/// </remarks>
public interface IBatchEventHandler<T> where T : class
{
    /// <summary>
    /// 연속된 이벤트 조각을 처리합니다.
    /// </summary>
    /// <param name="events">이벤트 span. 호출 범위 밖으로 유출하면 안 됩니다.</param>
    /// <param name="startSequence"><paramref name="events"/>[0] 의 시퀀스 번호</param>
    /// <param name="endOfBatch">이 조각이 현재 배치의 마지막인가</param>
    void OnBatch(ReadOnlySpan<T> events, long startSequence, bool endOfBatch);

    /// <summary>대기 타임아웃 시 호출됩니다. 틱 진행 / 송신 플러시 / 하트비트 지점입니다.</summary>
    void OnTimeout(long lastProcessedSequence) { }

    void OnStart() { }

    void OnShutdown() { }
}

/// <summary>
/// 값 타입 이벤트 구간을 통째로 받는 핸들러. span 이 쓰기 가능하므로 in-place 변환이 가능합니다.
/// </summary>
/// <remarks>
/// 구조체 배열의 연속 조각을 그대로 받으므로 <c>MemoryMarshal.Cast</c> + <c>Vector&lt;T&gt;</c> /
/// <c>TensorPrimitives</c> 로 바로 처리할 수 있습니다. 값 타입 링 버퍼의 진짜 이득이 나오는 지점입니다.
/// Recovery is scoped to a contiguous segment, as with <see cref="IBatchEventHandler{T}"/>.
/// When automatic clearing is enabled, completed and skipped segments are reset to default;
/// the failing segment is retained for Retry or Halt.
/// </remarks>
public interface IValueBatchEventHandler<T> where T : struct
{
    /// <summary>연속된 이벤트 조각을 처리합니다.</summary>
    void OnBatch(Span<T> events, long startSequence, bool endOfBatch);

    void OnTimeout(long lastProcessedSequence) { }

    void OnStart() { }

    void OnShutdown() { }
}

namespace Disruptor.Work;

/// <summary>
/// 여러 워커가 이벤트를 <b>나눠서</b> 처리하는 핸들러.
/// </summary>
/// <remarks>
/// Disruptor의 기본 모델은 멀티캐스트(모든 컨슈머가 모든 이벤트를 봄)라서
/// "N명 중 아무나 하나가 처리"를 표현할 수 없습니다. 이 인터페이스와
/// <see cref="WorkerPool{T}"/>가 그 빈틈을 채웁니다.
/// <para>
/// 게임 서버에서는 <b>순서 무관 + 느린 작업</b>에 사용하세요:
/// DB 쓰기, 외부 API 호출, 리플레이 인코딩 등.
/// </para>
/// <b>주의</b>: 이벤트가 워커들에게 분배되므로 <b>처리 순서가 보장되지 않습니다.</b>
/// 순서가 필요하면 일반 <see cref="IEventHandler{T}"/>를 사용하세요.
/// </remarks>
public interface IWorkHandler<in T>
{
    void OnEvent(T evt);

    void OnStart() { }

    void OnShutdown() { }

    /// <summary>대기 타임아웃 시 호출됩니다. 배치 커밋 지점으로 사용하세요.</summary>
    void OnTimeout() { }
}

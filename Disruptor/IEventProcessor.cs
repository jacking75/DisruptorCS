namespace Disruptor;

/// <summary>
/// 링 버퍼에서 이벤트를 읽어 처리하는 실행 단위. 하나의 스레드에서 실행됩니다.
/// </summary>
public interface IEventProcessor
{
    /// <summary>이 프로세서가 어디까지 처리했는지 나타내는 시퀀스.</summary>
    Sequence Sequence { get; }

    /// <summary>진단/메트릭에 사용할 이름.</summary>
    string Name { get; }

    /// <summary>실행 중인지 여부.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// 이벤트 루프를 실행합니다. Thread나 Task에서 호출하세요.
    /// </summary>
    /// <param name="cancellationToken">종료 신호</param>
    /// <param name="startedGate">이벤트 루프 진입 직전에 Signal됩니다. Start/Shutdown 경쟁 방지용입니다.</param>
    void Run(CancellationToken cancellationToken, CountdownEvent? startedGate = null);

    /// <summary>프로세서를 정지시킵니다.</summary>
    void Halt();
}

namespace Disruptor;

/// <summary>
/// 값 타입 이벤트를 처리하는 핸들러. 슬롯을 <c>ref</c>로 받으므로 복사가 발생하지 않습니다.
/// </summary>
public interface IValueEventHandler<T> where T : struct
{
    /// <summary>이벤트가 사용 가능해지면 호출됩니다.</summary>
    void OnEvent(ref T data, long sequence, bool endOfBatch);

    /// <summary>배치 처리 시작 시 1회 호출됩니다.</summary>
    void OnBatchStart(long batchSize) { }

    /// <summary>대기 타임아웃 시 호출됩니다. 틱 진행 / 송신 플러시 지점입니다.</summary>
    void OnTimeout(long lastProcessedSequence) { }

    void OnStart() { }

    void OnShutdown() { }
}

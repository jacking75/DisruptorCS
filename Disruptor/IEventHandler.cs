using System;
using System.Collections.Generic;
using System.Text;

namespace Disruptor;

/// <summary>
/// 이벤트를 처리하는 핸들러.
/// </summary>
public interface IEventHandler<in T>
{
    /// <summary>
    /// 이벤트가 사용 가능해지면 호출됩니다.
    /// </summary>
    /// <param name="data">이벤트 데이터</param>
    /// <param name="sequence">이벤트의 시퀀스 번호</param>
    /// <param name="endOfBatch">현재 배치의 마지막인지 여부</param>
    void OnEvent(T data, long sequence, bool endOfBatch);

    /// <summary>핸들러 시작 시 호출됩니다.</summary>
    void OnStart() { }

    /// <summary>핸들러 종료 시 호출됩니다.</summary>
    void OnShutdown() { }
}

/// <summary>
/// 예외 처리 핸들러.
/// </summary>
public interface IExceptionHandler<in T>
{
    void HandleEventException(Exception ex, long sequence, T @event);
    void HandleOnStartException(Exception ex);
    void HandleOnShutdownException(Exception ex);
}
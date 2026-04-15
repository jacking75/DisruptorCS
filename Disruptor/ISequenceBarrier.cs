using System;
using System.Collections.Generic;
using System.Text;

namespace Disruptor;

/// <summary>
/// 시퀀스 배리어 인터페이스.
/// 컨슈머가 프로듀서나 다른 컨슈머를 기다리는 데 사용됩니다.
/// </summary>
public interface ISequenceBarrier
{
    /// <summary>
    /// 주어진 시퀀스가 소비 가능해질 때까지 대기합니다.
    /// </summary>
    long WaitFor(long sequence);

    /// <summary>현재 커서 위치를 반환합니다.</summary>
    long Cursor { get; }

    /// <summary>알림 상태인지 확인합니다.</summary>
    bool IsAlerted { get; }

    /// <summary>알림 상태를 설정합니다.</summary>
    void Alert();

    /// <summary>알림 상태를 해제합니다.</summary>
    void ClearAlert();

    /// <summary>알림 상태이면 AlertException을 던집니다.</summary>
    void CheckAlert();
}
using System;
using System.Collections.Generic;
using System.Text;

namespace Disruptor;

/// <summary>
/// 예외 발생 시 콘솔에 출력하는 기본 예외 핸들러.
/// </summary>
public sealed class FatalExceptionHandler<T> : IExceptionHandler<T>
{
    public void HandleEventException(Exception ex, long sequence, T @event)
    {
        Console.Error.WriteLine(
            $"[FATAL] Exception processing sequence {sequence} for event {@event}: {ex}");
        throw new ApplicationException("Fatal exception in event handler", ex);
    }

    public void HandleOnStartException(Exception ex)
    {
        Console.Error.WriteLine($"[FATAL] Exception during OnStart: {ex}");
    }

    public void HandleOnShutdownException(Exception ex)
    {
        Console.Error.WriteLine($"[FATAL] Exception during OnShutdown: {ex}");
    }
}

/// <summary>
/// 예외를 무시하고 로깅만 하는 핸들러.
/// </summary>
public sealed class IgnoreExceptionHandler<T> : IExceptionHandler<T>
{
    public void HandleEventException(Exception ex, long sequence, T @event)
    {
        Console.Error.WriteLine(
            $"[WARN] Exception processing sequence {sequence}: {ex.Message}");
    }

    public void HandleOnStartException(Exception ex)
    {
        Console.Error.WriteLine($"[WARN] Exception during OnStart: {ex.Message}");
    }

    public void HandleOnShutdownException(Exception ex)
    {
        Console.Error.WriteLine($"[WARN] Exception during OnShutdown: {ex.Message}");
    }
}
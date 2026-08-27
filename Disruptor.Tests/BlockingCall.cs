namespace Disruptor.Tests;

/// <summary>
/// "이 호출이 블록되어야 한다"를 검증하기 위한 헬퍼.
/// </summary>
/// <remarks>
/// <see cref="Task"/> 대신 전용 스레드를 쓰는 이유:
/// (1) ThreadPool 스레드를 무기한 점유하면 다른 테스트가 굶고,
/// (2) 블로킹 대기는 Task 의미론과 맞지 않습니다.
/// </remarks>
public sealed class BlockingCall<T>
{
    private readonly ManualResetEventSlim _finished = new(false);
    private T _result = default!;
    private Exception? _exception;

    internal BlockingCall(Func<T> action)
    {
        var thread = new Thread(() =>
        {
            try
            {
                _result = action();
            }
            catch (Exception ex)
            {
                _exception = ex;
            }
            finally
            {
                _finished.Set();
            }
        })
        { IsBackground = true, Name = "BlockingCall" };

        thread.Start();
    }

    /// <summary>지정 시간 안에 호출이 완료되었는가.</summary>
    public bool CompletedWithin(TimeSpan timeout) => _finished.Wait(timeout);

    /// <summary>완료된 호출의 결과. 예외가 났다면 다시 던집니다.</summary>
    public T Result => _exception is not null
        ? throw new InvalidOperationException("블로킹 호출이 예외로 끝났습니다.", _exception)
        : _result;

    public Exception? Exception => _exception;
}

public static class BlockingCall
{
    public static BlockingCall<T> Start<T>(Func<T> action) => new(action);
}

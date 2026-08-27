using System.Runtime.InteropServices;

namespace Disruptor;

/// <summary>
/// 프로세서 스레드 생성 옵션.
/// </summary>
public sealed record ProcessorThreadOptions
{
    public static ProcessorThreadOptions Default { get; } = new();

    public ThreadPriority Priority { get; init; } = ThreadPriority.Normal;

    /// <summary>
    /// 스레드를 고정할 논리 코어 번호를 인덱스별로 반환합니다. null이면 고정하지 않습니다.
    /// </summary>
    /// <remarks>
    /// <b>코어 고정은 양날의 검입니다.</b> 고정한 코어에 다른 부하가 몰리면 오히려 느려집니다.
    /// 전용 베어메탈 게임 서버에서 프로파일링으로 이득을 확인한 뒤에만 사용하고,
    /// 컨테이너 환경에서는 비활성으로 두세요.
    /// </remarks>
    public Func<int, int?>? CoreAffinitySelector { get; init; }

    /// <summary>스레드 스택 크기. 기본 1MB는 핸들러 스레드에 과합니다.</summary>
    public int MaxStackSize { get; init; } = 256 * 1024;

    public bool IsBackground { get; init; } = true;

    /// <summary>스레드 이름 접두사.</summary>
    public string NamePrefix { get; init; } = "Disruptor";
}

/// <summary>
/// 프로세서 실행 스레드를 생성합니다.
/// </summary>
internal static class ProcessorThreadFactory
{
    public static Thread Create(
        IEventProcessor processor,
        CancellationToken cancellationToken,
        CountdownEvent startedGate,
        ProcessorThreadOptions? options,
        int index)
    {
        options ??= ProcessorThreadOptions.Default;

        int? core = options.CoreAffinitySelector?.Invoke(index);

        var thread = new Thread(() =>
        {
            if (core is int c)
                ThreadAffinity.PinToCore(c);

            processor.Run(cancellationToken, startedGate);
        }, options.MaxStackSize)
        {
            IsBackground = options.IsBackground,
            Priority = options.Priority,
            Name = $"{options.NamePrefix}-{processor.Name}-{index}",
        };

        return thread;
    }
}

/// <summary>
/// 스레드를 특정 논리 코어에 고정합니다. 지원하지 않는 플랫폼에서는 무시됩니다.
/// </summary>
public static partial class ThreadAffinity
{
    /// <summary>현재 스레드를 지정 논리 코어에 고정합니다.</summary>
    public static void PinToCore(int coreIndex)
    {
        if (coreIndex < 0 || coreIndex >= Environment.ProcessorCount)
            return;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)(1UL << coreIndex));
            }
            else if (OperatingSystem.IsLinux())
            {
                // cpu_set_t 는 1024비트 비트맵. 앞 8바이트만 사용해도 코어 0~63을 커버한다.
                Span<ulong> mask = stackalloc ulong[16];
                mask.Clear();
                mask[coreIndex / 64] = 1UL << (coreIndex % 64);
                SchedSetAffinity(0, (IntPtr)(mask.Length * sizeof(ulong)), ref MemoryMarshalHelper(mask));
            }
            // macOS 는 스레드 affinity API가 없으므로 no-op.
        }
        catch (EntryPointNotFoundException)
        {
            // 플랫폼이 지원하지 않으면 조용히 무시한다.
        }
        catch (DllNotFoundException)
        {
        }
    }

    private static ref ulong MemoryMarshalHelper(Span<ulong> span)
        => ref System.Runtime.InteropServices.MemoryMarshal.GetReference(span);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetCurrentThread();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial UIntPtr SetThreadAffinityMask(IntPtr hThread, UIntPtr dwThreadAffinityMask);

    [LibraryImport("libc", EntryPoint = "sched_setaffinity", SetLastError = true)]
    private static partial int SchedSetAffinity(int pid, IntPtr cpuSetSize, ref ulong mask);
}

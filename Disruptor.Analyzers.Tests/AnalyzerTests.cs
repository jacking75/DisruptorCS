using Xunit;

namespace Disruptor.Analyzers.Tests;

/// <summary>
/// DISR001: async 핸들러 검출.
/// </summary>
public class AsyncHandlerAnalyzerTests
{
    private const string Preamble = """
        using System;
        using System.Threading.Tasks;
        using Disruptor;
        using Disruptor.Work;

        public sealed class Evt { public long Value; }

        """;

    [Fact]
    public async Task ReportsAsyncEventHandler()
    {
        await AnalyzerVerifier.VerifyAsync(new AsyncHandlerAnalyzer(), Preamble + """
            public sealed class BadHandler : IEventHandler<Evt>
            {
                public async void OnEvent(Evt data, long sequence, bool endOfBatch)
                {
                    await Task.Delay(1);
                }
            }
            """, DiagnosticIds.AsyncHandler, expectedCount: 1);
    }

    [Fact]
    public async Task ReportsAsyncWorkHandler()
    {
        await AnalyzerVerifier.VerifyAsync(new AsyncHandlerAnalyzer(), Preamble + """
            public sealed class BadWorker : IWorkHandler<Evt>
            {
                public async void OnEvent(Evt evt)
                {
                    await Task.Yield();
                }
            }
            """, DiagnosticIds.AsyncHandler, expectedCount: 1);
    }

    /// <summary>
    /// <c>OnEvent</c> 만이 아니라 수명주기/타임아웃 훅도 async 면 안 됩니다.
    /// 특히 <c>OnTimeout</c> 이 async 면 틱 드라이버가 실제로 진행되기 전에 반환됩니다.
    /// </summary>
    [Fact]
    public async Task ReportsAsyncTimeoutHook()
    {
        await AnalyzerVerifier.VerifyAsync(new AsyncHandlerAnalyzer(), Preamble + """
            public sealed class BadTimeout : IEventHandler<Evt>
            {
                public void OnEvent(Evt data, long sequence, bool endOfBatch) { }

                public async void OnTimeout(long lastProcessedSequence)
                {
                    await Task.Yield();
                }
            }
            """, DiagnosticIds.AsyncHandler, expectedCount: 1);
    }

    /// <summary>
    /// <c>IValueEventHandler.OnEvent</c> 는 <c>ref</c> 매개변수라 C# 컴파일러가 이미 async 를 금지합니다
    /// (CS1988). 배치 핸들러의 span 도 ref struct 라 마찬가지입니다 — 분석기가 필요 없는 영역입니다.
    /// </summary>
    [Fact]
    public async Task ValueHandlerOnEvent_CannotBeAsync_ByLanguageRule()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AnalyzerVerifier.GetDiagnosticsAsync(new AsyncHandlerAnalyzer(), """
                using System;
                using System.Threading.Tasks;
                using Disruptor;

                public struct ValueEvt { public long Value; }

                public sealed class BadValueHandler : IValueEventHandler<ValueEvt>
                {
                    public async void OnEvent(ref ValueEvt data, long sequence, bool endOfBatch)
                    {
                        await Task.Yield();
                    }
                }
                """));

        Assert.Contains("CS1988", ex.Message);
    }

    [Fact]
    public async Task DoesNotReportSyncHandler()
    {
        await AnalyzerVerifier.VerifyNoDiagnosticsAsync(new AsyncHandlerAnalyzer(), Preamble + """
            public sealed class GoodHandler : IEventHandler<Evt>
            {
                public void OnEvent(Evt data, long sequence, bool endOfBatch)
                {
                    data.Value++;
                }
            }
            """, DiagnosticIds.AsyncHandler);
    }

    [Fact]
    public async Task DoesNotReportUnrelatedAsyncMethod()
    {
        await AnalyzerVerifier.VerifyNoDiagnosticsAsync(new AsyncHandlerAnalyzer(), Preamble + """
            public sealed class GoodHandler : IEventHandler<Evt>
            {
                public void OnEvent(Evt data, long sequence, bool endOfBatch) { }

                // 핸들러 인터페이스 멤버가 아닌 async 메서드는 문제가 없다.
                public async Task LoadAsync() => await Task.Yield();
            }
            """, DiagnosticIds.AsyncHandler);
    }

    [Fact]
    public async Task DoesNotReportAsyncMethodOnNonHandlerClass()
    {
        await AnalyzerVerifier.VerifyNoDiagnosticsAsync(new AsyncHandlerAnalyzer(), Preamble + """
            public sealed class NotAHandler
            {
                public async void OnEvent(Evt data, long sequence, bool endOfBatch)
                {
                    await Task.Yield();
                }
            }
            """, DiagnosticIds.AsyncHandler);
    }
}

/// <summary>
/// DISR003: 캡처 람다 검출.
/// </summary>
public class CapturingLambdaAnalyzerTests
{
    private const string Preamble = """
        using System;
        using Disruptor;
        using Disruptor.WaitStrategy;

        public sealed class Evt { public long Value; }

        public static class Sample
        {
            private static RingBuffer<Evt> Ring()
                => RingBuffer<Evt>.CreateSingleProducer(() => new Evt(), 1024, new YieldingWaitStrategy());

        """;

    private const string Postamble = """
        }
        """;

    [Fact]
    public async Task ReportsCapturingLambda()
    {
        await AnalyzerVerifier.VerifyAsync(new CapturingLambdaAnalyzer(), Preamble + """
            public static void Bad()
            {
                var ring = Ring();
                for (int i = 0; i < 10; i++)
                {
                    // i 를 캡처한다 -> 게시마다 클로저 할당
                    ring.PublishEvent((evt, seq) => evt.Value = i);
                }
            }
            """ + Postamble, DiagnosticIds.CapturingLambdaInPublish, expectedCount: 1);
    }

    [Fact]
    public async Task DoesNotReportStaticLambda()
    {
        await AnalyzerVerifier.VerifyNoDiagnosticsAsync(new CapturingLambdaAnalyzer(), Preamble + """
            public static void Good()
            {
                var ring = Ring();
                for (int i = 0; i < 10; i++)
                {
                    ring.PublishEvent(static (evt, seq, id) => evt.Value = id, (long)i);
                }
            }
            """ + Postamble, DiagnosticIds.CapturingLambdaInPublish);
    }

    [Fact]
    public async Task DoesNotReportNonCapturingLambda()
    {
        await AnalyzerVerifier.VerifyNoDiagnosticsAsync(new CapturingLambdaAnalyzer(), Preamble + """
            public static void Good()
            {
                var ring = Ring();
                // 바깥 변수를 참조하지 않으므로 캐시된 델리게이트가 재사용된다.
                ring.PublishEvent((evt, seq) => evt.Value = seq);
            }
            """ + Postamble, DiagnosticIds.CapturingLambdaInPublish);
    }
}

/// <summary>
/// DISR004: 2의 거듭제곱이 아닌 링 크기 검출.
/// </summary>
public class RingSizeAnalyzerTests
{
    private const string Preamble = """
        using System;
        using Disruptor;
        using Disruptor.WaitStrategy;

        public sealed class Evt { public long Value; }

        public static class Sample
        {
        """;

    private const string Postamble = """
        }
        """;

    [Fact]
    public async Task ReportsNonPowerOfTwo()
    {
        await AnalyzerVerifier.VerifyAsync(new RingSizeAnalyzer(), Preamble + """
            public static void Bad()
            {
                // 1000 은 2의 거듭제곱이 아니다 -> 런타임 예외가 되기 전에 잡아야 한다
                var ring = RingBuffer<Evt>.CreateSingleProducer(
                    () => new Evt(), 1000, new YieldingWaitStrategy());
            }
            """ + Postamble, DiagnosticIds.RingSizeNotPowerOfTwo, expectedCount: 1);
    }

    [Fact]
    public async Task DoesNotReportPowerOfTwo()
    {
        await AnalyzerVerifier.VerifyNoDiagnosticsAsync(new RingSizeAnalyzer(), Preamble + """
            public static void Good()
            {
                var ring = RingBuffer<Evt>.CreateSingleProducer(
                    () => new Evt(), 1024, new YieldingWaitStrategy());
            }
            """ + Postamble, DiagnosticIds.RingSizeNotPowerOfTwo);
    }

    [Fact]
    public async Task DoesNotReportNonConstantSize()
    {
        await AnalyzerVerifier.VerifyNoDiagnosticsAsync(new RingSizeAnalyzer(), Preamble + """
            public static void Runtime(int size)
            {
                // 상수가 아니면 컴파일 타임에 판단할 수 없다 (런타임 검사가 담당).
                var ring = RingBuffer<Evt>.CreateSingleProducer(
                    () => new Evt(), size, new YieldingWaitStrategy());
            }
            """ + Postamble, DiagnosticIds.RingSizeNotPowerOfTwo);
    }

    [Fact]
    public async Task ReportsNonPowerOfTwoInDslConstructor()
    {
        await AnalyzerVerifier.VerifyAsync(new RingSizeAnalyzer(), """
            using System;
            using Disruptor;
            using Disruptor.Dsl;

            public sealed class Evt { public long Value; }

            public static class Sample
            {
                public static void Bad()
                {
                    var d = new Disruptor<Evt>(() => new Evt(), 100);
                }
            }
            """, DiagnosticIds.RingSizeNotPowerOfTwo, expectedCount: 1);
    }
}

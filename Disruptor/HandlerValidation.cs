using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Disruptor;

/// <summary>
/// 핸들러 구현이 Disruptor의 실행 모델을 위반하지 않는지 개발 빌드에서 검사합니다.
/// </summary>
internal static class HandlerValidation
{
    /// <summary>
    /// 핸들러의 지정 메서드가 <c>async</c>로 선언되었는지 검사합니다.
    /// Release 빌드에서는 호출 자체가 제거됩니다.
    /// </summary>
    /// <remarks>
    /// <c>async void OnEvent</c>는 첫 <c>await</c>에서 즉시 반환되므로, 프레임워크는 이벤트가
    /// 처리되었다고 보고 시퀀스를 전진시킵니다. 그 결과 <b>순서 보장이 깨지고 슬롯이
    /// 처리 도중에 재사용</b>됩니다. 조용히 데이터가 깨지는 유형이라 반드시 조기에 잡아야 합니다.
    /// </remarks>
    [Conditional("DEBUG")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "DEBUG 빌드 전용 진단이며, Release/AOT 빌드에서는 [Conditional] 로 호출 자체가 제거됩니다.")]
    public static void AssertNotAsync(object handler, string methodName)
    {
        Type type = handler.GetType();

        foreach (MethodInfo method in type.GetMethods(
                     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            bool nameMatches = method.Name == methodName
                               || method.Name.EndsWith("." + methodName, StringComparison.Ordinal);

            if (!nameMatches)
                continue;

            if (method.GetCustomAttribute<AsyncStateMachineAttribute>() is not null)
            {
                throw new InvalidOperationException(
                    $"'{type.Name}.{method.Name}' 이(가) async 로 선언되었습니다. " +
                    "Disruptor 핸들러는 동기 메서드여야 합니다 — async 핸들러는 첫 await 에서 반환되어 " +
                    "이벤트 순서 보장이 깨지고 슬롯이 처리 도중 재사용됩니다. " +
                    "느린 I/O는 WorkerPool(IWorkHandler)로 분리하세요.");
            }
        }
    }
}

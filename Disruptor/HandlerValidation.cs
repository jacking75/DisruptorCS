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

    /// <summary>
    /// 핸들러가 인터페이스 기본 구현(DIM)을 그대로 쓰는지, 직접 구현했는지 판별합니다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>OnBatchStart</c> 같은 선택적 훅은 대부분의 핸들러가 구현하지 않습니다. 그런데도 매 배치마다
    /// 호출하면 <b>DIM 디스패치</b> 비용을 지불하게 됩니다 — 일반 인터페이스 호출보다 비싸고,
    /// 링이 작아 배치가 작을수록 이 고정 비용이 처리량을 지배합니다.
    /// </para>
    /// <para>
    /// 생성 시점에 한 번만 판별해 두고, 구현하지 않은 훅은 아예 호출하지 않습니다.
    /// 판별에 실패하면 보수적으로 "구현했다"고 보아 동작은 항상 안전합니다.
    /// </para>
    /// </remarks>
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "생성 시점 1회 검사이며, 실패 시 훅을 호출하는 보수적 기본값으로 동작합니다.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "생성 시점 1회 검사이며, 실패 시 훅을 호출하는 보수적 기본값으로 동작합니다.")]
    public static bool ImplementsHook(
        object handler,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces |
                                    DynamicallyAccessedMemberTypes.PublicMethods |
                                    DynamicallyAccessedMemberTypes.NonPublicMethods)]
        Type interfaceType,
        string methodName)
    {
        try
        {
            InterfaceMapping map = handler.GetType().GetInterfaceMap(interfaceType);

            for (int i = 0; i < map.InterfaceMethods.Length; i++)
            {
                if (map.InterfaceMethods[i].Name != methodName)
                    continue;

                // DIM 을 그대로 쓰면 대상 메서드의 선언 타입이 인터페이스 자신이다.
                return map.TargetMethods[i].DeclaringType != interfaceType;
            }
        }
        catch (ArgumentException)
        {
            // 매핑을 구할 수 없는 경우(제네릭 특수 상황 등)에는 보수적으로 호출한다.
        }

        return true;
    }
}

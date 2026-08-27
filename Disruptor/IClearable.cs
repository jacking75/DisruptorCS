namespace Disruptor;

/// <summary>
/// 슬롯이 가진 외부 참조를 해제할 수 있는 이벤트.
/// </summary>
/// <remarks>
/// 링 버퍼의 슬롯은 재사용되기 전까지 이전 이벤트의 참조 필드(byte[], string, 세션 핸들 등)를
/// 계속 붙들고 있습니다. 그 결과 객체 수명이 <b>최대 링 버퍼 한 바퀴만큼 연장</b>되며,
/// 1M 슬롯 × 4KB 패킷 버퍼면 4GB가 회수되지 않습니다.
/// <para>
/// 이 인터페이스를 구현하면 파이프라인의 마지막 핸들러가 처리한 직후
/// 프레임워크가 <see cref="Clear"/>를 호출해 참조를 끊습니다.
/// </para>
/// </remarks>
public interface IClearable
{
    /// <summary>슬롯이 가진 외부 참조를 해제합니다. 풀 버퍼 반납도 여기서 수행하세요.</summary>
    void Clear();
}

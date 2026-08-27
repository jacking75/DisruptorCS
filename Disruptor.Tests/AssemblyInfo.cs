using Xunit;

// 이 스위트에는 타이밍/CPU 예산에 의존하는 테스트가 있습니다.
// xUnit이 컬렉션을 병렬 실행하면 Process.TotalProcessorTime 이 다른 테스트의 CPU까지 포함하고,
// 스핀 전략 테스트끼리 코어를 다투어 결과가 흔들립니다.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

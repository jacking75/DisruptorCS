using System;
using System.Collections.Generic;
using System.Text;

namespace Disruptor;

/// <summary>
/// 이벤트 프로세서를 중지시키기 위한 예외.
/// </summary>
public sealed class AlertException : Exception
{
    public static readonly AlertException Instance = new();

    private AlertException() : base("Alert!") { }
}
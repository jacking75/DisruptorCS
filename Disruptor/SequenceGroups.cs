using System;
using System.Collections.Generic;
using System.Text;

namespace Disruptor;

/// <summary>
/// 시퀀스 배열 관리 유틸리티. 원자적으로 시퀀스를 추가/제거합니다.
/// </summary>
internal static class SequenceGroups
{
    public static long GetMinimumSequence(Sequence[] sequences, long minimum)
    {
        for (int i = 0; i < sequences.Length; i++)
        {
            long value = sequences[i].Value;
            if (value < minimum)
                minimum = value;
        }
        return minimum;
    }

    public static void AddSequences(ref Sequence[] sequences, Sequence[] toAdd)
    {
        Sequence[] oldSequences;
        Sequence[] newSequences;

        do
        {
            oldSequences = Volatile.Read(ref sequences);
            newSequences = new Sequence[oldSequences.Length + toAdd.Length];
            Array.Copy(oldSequences, newSequences, oldSequences.Length);
            Array.Copy(toAdd, 0, newSequences, oldSequences.Length, toAdd.Length);
        }
        while (Interlocked.CompareExchange(ref sequences, newSequences, oldSequences)
               != oldSequences);
    }

    public static bool RemoveSequence(ref Sequence[] sequences, Sequence toRemove)
    {
        Sequence[] oldSequences;
        Sequence[] newSequences;

        do
        {
            oldSequences = Volatile.Read(ref sequences);
            int index = Array.IndexOf(oldSequences, toRemove);
            if (index < 0)
                return false;

            newSequences = new Sequence[oldSequences.Length - 1];
            Array.Copy(oldSequences, 0, newSequences, 0, index);
            Array.Copy(oldSequences, index + 1, newSequences, index,
                       oldSequences.Length - index - 1);
        }
        while (Interlocked.CompareExchange(ref sequences, newSequences, oldSequences)
               != oldSequences);

        return true;
    }
}
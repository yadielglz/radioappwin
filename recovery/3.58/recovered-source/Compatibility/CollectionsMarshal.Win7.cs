#if NET6_0_WINDOWS
global using CollectionsMarshal = RadioApp.Compatibility.CollectionsMarshalCompat;
#endif

using System;
using System.Collections.Generic;

namespace RadioApp.Compatibility;

#if NET6_0_WINDOWS
internal static class CollectionsMarshalCompat
{
    public static Span<T> AsSpan<T>(List<T> list)
    {
        return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list);
    }

    public static void SetCount<T>(List<T> list, int count)
    {
        if (list == null)
        {
            throw new ArgumentNullException(nameof(list));
        }

        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        while (list.Count < count)
        {
            list.Add(default!);
        }

        if (list.Count > count)
        {
            list.RemoveRange(count, list.Count - count);
        }
    }
}
#endif

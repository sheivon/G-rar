using System.Globalization;
using System.Text;

namespace GRAR.Helpers;

/// <summary>Windows Explorer style "natural" ordering: numbers inside names sort numerically.</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int ix = 0, iy = 0;
        while (ix < x.Length && iy < y.Length)
        {
            char cx = x[ix], cy = y[iy];

            if (char.IsDigit(cx) && char.IsDigit(cy))
            {
                int sx = ix, sy = iy;
                while (sx < x.Length && char.IsDigit(x[sx])) sx++;
                while (sy < y.Length && char.IsDigit(y[sy])) sy++;

                // Compare the numeric runs, ignoring leading zeros.
                var nx = x.AsSpan(ix, sx - ix).TrimStart('0');
                var ny = y.AsSpan(iy, sy - iy).TrimStart('0');

                int result = nx.Length != ny.Length
                    ? nx.Length.CompareTo(ny.Length)
                    : nx.SequenceCompareTo(ny);

                if (result != 0)
                    return result;

                // Equal numerically: the shorter (more zero padded) string sorts first.
                int lenResult = (sx - ix).CompareTo(sy - iy);
                if (lenResult != 0)
                    return lenResult;

                ix = sx;
                iy = sy;
                continue;
            }

            int cmp = char.ToUpperInvariant(cx).CompareTo(char.ToUpperInvariant(cy));
            if (cmp != 0)
                return cmp;

            ix++;
            iy++;
        }

        return (x.Length - ix).CompareTo(y.Length - iy);
    }
}

/// <summary>Compares byte counts and dates, treating "missing" values as smallest.</summary>
public sealed class NullableNumberComparer : IComparer<long>
{
    public static readonly NullableNumberComparer Instance = new();

    public int Compare(long x, long y) => x.CompareTo(y);
}

public static class Text
{
    /// <summary>Case insensitive, culture independent comparison used for names.</summary>
    public static int CompareName(string? x, string? y)
        => string.Compare(x, y, CultureInfo.InvariantCulture, CompareOptions.IgnoreCase);
}

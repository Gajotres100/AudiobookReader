namespace AudioBookReader.Core.Audio;

/// <summary>
/// Orders strings the way a person reading filenames would, so "Chapter 9" sorts before
/// "Chapter 10". Ordinal comparison puts them the other way round, which would silently shuffle
/// a folder-of-files audiobook into the wrong order.
/// </summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    private NaturalStringComparer() { }

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var xEnd = i;
                while (xEnd < x.Length && char.IsDigit(x[xEnd])) xEnd++;
                var yEnd = j;
                while (yEnd < y.Length && char.IsDigit(y[yEnd])) yEnd++;

                var xDigits = x.AsSpan(i, xEnd - i).TrimStart('0');
                var yDigits = y.AsSpan(j, yEnd - j).TrimStart('0');

                // Longer run of significant digits is the larger number; same length compares lexically.
                if (xDigits.Length != yDigits.Length)
                    return xDigits.Length - yDigits.Length;

                var digitCompare = xDigits.SequenceCompareTo(yDigits);
                if (digitCompare != 0) return digitCompare;

                i = xEnd;
                j = yEnd;
                continue;
            }

            var charCompare = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
            if (charCompare != 0) return charCompare;

            i++;
            j++;
        }

        return (x.Length - i) - (y.Length - j);
    }
}

using AudioBookReader.Core.Audio;

namespace AudioBookReader.Core.Tests;

public class NaturalStringComparerTests
{
    private static readonly NaturalStringComparer Comparer = NaturalStringComparer.Instance;

    [Fact]
    public void OrdersMultiDigitNumbersByValueNotText()
    {
        string[] files = ["Chapter 10.mp3", "Chapter 2.mp3", "Chapter 1.mp3"];

        Array.Sort(files, Comparer);

        Assert.Equal(["Chapter 1.mp3", "Chapter 2.mp3", "Chapter 10.mp3"], files);
    }

    [Fact]
    public void TreatsLeadingZerosAsInsignificant()
    {
        Assert.True(Comparer.Compare("track007", "track8") < 0);
        Assert.Equal(0, Comparer.Compare("track07", "track7"));
    }

    [Fact]
    public void ComparesTextCaseInsensitively() =>
        Assert.Equal(0, Comparer.Compare("Part 3", "part 3"));

    [Fact]
    public void OrdersByTextWhereNumbersAreEqual() =>
        Assert.True(Comparer.Compare("01 - Intro", "01 - Prologue") < 0);

    [Fact]
    public void PutsAPrefixBeforeTheLongerStringItStarts() =>
        Assert.True(Comparer.Compare("Part 1", "Part 1 (reprise)") < 0);

    [Fact]
    public void HandlesNulls()
    {
        Assert.Equal(0, Comparer.Compare(null, null));
        Assert.True(Comparer.Compare(null, "a") < 0);
        Assert.True(Comparer.Compare("a", null) > 0);
    }
}

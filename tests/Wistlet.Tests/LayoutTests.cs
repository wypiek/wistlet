using Wistlet.Services;

namespace Wistlet.Tests;

public class LayoutTests
{
    private static int[] Rows(params int?[] chosen) => PostService.LayoutRows(chosen);

    [Fact]
    public void AutoLayoutMatchesTheOldOne()
    {
        Assert.Equal([0], Rows([null]));
        Assert.Equal([0, 0], Rows(null, null));
        Assert.Equal([0, 1, 1], Rows(null, null, null));
        Assert.Equal([0, 0, 1, 1, 2], Rows(null, null, null, null, null));
    }

    [Fact]
    public void ChosenRowsAreKept() => Assert.Equal([0, 0, 0, 1], Rows(1, 1, 1, 2));

    [Fact]
    public void GapsAreClosed() => Assert.Equal([0, 1], Rows(2, 5));

    [Fact]
    public void AutoImagesFollowInPairsAfterChosenRows() => Assert.Equal([0, 1, 1, 2], Rows(1, null, null, null));

    [Fact]
    public void ImagesCanBeReordered() => Assert.Equal([1, 0], Rows(2, 1));
}

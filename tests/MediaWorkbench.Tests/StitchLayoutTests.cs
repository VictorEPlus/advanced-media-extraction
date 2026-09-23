using MediaWorkbench.Core;

namespace MediaWorkbench.Tests;

public class StitchLayoutTests
{
    private static readonly StitchItem Wide = new(400, 200);
    private static readonly StitchItem Tall = new(200, 400);
    private static readonly StitchItem Square = new(300, 300);

    [Fact]
    public void NoPicturesMeansNothingToDraw()
    {
        Assert.True(StitchLayout.Compute([], StitchDirection.Row, 10, 3, true).IsEmpty);
        Assert.True(StitchLayout.Compute([new StitchItem(0, 100)], StitchDirection.Row, 10, 3, true).IsEmpty);
    }

    [Fact]
    public void ARowMatchesHeightsToTheShortestPictureAndNeverEnlarges()
    {
        var plan = StitchLayout.Compute([Wide, Tall], StitchDirection.Row, 0, 3, matchSizes: true);
        // The shortest is 200 tall, so the tall one is scaled down to it and keeps its shape: 200 x 400 becomes 100 x 200.
        Assert.Equal(200, plan.Height);
        Assert.Equal([new StitchPlacement(0, 0, 0, 400, 200), new StitchPlacement(1, 400, 0, 100, 200)], plan.Placements);
        Assert.Equal(500, plan.Width);
    }

    [Fact]
    public void AGapGoesBetweenPicturesAndNotAroundThem()
    {
        var plan = StitchLayout.Compute([Wide, Wide, Wide], StitchDirection.Row, 20, 3, matchSizes: true);
        Assert.Equal(400 * 3 + 20 * 2, plan.Width);
        Assert.Equal(0, plan.Placements[0].X);
        Assert.Equal(420, plan.Placements[1].X);
        Assert.Equal(840, plan.Placements[2].X);
    }

    [Fact]
    public void WithoutMatchingSizesEveryPictureKeepsItsPixelsAndIsCentred()
    {
        var plan = StitchLayout.Compute([Wide, Square], StitchDirection.Row, 0, 3, matchSizes: false);
        Assert.Equal(700, plan.Width);
        Assert.Equal(300, plan.Height);
        Assert.Equal(new StitchPlacement(0, 0, 50, 400, 200), plan.Placements[0]);
        Assert.Equal(new StitchPlacement(1, 400, 0, 300, 300), plan.Placements[1]);
    }

    [Fact]
    public void AColumnMatchesWidthsToTheNarrowestPicture()
    {
        var plan = StitchLayout.Compute([Wide, Tall], StitchDirection.Column, 0, 3, matchSizes: true);
        Assert.Equal(200, plan.Width);
        Assert.Equal(new StitchPlacement(0, 0, 0, 200, 100), plan.Placements[0]);
        Assert.Equal(new StitchPlacement(1, 0, 100, 200, 400), plan.Placements[1]);
        Assert.Equal(500, plan.Height);
    }

    [Fact]
    public void AGridFillsRowsLeftToRightAndUsesOneCellSizeThroughout()
    {
        var plan = StitchLayout.Compute([Square, Square, Square, Square, Square], StitchDirection.Grid, 10, 2, matchSizes: false);
        Assert.Equal(300 * 2 + 10, plan.Width);
        Assert.Equal(300 * 3 + 20, plan.Height);
        Assert.Equal(new StitchPlacement(0, 0, 0, 300, 300), plan.Placements[0]);
        Assert.Equal(new StitchPlacement(1, 310, 0, 300, 300), plan.Placements[1]);
        Assert.Equal(new StitchPlacement(2, 0, 310, 300, 300), plan.Placements[2]);
        Assert.Equal(new StitchPlacement(4, 0, 620, 300, 300), plan.Placements[4]);
    }

    [Fact]
    public void AGridFitsEveryPictureInsideTheSmallestBoxWhenSizesAreMatched()
    {
        var plan = StitchLayout.Compute([Wide, Tall], StitchDirection.Grid, 0, 2, matchSizes: true);
        // The box is the narrowest width by the shortest height, 200 x 200; each picture fits inside it, keeping its shape.
        Assert.Equal(400, plan.Width);
        Assert.Equal(200, plan.Height);
        Assert.Equal(new StitchPlacement(0, 0, 50, 200, 100), plan.Placements[0]);
        Assert.Equal(new StitchPlacement(1, 250, 0, 100, 200), plan.Placements[1]);
    }

    [Fact]
    public void AnEnormousCombinationIsScaledDownToSomethingWritable()
    {
        var huge = new StitchItem(12000, 9000);
        var plan = StitchLayout.Compute([huge, huge, huge], StitchDirection.Row, 0, 3, matchSizes: true);
        Assert.Equal(StitchLayout.MaximumSide, Math.Max(plan.Width, plan.Height));
        Assert.All(plan.Placements, placement => Assert.True(placement.Width > 0 && placement.Height > 0));
        Assert.Equal(3, plan.Placements.Count);
    }

    [Fact]
    public void SillyGapsAndColumnCountsAreBroughtBackIntoRange()
    {
        var plan = StitchLayout.Compute([Square, Square], StitchDirection.Grid, -50, 99, matchSizes: true);
        Assert.Equal(600, plan.Width);
        Assert.Equal(300, plan.Height);
    }
}

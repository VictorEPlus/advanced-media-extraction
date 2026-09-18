using MediaWorkbench.Core;

namespace MediaWorkbench.Tests;

public sealed class VideoTransformTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    [InlineData(-90)]
    [InlineData(450)]
    public void TurningACropAndTurningItBackIsLossless(int rotation)
    {
        var crop = new PixelCrop(30, 10, 100, 40);
        var turned = VideoTransform.ToRotated(crop, 200, 120, rotation);
        Assert.Equal(crop, VideoTransform.FromRotated(turned, 200, 120, rotation));
        var (width, height) = VideoTransform.NormalizeRotation(rotation) is 90 or 270 ? (120, 200) : (200, 120);
        Assert.True(turned.X >= 0 && turned.Y >= 0 && turned.X + turned.Width <= width && turned.Y + turned.Height <= height);
    }

    [Fact]
    public void AQuarterTurnClockwisePutsTheSourceBottomOnTheLeft()
    {
        // Source 200 x 120; a crop hugging the bottom-left corner.
        var turned = VideoTransform.ToRotated(new PixelCrop(0, 100, 50, 20), 200, 120, 90);
        // After turning clockwise the source's bottom edge is the left edge and its left edge is the top edge.
        Assert.Equal(new PixelCrop(0, 0, 20, 50), turned);
        Assert.Equal(new PixelCrop(150, 0, 50, 20), VideoTransform.ToRotated(new PixelCrop(0, 100, 50, 20), 200, 120, 180));
        Assert.Equal(new PixelCrop(100, 150, 20, 50), VideoTransform.ToRotated(new PixelCrop(0, 100, 50, 20), 200, 120, 270));
    }

    [Fact]
    public void EdgesStopAtTheFrameAndShortOfTheOppositeEdge()
    {
        var crop = new PixelCrop(10, 10, 100, 60);
        Assert.Equal(new PixelCrop(0, 10, 110, 60), VideoTransform.MoveEdge(crop, CropEdge.Left, -500, 200, 120));
        Assert.Equal(new PixelCrop(94, 10, 16, 60), VideoTransform.MoveEdge(crop, CropEdge.Left, 500, 200, 120));
        Assert.Equal(new PixelCrop(10, 10, 190, 60), VideoTransform.MoveEdge(crop, CropEdge.Right, 500, 200, 120));
        Assert.Equal(new PixelCrop(10, 11, 100, 59), VideoTransform.MoveEdge(crop, CropEdge.Top, 1, 200, 120));
        Assert.Equal(new PixelCrop(10, 10, 100, 16), VideoTransform.MoveEdge(crop, CropEdge.Bottom, -500, 200, 120));
        Assert.Equal(crop, VideoTransform.MoveEdge(crop, CropEdge.None, 5, 200, 120));
    }

    [Fact]
    public void FiltersCropFirstThenTurnAndKeepEvenSizes()
    {
        Assert.Equal("", new VideoTransform(null, 0).Filter(200, 120));
        Assert.Equal("", new VideoTransform(new PixelCrop(0, 0, 200, 120), 360).Filter(200, 120));
        Assert.True(new VideoTransform(null, 0).IsIdentity(200, 120));
        Assert.Equal("transpose=1", new VideoTransform(null, 90).Filter(200, 120));
        Assert.Equal("transpose=2", new VideoTransform(null, -90).Filter(200, 120));
        Assert.Equal("hflip,vflip", new VideoTransform(null, 180).Filter(200, 120));
        var transform = new VideoTransform(new PixelCrop(11, 7, 101, 51), 90);
        Assert.Equal("crop=100:50:11:7,transpose=1", transform.Filter(200, 120));
        Assert.Equal((50, 100), transform.OutputSize(200, 120));
        Assert.False(transform.IsIdentity(200, 120));
        // A crop that reaches outside the frame is pulled back inside rather than failing the export.
        Assert.Equal(new PixelCrop(150, 100, 50, 20), new VideoTransform(new PixelCrop(150, 100, 500, 500), 0).EffectiveCrop(200, 120));
    }
}

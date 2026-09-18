using System.Globalization;

namespace MediaWorkbench.Core;

/// <summary>An edge of a rectangle as it appears on screen.</summary>
public enum CropEdge { None, Left, Top, Right, Bottom }

/// <summary>
/// A crop and a quarter-turn rotation applied to a whole video on export. The crop is in the pixels of the source frame as it is
/// normally shown (after any rotation stored in the file); the rotation is applied after the crop, clockwise, in steps of 90 degrees.
/// </summary>
public sealed record VideoTransform(PixelCrop? Crop, int Rotation)
{
    /// <summary>Smallest crop edge length; smaller than this is almost certainly a slip, and some encoders reject tiny frames.</summary>
    public const int MinimumSize = 16;

    public static int NormalizeRotation(int degrees) => ((degrees % 360) + 360) % 360 / 90 * 90;

    public bool IsIdentity(int width, int height) => NormalizeRotation(Rotation) == 0 && (Crop is null || Crop == new PixelCrop(0, 0, width, height));

    /// <summary>The crop clamped into the frame, or the whole frame when none is set.</summary>
    public PixelCrop EffectiveCrop(int width, int height)
    {
        if (Crop is not { } crop)
            return new PixelCrop(0, 0, width, height);
        var x = Math.Clamp(crop.X, 0, Math.Max(0, width - 1));
        var y = Math.Clamp(crop.Y, 0, Math.Max(0, height - 1));
        return new PixelCrop(x, y, Math.Clamp(crop.Width, 1, width - x), Math.Clamp(crop.Height, 1, height - y));
    }

    /// <summary>
    /// What is actually encoded. H.264 with 4:2:0 colour needs even dimensions, so an odd width or height loses its last
    /// pixel column or row rather than gaining a black one.
    /// </summary>
    public PixelCrop EncodedCrop(int width, int height)
    {
        var crop = EffectiveCrop(width, height);
        return crop with { Width = Math.Max(2, crop.Width - crop.Width % 2), Height = Math.Max(2, crop.Height - crop.Height % 2) };
    }

    /// <summary>Width and height of the exported video.</summary>
    public (int Width, int Height) OutputSize(int width, int height)
    {
        var crop = EncodedCrop(width, height);
        return NormalizeRotation(Rotation) is 90 or 270 ? (crop.Height, crop.Width) : (crop.Width, crop.Height);
    }

    /// <summary>FFmpeg filters for this transform, crop first and then rotation; empty when nothing changes.</summary>
    public string Filter(int width, int height)
    {
        var parts = new List<string>();
        var crop = EncodedCrop(width, height);
        if (crop != new PixelCrop(0, 0, width, height))
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"crop={crop.Width}:{crop.Height}:{crop.X}:{crop.Y}"));
        switch (NormalizeRotation(Rotation))
        {
            case 90: parts.Add("transpose=1"); break;
            case 180: parts.Add("hflip"); parts.Add("vflip"); break;
            case 270: parts.Add("transpose=2"); break;
        }
        return string.Join(',', parts);
    }

    /// <summary>
    /// The crop as it appears once the frame has been turned clockwise by <paramref name="rotation"/>: a rectangle in the
    /// turned frame, whose size is height x width for 90 and 270 degrees.
    /// </summary>
    public static PixelCrop ToRotated(PixelCrop crop, int width, int height, int rotation)
    {
        var (left, top, right, bottom) = (crop.X, crop.Y, crop.X + crop.Width, crop.Y + crop.Height);
        return NormalizeRotation(rotation) switch
        {
            90 => FromEdges(height - bottom, left, height - top, right),
            180 => FromEdges(width - right, height - bottom, width - left, height - top),
            270 => FromEdges(top, width - right, bottom, width - left),
            _ => crop
        };
    }

    /// <summary>Inverse of <see cref="ToRotated"/>: <paramref name="width"/> and <paramref name="height"/> are those of the unturned source frame.</summary>
    public static PixelCrop FromRotated(PixelCrop rotated, int width, int height, int rotation)
    {
        var turned = NormalizeRotation(rotation);
        var (turnedWidth, turnedHeight) = turned is 90 or 270 ? (height, width) : (width, height);
        return ToRotated(rotated, turnedWidth, turnedHeight, 360 - turned);
    }

    /// <summary>
    /// Moves one edge of <paramref name="crop"/> (a rectangle inside a frame of the given size) by <paramref name="delta"/> pixels,
    /// positive towards the right or the bottom. The edge stops at the frame and <see cref="MinimumSize"/> short of the opposite edge.
    /// </summary>
    public static PixelCrop MoveEdge(PixelCrop crop, CropEdge edge, int delta, int width, int height)
    {
        var (left, top, right, bottom) = (crop.X, crop.Y, crop.X + crop.Width, crop.Y + crop.Height);
        var minimumWidth = Math.Min(MinimumSize, width);
        var minimumHeight = Math.Min(MinimumSize, height);
        switch (edge)
        {
            case CropEdge.Left: left = Math.Clamp(left + delta, 0, right - minimumWidth); break;
            case CropEdge.Right: right = Math.Clamp(right + delta, left + minimumWidth, width); break;
            case CropEdge.Top: top = Math.Clamp(top + delta, 0, bottom - minimumHeight); break;
            case CropEdge.Bottom: bottom = Math.Clamp(bottom + delta, top + minimumHeight, height); break;
        }
        return FromEdges(left, top, right, bottom);
    }

    private static PixelCrop FromEdges(int left, int top, int right, int bottom) => new(left, top, right - left, bottom - top);
}

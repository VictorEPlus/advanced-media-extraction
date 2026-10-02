using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;

namespace MediaWorkbench.Avalonia.Services;

/// <summary>
/// Playing video drawn by the app itself. VLC decodes every picture into memory; the newest one is copied into a
/// <see cref="WriteableBitmap"/> at most once per screen frame, and the preview draws that bitmap exactly like a still. There is no
/// separate VLC window, so nothing can flicker between "playing" and "paused" pictures and the preview can be zoomed or drawn on.
/// Ported from the WPF app's LiveVideo.
/// </summary>
public sealed class VideoBridge : IDisposable
{
    /// <summary>Wider pictures are scaled down by VLC to this width while playing.</summary>
    public const int MaximumWidth = 2560;

    private readonly object gate = new();
    // Kept as fields so the garbage collector never frees a delegate VLC still holds.
    private readonly MediaPlayer.LibVLCVideoFormatCb format;
    private readonly MediaPlayer.LibVLCVideoCleanupCb cleanup;
    private readonly MediaPlayer.LibVLCVideoLockCb lockPicture;
    private readonly MediaPlayer.LibVLCVideoDisplayCb display;
    private IntPtr decodeBuffer;
    private IntPtr latestBuffer;
    private int width;
    private int height;
    private int pitch;
    private int pictures;
    private int copiedPictures;
    private bool pullQueued;
    private bool disposed;

    public VideoBridge()
    {
        format = OnFormat;
        cleanup = OnCleanup;
        lockPicture = OnLock;
        display = OnDisplay;
    }

    /// <summary>The picture VLC showed last, on the UI thread. A new bitmap is made when the video size changes.</summary>
    public WriteableBitmap? Bitmap { get; private set; }

    /// <summary>Raised on the UI thread after a new picture has been copied into <see cref="Bitmap"/>.</summary>
    public event EventHandler? PictureShown;

    public void Attach(MediaPlayer player)
    {
        player.SetVideoFormatCallbacks(format, cleanup);
        player.SetVideoCallbacks(lockPicture, null, display);
    }

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint sourceWidth, ref uint sourceHeight, ref uint pitches, ref uint lines)
    {
        // RV32 is 32-bit BGRX, which is Bgra8888 with the alpha ignored.
        Marshal.Copy("RV32"u8.ToArray(), 0, chroma, 4);
        var (shownWidth, shownHeight) = ((int)sourceWidth, (int)sourceHeight);
        if (shownWidth > MaximumWidth)
        {
            shownHeight = Math.Max(2, (int)Math.Round(shownHeight * (double)MaximumWidth / shownWidth) & ~1);
            shownWidth = MaximumWidth;
        }
        sourceWidth = (uint)shownWidth;
        sourceHeight = (uint)shownHeight;
        var rowBytes = shownWidth * 4;
        pitches = (uint)rowBytes;
        lines = (uint)shownHeight;
        lock (gate)
        {
            FreeBuffers();
            width = shownWidth;
            height = shownHeight;
            pitch = rowBytes;
            // A few spare rows: some decoders write a little past the last line of a plane.
            var size = (nint)rowBytes * (shownHeight + 32);
            decodeBuffer = Marshal.AllocHGlobal(size);
            latestBuffer = Marshal.AllocHGlobal(size);
        }
        return 1;
    }

    private void OnCleanup(ref IntPtr opaque)
    {
        lock (gate)
        {
            FreeBuffers();
            width = height = pitch = 0;
        }
    }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, decodeBuffer);
        return IntPtr.Zero;
    }

    private unsafe void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        lock (gate)
        {
            if (disposed || decodeBuffer == IntPtr.Zero)
                return;
            var bytes = (long)pitch * height;
            Buffer.MemoryCopy((void*)decodeBuffer, (void*)latestBuffer, bytes, bytes);
            pictures++;
            if (pullQueued)
                return;
            pullQueued = true;
        }
        // One copy to the screen per UI frame at most: if the UI is busy, pictures in between are skipped.
        Dispatcher.UIThread.Post(Pull, DispatcherPriority.Render);
    }

    private unsafe void Pull()
    {
        lock (gate)
        {
            pullQueued = false;
            if (disposed || latestBuffer == IntPtr.Zero || width <= 0 || pictures == copiedPictures)
                return;
            if (Bitmap is null || Bitmap.PixelSize.Width != width || Bitmap.PixelSize.Height != height)
                Bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using (var target = Bitmap.Lock())
            {
                if (target.RowBytes == pitch)
                    Buffer.MemoryCopy((void*)latestBuffer, (void*)target.Address, (long)pitch * height, (long)pitch * height);
                else
                    for (var row = 0; row < height; row++)
                        Buffer.MemoryCopy((byte*)latestBuffer + (long)row * pitch, (byte*)target.Address + (long)row * target.RowBytes, target.RowBytes, Math.Min(pitch, target.RowBytes));
            }
            copiedPictures = pictures;
        }
        PictureShown?.Invoke(this, EventArgs.Empty);
    }

    private void FreeBuffers()
    {
        if (decodeBuffer != IntPtr.Zero) Marshal.FreeHGlobal(decodeBuffer);
        if (latestBuffer != IntPtr.Zero) Marshal.FreeHGlobal(latestBuffer);
        decodeBuffer = latestBuffer = IntPtr.Zero;
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            FreeBuffers();
        }
    }
}

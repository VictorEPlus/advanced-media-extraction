using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace MediaWorkbench.App;

/// <summary>
/// Playing video drawn by the app itself. VLC decodes each picture into memory and hands it over; the picture is copied into a
/// <see cref="WriteableBitmap"/> that the preview draws exactly like a still frame. Before, VLC drew into a window of its own laid
/// over the preview: switching between that window and the still showed a wrong picture for a frame or two, VLC wrote text over
/// the video, and nothing could be drawn on top of it or zoomed. Now playing and paused are the same surface, so the hand-over
/// cannot flash, and pausing leaves on screen exactly the picture VLC showed last.
/// </summary>
internal sealed class LiveVideo : IDisposable
{
    /// <summary>Wider pictures are scaled down by VLC to this width; the preview never shows more pixels than that while playing.</summary>
    public const int MaximumWidth = 2560;

    private readonly Dispatcher dispatcher;
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
    private bool frozen;
    private bool disposed;

    public LiveVideo(Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
        format = OnFormat;
        cleanup = OnCleanup;
        lockPicture = OnLock;
        display = OnDisplay;
    }

    /// <summary>The picture VLC showed last, on the UI thread. Replaced by a new bitmap when the video size changes.</summary>
    public WriteableBitmap? Bitmap { get; private set; }

    /// <summary>Pictures copied into <see cref="Bitmap"/> so far. Goes up by one for every picture VLC shows.</summary>
    public int PictureCount => copiedPictures;

    /// <summary>Raised on the UI thread after a new picture has been copied into <see cref="Bitmap"/>, or a new bitmap made.</summary>
    public event EventHandler? PictureShown;

    public void Attach(MediaPlayer player)
    {
        player.SetVideoFormatCallbacks(format, cleanup);
        player.SetVideoCallbacks(lockPicture, null, display);
    }

    /// <summary>Stops copying new pictures, so what is on screen stays put while it is being identified. <see cref="Thaw"/> undoes it.</summary>
    public void Freeze() { lock (gate) frozen = true; }

    public void Thaw() { lock (gate) frozen = false; }

    /// <summary>A frozen copy of the picture on screen, or null when nothing has been shown.</summary>
    public BitmapSource? Capture()
    {
        if (Bitmap is not { } bitmap)
            return null;
        var copy = bitmap.Clone();
        copy.Freeze();
        return copy;
    }

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint sourceWidth, ref uint sourceHeight, ref uint pitches, ref uint lines)
    {
        // RV32 is 32-bit BGRX, which WPF draws directly as Bgr32.
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
            if (disposed || decodeBuffer == IntPtr.Zero || frozen)
                return;
            var bytes = (long)pitch * height;
            Buffer.MemoryCopy((void*)decodeBuffer, (void*)latestBuffer, bytes, bytes);
            pictures++;
            if (pullQueued)
                return;
            pullQueued = true;
        }
        // One copy to the screen per UI frame at most: if the UI is busy, pictures in between are simply skipped.
        dispatcher.BeginInvoke(DispatcherPriority.Render, Pull);
    }

    private unsafe void Pull()
    {
        lock (gate)
        {
            pullQueued = false;
            if (disposed || latestBuffer == IntPtr.Zero || width <= 0 || pictures == copiedPictures)
                return;
            if (Bitmap is null || Bitmap.PixelWidth != width || Bitmap.PixelHeight != height)
                Bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
            var bitmap = Bitmap;
            bitmap.Lock();
            try
            {
                if (bitmap.BackBufferStride == pitch)
                    Buffer.MemoryCopy((void*)latestBuffer, (void*)bitmap.BackBuffer, (long)pitch * height, (long)pitch * height);
                else
                    for (var row = 0; row < height; row++)
                        Buffer.MemoryCopy((byte*)latestBuffer + (long)row * pitch, (byte*)bitmap.BackBuffer + (long)row * bitmap.BackBufferStride, bitmap.BackBufferStride, Math.Min(pitch, bitmap.BackBufferStride));
                bitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
            }
            finally { bitmap.Unlock(); }
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

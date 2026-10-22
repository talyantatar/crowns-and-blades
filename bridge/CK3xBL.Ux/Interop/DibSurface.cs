using System.Drawing;
using System.Drawing.Imaging;
using static CK3xBL.Ux.Interop.Native;

namespace CK3xBL.Ux.Interop;

/// <summary>
/// A 32-bit top-down DIB section: the pixel format UpdateLayeredWindow consumes (BGRA, premultiplied when used with
/// per-pixel alpha) and PrintWindow can render into. GDI+ can draw straight into its memory through <see cref="CreateView"/>.
/// The HBITMAP is a process-wide GDI object, so a surface may be filled on one thread and presented on another
/// (never concurrently written).
/// </summary>
internal sealed class DibSurface : IDisposable
{
    public nint Handle { get; private set; }
    public nint Bits { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;

    private DibSurface(nint handle, nint bits, int width, int height)
    {
        Handle = handle; Bits = bits; Width = width; Height = height;
    }

    public static DibSurface Create(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), $"{width}x{height}");
        var bmi = new BITMAPINFOHEADER
        {
            biSize = System.Runtime.InteropServices.Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,                 // negative = top-down rows, same order as GDI+ bitmaps
            biPlanes = 1,
            biBitCount = 32,
            biCompression = BI_RGB,
        };
        nint handle = CreateDIBSection(0, bmi, DIB_RGB_COLORS, out nint bits, 0, 0);
        if (handle == 0 || bits == 0)
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
                $"CreateDIBSection {width}x{height} failed");
        return new DibSurface(handle, bits, width, height);
    }

    /// <summary>
    /// A GDI+ bitmap sharing this surface's memory (no copy). Use <see cref="PixelFormat.Format32bppPArgb"/> to draw content
    /// for per-pixel-alpha layers and <see cref="PixelFormat.Format32bppRgb"/> to read opaque captures. Dispose the view
    /// before the surface.
    /// </summary>
    public Bitmap CreateView(PixelFormat format) => new(Width, Height, Stride, format, Bits);

    public void Dispose()
    {
        if (Handle != 0) { DeleteObject(Handle); Handle = 0; }
    }
}

using CK3xBL.Ux.Interop;

namespace CK3xBL.Ux;

/// <summary>A rectangle in physical screen pixels (Per-Monitor-V2 coordinates).</summary>
public readonly record struct ScreenRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    internal static ScreenRect From(Native.RECT r) => new(r.Left, r.Top, r.Width, r.Height);
    internal Native.RECT ToRect() => new(Left, Top, Right, Bottom);

    public override string ToString() => $"({Left},{Top}) {Width}x{Height}";
}

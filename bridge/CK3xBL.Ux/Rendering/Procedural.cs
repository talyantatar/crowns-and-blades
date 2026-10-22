using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;

namespace CK3xBL.Ux.Rendering;

/// <summary>Small procedural-graphics toolkit: hash noise, fractal value noise on a grid, box blur on premultiplied bitmaps.</summary>
internal static class Procedural
{
    /// <summary>Deterministic integer hash to [0, 1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Hash01(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)x * 0x8DA6B343u ^ (uint)y * 0xD8163841u ^ (uint)seed * 0xCB1AB31Fu;
            h ^= h >> 15; h *= 0x2C1B3C6Du;
            h ^= h >> 12; h *= 0x297A2D39u;
            h ^= h >> 15;
            return (h & 0xFFFFFF) / 16777216f;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Value noise in [0, 1) at a continuous position, lattice spacing 1, quintic-smoothed.</summary>
    private static float ValueNoise(float x, float y, int seed)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        fx = fx * fx * fx * (fx * (fx * 6 - 15) + 10);
        fy = fy * fy * fy * (fy * (fy * 6 - 15) + 10);
        float a = Hash01(x0, y0, seed), b = Hash01(x0 + 1, y0, seed);
        float c = Hash01(x0, y0 + 1, seed), d = Hash01(x0 + 1, y0 + 1, seed);
        return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
    }

    /// <summary>
    /// Fractal (fBm) value noise sampled on a coarse grid: <paramref name="step"/> pixels between grid points, the largest
    /// feature is <paramref name="largestFeature"/> pixels and each octave halves it. Returns a row-major grid with values
    /// normalised to [0, 1]. Sample it with <see cref="SampleGrid"/>.
    /// </summary>
    public static float[] FractalGrid(int gridW, int gridH, float step, float largestFeature, int octaves, int seed)
    {
        var grid = new float[gridW * gridH];
        Parallel.For(0, gridH, gy =>
        {
            for (int gx = 0; gx < gridW; gx++)
            {
                float px = gx * step, py = gy * step;
                float sum = 0, amp = 1, norm = 0, scale = 1f / largestFeature;
                for (int o = 0; o < octaves; o++)
                {
                    sum += amp * ValueNoise(px * scale, py * scale, seed + o * 101);
                    norm += amp;
                    amp *= 0.5f;
                    scale *= 2f;
                }
                grid[gy * gridW + gx] = sum / norm;
            }
        });
        // fBm of value noise clusters around 0.5; stretch to the full range so callers get predictable contrast.
        float min = float.MaxValue, max = float.MinValue;
        foreach (var v in grid) { if (v < min) min = v; if (v > max) max = v; }
        float range = Math.Max(1e-5f, max - min);
        for (int i = 0; i < grid.Length; i++) grid[i] = (grid[i] - min) / range;
        return grid;
    }

    /// <summary>Bilinear sample of a row-major grid at grid coordinates (clamped at the edges).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SampleGrid(float[] grid, int gridW, int gridH, float gx, float gy)
    {
        gx = Math.Clamp(gx, 0, gridW - 1.001f);
        gy = Math.Clamp(gy, 0, gridH - 1.001f);
        int x0 = (int)gx, y0 = (int)gy;
        float fx = gx - x0, fy = gy - y0;
        int i = y0 * gridW + x0;
        float a = grid[i], b = grid[i + 1], c = grid[i + gridW], d = grid[i + gridW + 1];
        return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
    }

    /// <summary>
    /// In-place approximate Gaussian blur (three box passes, horizontal then vertical) of a 32bpp bitmap. Works on any
    /// 4-byte format; on premultiplied ARGB it is the correct blur of a transparent layer.
    /// </summary>
    public static unsafe void BoxBlur(Bitmap bitmap, int radius, int passes = 3)
    {
        if (radius < 1) return;
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, bitmap.PixelFormat);
        try
        {
            int w = bitmap.Width, h = bitmap.Height, stride = data.Stride;
            nint basePtr = data.Scan0;
            for (int p = 0; p < passes; p++)
            {
                Parallel.For(0, h, y => BlurLine((byte*)basePtr + (long)y * stride, w, 4, radius));
                Parallel.For(0, w, x => BlurLine((byte*)basePtr + x * 4, h, stride, radius));
            }
        }
        finally { bitmap.UnlockBits(data); }
    }

    /// <summary>Sliding-window box blur of one line of 4-byte pixels (count pixels, byte distance step between them).</summary>
    private static unsafe void BlurLine(byte* start, int count, int step, int radius)
    {
        // Copy the line first so the window reads unblurred values.
        int[] buffer = System.Buffers.ArrayPool<int>.Shared.Rent(count * 4);
        try
        {
            for (int i = 0; i < count; i++)
            {
                byte* px = start + (long)i * step;
                buffer[i * 4] = px[0]; buffer[i * 4 + 1] = px[1]; buffer[i * 4 + 2] = px[2]; buffer[i * 4 + 3] = px[3];
            }
            int window = radius * 2 + 1;
            int s0 = 0, s1 = 0, s2 = 0, s3 = 0;
            // Edge pixels are repeated outward (clamp), so borders do not darken.
            for (int k = -radius; k <= radius; k++)
            {
                int j = Math.Clamp(k, 0, count - 1) * 4;
                s0 += buffer[j]; s1 += buffer[j + 1]; s2 += buffer[j + 2]; s3 += buffer[j + 3];
            }
            for (int i = 0; i < count; i++)
            {
                byte* px = start + (long)i * step;
                px[0] = (byte)(s0 / window); px[1] = (byte)(s1 / window); px[2] = (byte)(s2 / window); px[3] = (byte)(s3 / window);
                int outJ = Math.Clamp(i - radius, 0, count - 1) * 4;
                int inJ = Math.Clamp(i + radius + 1, 0, count - 1) * 4;
                s0 += buffer[inJ] - buffer[outJ]; s1 += buffer[inJ + 1] - buffer[outJ + 1];
                s2 += buffer[inJ + 2] - buffer[outJ + 2]; s3 += buffer[inJ + 3] - buffer[outJ + 3];
            }
        }
        finally { System.Buffers.ArrayPool<int>.Shared.Return(buffer); }
    }
}

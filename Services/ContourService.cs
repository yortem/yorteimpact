using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YorteImpact.Models;

namespace YorteImpact.Services;

/// <summary>
/// Builds the selection outline that hugs the element's ACTUAL opaque shape
/// (marching ants) instead of its bounding box: rounded corners for
/// shapes, and the true opaque-pixel contour — holes/notches included — for
/// images. Geometry is returned in pasteboard (canvas) coordinates, unrotated;
/// the overlay applies the element rotation when drawing.
/// </summary>
public static class ContourService
{
    private const int MaxTraceSize = 256; // trace resolution cap (ants don't need pixels)
    private const byte AlphaThreshold = 127;

    public static Geometry BuildContour(DesignElement el)
    {
        try
        {
            return el switch
            {
                ShapeElement s => ShapeContour(s),
                ImageElement im => ImageContour(im),
                TextElement t => BoxContour(t, 0, 4), // breathing room so ants don't hide punctuation
                _ => BoxContour(el, 0),
            };
        }
        catch
        {
            return BoxContour(el, 0); // never break selection over a trace failure
        }
    }

    // ---------------- shapes: analytic rounded rect (+ half border) ----------------

    private static Geometry ShapeContour(ShapeElement s)
    {
        double r = Math.Min(s.CornerRadius, Math.Min(s.Width, s.Height) / 2);
        double t = s.StrokeThickness / 2;
        return BoxContour(s, r, t);
    }

    /// <summary>Cheap (rounded) bounding-box outline, used while dragging.</summary>
    public static Geometry Box(DesignElement el)
    {
        if (el is TextElement t) return BoxContour(t, 0, 4);
        double radius = el switch
        {
            ShapeElement s => s.CornerRadius,
            ImageElement im => im.CornerRadius,
            _ => 0,
        };
        radius = Math.Clamp(radius, 0, Math.Min(el.Width, el.Height) / 2);
        return BoxContour(el, radius);
    }

    private static Geometry BoxContour(DesignElement el, double radius, double inflate = 0)
    {
        var g = new RectangleGeometry(
            new Rect(el.X - inflate, el.Y - inflate,
                     el.Width + inflate * 2, el.Height + inflate * 2),
            radius + inflate, radius + inflate);
        g.Freeze();
        return g;
    }

    // ---------------- images: marching-squares opaque contour ----------------

    private static Geometry ImageContour(ImageElement el)
    {
        if (el.Source is not BitmapSource src || src.PixelWidth <= 0 || src.PixelHeight <= 0)
            return BoxContour(el, el.CornerRadius);

        // Visible source rect for UniformToFill + center crop (same math as erase).
        int pw = src.PixelWidth, ph = src.PixelHeight;
        double s = Math.Max(el.Width / pw, el.Height / ph);
        double offX = (pw * s - el.Width) / 2;
        double offY = (ph * s - el.Height) / 2;
        int cx = Math.Clamp((int)Math.Floor(offX / s), 0, pw - 1);
        int cy = Math.Clamp((int)Math.Floor(offY / s), 0, ph - 1);
        int cw = Math.Clamp((int)Math.Ceiling(el.Width / s), 1, pw - cx);
        int ch = Math.Clamp((int)Math.Ceiling(el.Height / s), 1, ph - cy);

        var crop = new CroppedBitmap(src, new Int32Rect(cx, cy, cw, ch));
        double k = Math.Min(1.0, MaxTraceSize / (double)Math.Max(cw, ch));
        BitmapSource small = k < 1 ? new TransformedBitmap(crop, new ScaleTransform(k, k)) : crop;
        var fmt = new FormatConvertedBitmap(small, PixelFormats.Pbgra32, null, 0);

        int w = fmt.PixelWidth, h = fmt.PixelHeight;
        int stride = w * 4;
        var px = new byte[h * stride];
        fmt.CopyPixels(px, stride, 0);

        var occ = new bool[w, h];
        bool any = false;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (px[(y * w + x) * 4 + 3] > AlphaThreshold) { occ[x, y] = true; any = true; }
        if (!any) return BoxContour(el, el.CornerRadius);

        var loops = TraceLoops(occ, w, h);
        if (loops.Count == 0) return BoxContour(el, el.CornerRadius);

        // small-px -> element-local DIPs -> canvas coords (mirror for flip).
        double dipPerSmall = s / k;
        double baseX = cx * s - offX;
        double baseY = cy * s - offY;
        var geo = new PathGeometry();
        foreach (var loop in loops)
        {
            if (loop.Count < 3) continue;
            var fig = new PathFigure { IsClosed = true, IsFilled = false };
            bool first = true;
            foreach (var p in loop)
            {
                double lx = baseX + p.X * dipPerSmall;
                double ly = baseY + p.Y * dipPerSmall;
                if (el.FlipHorizontal) lx = el.Width - lx;
                if (el.FlipVertical) ly = el.Height - ly;
                var pt = new Point(el.X + lx, el.Y + ly);
                if (first) { fig.StartPoint = pt; first = false; }
                else fig.Segments.Add(new LineSegment(pt, true));
            }
            if (!first) geo.Figures.Add(fig);
        }
        if (geo.Figures.Count == 0) return BoxContour(el, el.CornerRadius);
        geo.Freeze();
        return geo;
    }

    // ---------------- boundary tracing -> closed loops ----------------
    //
    // Exact pixel-edge extraction (no case-table ambiguity): every edge shared
    // with a transparent/out-of-bounds neighbour becomes one unit segment,
    // oriented head-to-tail so segments chain into closed loops directly.

    private static List<List<Point>> TraceLoops(bool[,] occ, int w, int h)
    {
        bool O(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && occ[x, y];

        var segs = new List<(long a, long b, double ax, double ay, double bx, double by)>();
        long Key(double x, double y) => ((long)(x * 2) << 32) | (uint)(y * 2);
        void Add((double x, double y) p, (double x, double y) q) =>
            segs.Add((Key(p.x, p.y), Key(q.x, q.y), p.x, p.y, q.x, q.y));

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (!occ[x, y]) continue;
                if (!O(x, y - 1)) Add(((double)x, y), ((double)x + 1, y));                 // top
                if (!O(x + 1, y)) Add(((double)x + 1, y), ((double)x + 1, (double)y + 1)); // right
                if (!O(x, y + 1)) Add(((double)x + 1, y + 1), ((double)x, (double)y + 1)); // bottom
                if (!O(x - 1, y)) Add(((double)x, y + 1), ((double)x, y));                 // left
            }
        }

        var from = new Dictionary<long, List<int>>();
        for (int n = 0; n < segs.Count; n++)
        {
            if (!from.TryGetValue(segs[n].a, out var list)) from[segs[n].a] = list = new List<int>();
            list.Add(n);
        }

        var used = new bool[segs.Count];
        var loops = new List<List<Point>>();
        for (int n = 0; n < segs.Count; n++)
        {
            if (used[n]) continue;
            used[n] = true;
            var pts = new List<Point>
            {
                new(segs[n].ax, segs[n].ay),
                new(segs[n].bx, segs[n].by),
            };
            long cur = segs[n].b, start = segs[n].a;
            while (cur != start)
            {
                if (!from.TryGetValue(cur, out var cand)) break;
                int nxt = -1;
                foreach (int c in cand) if (!used[c]) { nxt = c; break; }
                if (nxt < 0) break;
                used[nxt] = true;
                pts.Add(new Point(segs[nxt].bx, segs[nxt].by));
                cur = segs[nxt].b;
                if (pts.Count > segs.Count + 4) break; // safety valve
            }
            loops.Add(Simplify(pts));
        }
        return loops;
    }

    private static List<Point> Simplify(List<Point> pts)
    {
        if (pts.Count < 4) return pts;
        var closed = new List<Point>(pts) { pts[0] };
        var outPts = new List<Point> { closed[0] };
        for (int i = 1; i < closed.Count - 1; i++)
        {
            var p0 = outPts[^1]; var p1 = closed[i]; var p2 = closed[i + 1];
            double cross = (p1.X - p0.X) * (p2.Y - p1.Y) - (p1.Y - p0.Y) * (p2.X - p1.X);
            if (Math.Abs(cross) > 1e-9) outPts.Add(p1);
        }
        outPts.Add(closed[^1]);
        return outPts;
    }
}

using System.Windows;
using YorteImpact.Controls;
using YorteImpact.Models;

namespace YorteImpact.Services;

/// <summary>
/// Pure resize math (no UI): given the original rect, rotation, active handle
/// and pointer delta in the element's LOCAL (unrotated) frame, compute the new
/// rect with the opposite edge(s) pinned. Edge handles provably preserve the
/// perpendicular axis (W never touches Y/Height, etc.).
/// </summary>
public static class ResizeService
{
    public const double MinSize = 10;

    public static Rect Compute(
        double origX, double origY, double origW, double origH,
        double thetaDeg, HandleKind handle, double dx, double dy)
    {
        double left = 0, top = 0, right = origW, bottom = origH;
        switch (handle)
        {
            case HandleKind.E: right = Math.Max(MinSize, origW + dx); break;
            case HandleKind.W: left = Math.Min(origW - MinSize, dx); break;
            case HandleKind.S: bottom = Math.Max(MinSize, origH + dy); break;
            case HandleKind.N: top = Math.Min(origH - MinSize, dy); break;
            case HandleKind.SE: right = Math.Max(MinSize, origW + dx); bottom = Math.Max(MinSize, origH + dy); break;
            case HandleKind.SW: left = Math.Min(origW - MinSize, dx); bottom = Math.Max(MinSize, origH + dy); break;
            case HandleKind.NE: right = Math.Max(MinSize, origW + dx); top = Math.Min(origH - MinSize, dy); break;
            case HandleKind.NW: left = Math.Min(origW - MinSize, dx); top = Math.Min(origH - MinSize, dy); break;
            default: return new Rect(origX, origY, origW, origH);
        }

        double newW = Math.Max(MinSize, right - left);
        double newH = Math.Max(MinSize, bottom - top);

        // Local center offset, rotated back into canvas space.
        double theta = thetaDeg * Math.PI / 180.0;
        double ox = (left + right) / 2 - origW / 2;
        double oy = (top + bottom) / 2 - origH / 2;
        double c = Math.Cos(theta), s = Math.Sin(theta);
        double origCx = origX + origW / 2, origCy = origY + origH / 2;
        double newCx = origCx + ox * c - oy * s;
        double newCy = origCy + ox * s + oy * c;

        return new Rect(newCx - newW / 2, newCy - newH / 2, newW, newH);
    }
}

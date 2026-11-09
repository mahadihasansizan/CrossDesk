namespace DeskRelay;

static class PointerGeometry
{
    // Map the crossing height proportionally. Movement itself stays in destination pixels.
    public static double MapHeight(double pixel, int sourceHeight, int destinationHeight) =>
        Math.Clamp(pixel / Math.Max(1, sourceHeight - 1), 0, 1) * Math.Max(0, destinationHeight - 1);
}

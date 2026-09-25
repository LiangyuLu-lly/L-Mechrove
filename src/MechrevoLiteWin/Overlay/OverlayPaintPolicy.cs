namespace MechrevoLite.Overlay;

internal static class OverlayPaintPolicy
{
    internal static bool ShouldInvalidate(string? lastPainted, string next) =>
        lastPainted != next;
}

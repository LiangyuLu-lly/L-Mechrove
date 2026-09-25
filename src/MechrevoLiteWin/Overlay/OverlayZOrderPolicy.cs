namespace MechrevoLite.Overlay;

internal static class OverlayZOrderPolicy
{
    internal static bool ShouldReassertTopmost(bool currentlyTopmost) => !currentlyTopmost;
}

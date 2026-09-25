using MechrevoLite.Hardware;
using MechrevoLite.Properties;

namespace MechrevoLite.Mode;

/// <summary>
/// Which performance mode a secondary editor is bound to.
/// Silent turbo is not a fifth firmware mode: it is <see cref="MechrevoService.ModeTurbo"/>
/// plus the silent flag, and it shares that Turbo detail slot.
/// </summary>
public readonly record struct ModeEditorTarget(int VisualMode, bool silentSubMode)
{
    public static ModeEditorTarget Office => new(MechrevoService.ModeOffice, false);

    public static ModeEditorTarget Gaming => new(MechrevoService.ModeGaming, false);

    public static ModeEditorTarget Turbo => new(MechrevoService.ModeTurbo, false);

    public static ModeEditorTarget SilentTurbo => new(MechrevoService.ModeTurbo, true);

    public static ModeEditorTarget Custom => new(MechrevoService.ModeCustom, false);
}

/// <summary>
/// <c>SET_OPERATING_MODE_DETAIL</c> has no mode field. The vendor service applies it to the
/// operating mode that is already running (official HomePage turbo GPU OC is the proof).
/// A mismatched edit is refused. There is no "write the custom slot instead" outcome.
/// </summary>
public enum FirmwareDetailWrite
{
    ApplyToActiveMode,
    RefuseInactiveMode,
}

public enum DetailCommit
{
    Applied,
    RejectedByDevice,
    RefusedWrongMode,
}

public static class ModeSecondaryEditor
{
    public static string EditorMenuText => Strings.EditorMenu;

    /// <summary>Firmware <c>OperatingMode</c> is only office / gaming / turbo / customize.</summary>
    public static bool IsFirmwareVisualMode(int visualMode) => visualMode is
        MechrevoService.ModeGaming or MechrevoService.ModeTurbo
        or MechrevoService.ModeOffice or MechrevoService.ModeCustom;

    public static string DisplayName(ModeEditorTarget target)
    {
        if (target.silentSubMode && target.VisualMode == MechrevoService.ModeTurbo)
            return Properties.Strings.SilentTurbo;
        return target.VisualMode switch
        {
            MechrevoService.ModeOffice => Properties.Strings.Silent,
            MechrevoService.ModeGaming => Properties.Strings.Balanced,
            MechrevoService.ModeTurbo => Properties.Strings.Turbo,
            MechrevoService.ModeCustom => Strings.ModeCustom,
            _ => Strings.UnknownMode,
        };
    }

    public static string Title(ModeEditorTarget target) =>
        target.VisualMode == MechrevoService.ModeCustom
            ? Strings.CustomModeTitle
            : DisplayName(target) + " · " + EditorMenuText;

    public static string TrayItemText(ModeEditorTarget target) =>
        EditorMenuText + " · " + DisplayName(target);

    public static string Hint(ModeEditorTarget target)
    {
        if (target.VisualMode == MechrevoService.ModeCustom)
            return Strings.CustomModeHint;

        string name = DisplayName(target);
        string shared = target.silentSubMode ? Strings.SilentTurboSharedSlot : "";
        return string.Format(Strings.FirmwareDetailHint, name) + shared;
    }

    public static string RefusedWriteText(ModeEditorTarget target) =>
        string.Format(Strings.RefusedWrite, DisplayName(target));

    public static FirmwareDetailWrite DecideFirmwareWrite(ModeEditorTarget editing, int activeVisualMode)
    {
        if (!IsFirmwareVisualMode(editing.VisualMode) || !IsFirmwareVisualMode(activeVisualMode))
            return FirmwareDetailWrite.RefuseInactiveMode;
        return editing.VisualMode == activeVisualMode
            ? FirmwareDetailWrite.ApplyToActiveMode
            : FirmwareDetailWrite.RefuseInactiveMode;
    }

    /// <summary>True only when the editor is the custom mode and that mode is the one running.</summary>
    public static bool WritesCustomSlot(ModeEditorTarget editing, int activeVisualMode) =>
        editing.VisualMode == MechrevoService.ModeCustom
        && DecideFirmwareWrite(editing, activeVisualMode) == FirmwareDetailWrite.ApplyToActiveMode;
}

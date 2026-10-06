using Avalonia.Input;

namespace MMW.App;

/// <summary>Keyboard shortcuts, using Cmd on macOS and Ctrl elsewhere.</summary>
public static class Gestures
{
    private static readonly KeyModifiers s_cmd = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    /// <summary>The platform's command modifier (Cmd on macOS, Ctrl elsewhere).</summary>
    public static KeyModifiers Command => s_cmd;

    public static KeyGesture Open { get; } = new(Key.O, s_cmd);

    public static KeyGesture Save { get; } = new(Key.S, s_cmd);

    public static KeyGesture SaveAs { get; } = new(Key.S, s_cmd | KeyModifiers.Shift);

    public static KeyGesture Close { get; } = new(Key.W, s_cmd);

    public static KeyGesture Undo { get; } = new(Key.Z, s_cmd);

    public static KeyGesture Redo { get; } = OperatingSystem.IsMacOS() ? new(Key.Z, s_cmd | KeyModifiers.Shift) : new(Key.Y, s_cmd);

    public static KeyGesture Quit { get; } = new(Key.Q, s_cmd);

    public static KeyGesture Log { get; } = new(Key.L, s_cmd | KeyModifiers.Alt);

    public static KeyGesture Delete { get; } = new(Key.Delete);

    public static KeyGesture Import { get; } = new(Key.I, s_cmd);

    public static KeyGesture Search { get; } = new(Key.K, s_cmd);

    public static KeyGesture Preset(int n) => new(Key.D0 + n, s_cmd);

    public static KeyGesture Preset1 { get; } = Preset(1);

    public static KeyGesture Preset2 { get; } = Preset(2);

    public static KeyGesture Preset3 { get; } = Preset(3);

    public static KeyGesture Preset4 { get; } = Preset(4);

    public static KeyGesture Preset5 { get; } = Preset(5);

    public static KeyGesture Preset6 { get; } = Preset(6);

    public static KeyGesture Preset7 { get; } = Preset(7);

    public static KeyGesture Preset8 { get; } = Preset(8);

    public static KeyGesture Preset9 { get; } = Preset(9);
}

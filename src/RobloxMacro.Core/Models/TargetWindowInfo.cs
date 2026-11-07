using System;

namespace RobloxMacro.Core.Models;

public enum WindowFocusState
{
    FocusedAndVisible,
    VisibleUnfocused,
    Minimized,
    HiddenOrUnavailable,
    Closed
}

public sealed class TargetWindowInfo
{
    public IntPtr Handle { get; set; }
    public uint ProcessId { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string WindowTitle { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public bool IsRobloxProcess { get; set; }
    public WindowFocusState FocusState { get; set; }
    public int ClientWidth { get; set; }
    public int ClientHeight { get; set; }
    public int ScreenClientLeft { get; set; }
    public int ScreenClientTop { get; set; }
    public uint Dpi { get; set; } = 96;
    public double DpiScaleFactor => Dpi > 0 ? Dpi / 96.0 : 1.0;

    public string HandleHex => $"0x{Handle.ToInt64():X}";

    public string DisplayName =>
        IsRobloxProcess
            ? $"[ROBLOX] {ProcessName}.exe (PID {ProcessId}) — \"{WindowTitle}\" [{HandleHex}]"
            : $"{ProcessName}.exe (PID {ProcessId}) — \"{WindowTitle}\" [{HandleHex}]";

    public string StatusSummary => FocusState switch
    {
        WindowFocusState.FocusedAndVisible => $"Focused & Visible ({ClientWidth}x{ClientHeight} @ {DpiScaleFactor * 100:0}% DPI)",
        WindowFocusState.VisibleUnfocused => $"Visible (Unfocused / Background) ({ClientWidth}x{ClientHeight} @ {DpiScaleFactor * 100:0}% DPI)",
        WindowFocusState.Minimized => "Minimized to Taskbar (Iconic)",
        WindowFocusState.HiddenOrUnavailable => "Window Handle Zero / Hidden",
        WindowFocusState.Closed => "Process or Window Closed",
        _ => "Unknown"
    };
}

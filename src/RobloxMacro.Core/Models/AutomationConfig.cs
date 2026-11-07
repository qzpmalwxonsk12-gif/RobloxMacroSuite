using System;

namespace RobloxMacro.Core.Models;

/// <summary>
/// Input delivery mechanism used to target the selected window.
/// </summary>
public enum InputDeliveryMode
{
    /// <summary>
    /// Recommended for Roblox: Uses Win32 SendInput only when the selected Roblox window is the active
    /// foreground window. Automatically pauses within milliseconds if Roblox loses focus or minimizes,
    /// guaranteeing that clicks never hit other applications.
    /// </summary>
    ForegroundGuardedSendInput,

    /// <summary>
    /// Experimental / Background Win32 Message Mode: Dispatches WM_MOUSEMOVE, WM_LBUTTONDOWN, and WM_LBUTTONUP
    /// directly to the target HWND (and optional child HWNDs) without moving the physical cursor or stealing focus.
    /// Works on standard Win32 controls in all 3 scenarios, but is ignored by Roblox's UserInputService when unfocused/minimized.
    /// </summary>
    BackgroundWin32Message
}

public enum ClickActionMode
{
    /// <summary>
    /// Repeatedly presses and releases the left mouse button at the configured interval.
    /// </summary>
    RepeatedLeftClick,

    /// <summary>
    /// Holds the left mouse button down continuously (or in long hold cycles) and guarantees release on stop/pause.
    /// </summary>
    ClickAndHold
}

public enum ClickPositionMode
{
    /// <summary>
    /// Clicks at the center of the target window's client area (dynamically tracks window move/resize).
    /// </summary>
    ClientCenter,

    /// <summary>
    /// Clicks at a specific client-relative (X, Y) coordinate inside the target window,
    /// optionally scaling proportionally if the window is resized.
    /// </summary>
    FixedClientPosition,

    /// <summary>
    /// In Foreground-Guarded mode, clicks wherever the user's cursor is currently positioned inside the target window.
    /// </summary>
    CurrentCursorPosition
}

public enum AutomationRunState
{
    Idle,
    Countdown,
    Running,
    AutoPausedFocusLost,
    Stopped
}

public sealed class AutomationConfig
{
    public InputDeliveryMode DeliveryMode { get; set; } = InputDeliveryMode.ForegroundGuardedSendInput;
    public ClickActionMode ActionMode { get; set; } = ClickActionMode.RepeatedLeftClick;
    public ClickPositionMode PositionMode { get; set; } = ClickPositionMode.ClientCenter;

    public int ClickIntervalMs { get; set; } = 100;
    public int ClickHoldDurationMs { get; set; } = 35;

    public int TargetClientX { get; set; } = 400;
    public int TargetClientY { get; set; } = 300;
    public int ReferenceClientWidth { get; set; } = 800;
    public int ReferenceClientHeight { get; set; } = 600;
    public bool ScaleCoordinatesOnResize { get; set; } = true;

    public bool EnableClickLimit { get; set; } = false;
    public long MaxClickCount { get; set; } = 1000;

    public int StartDelayMs { get; set; } = 2000;

    public bool AutoResumeWhenTargetRefocused { get; set; } = true;
    public bool RestoreCursorAfterForegroundClick { get; set; } = false;

    public bool UseSendMessageTimeoutInBackground { get; set; } = false;
    public bool BroadcastToChildWindowsInBackground { get; set; } = true;

    public AutomationConfig Clone()
    {
        return (AutomationConfig)MemberwiseClone();
    }
}

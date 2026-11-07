using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RobloxMacro.Core.Models;

public enum MacroEventType
{
    LeftMouseClick,
    RightMouseClick,
    MiddleMouseClick,
    MouseDown,
    MouseUp,
    KeyDown,
    KeyUp
}

public enum MacroMouseButton
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 3
}

public sealed class MacroEvent
{
    public int Index { get; set; }
    public MacroEventType EventType { get; set; } = MacroEventType.LeftMouseClick;
    public MacroMouseButton MouseButton { get; set; } = MacroMouseButton.Left;

    /// <summary>
    /// Time elapsed (in milliseconds) after the previous event before this event is executed.
    /// </summary>
    public int DelayBeforeMs { get; set; } = 50;

    /// <summary>
    /// Hold duration (in milliseconds) between button-down and button-up for click events.
    /// </summary>
    public int HoldDurationMs { get; set; } = 35;

    /// <summary>
    /// Client-relative X coordinate inside the target window when recorded.
    /// </summary>
    public int ClientX { get; set; }

    /// <summary>
    /// Client-relative Y coordinate inside the target window when recorded.
    /// </summary>
    public int ClientY { get; set; }

    /// <summary>
    /// Reference client width of the target window when this coordinate was recorded.
    /// Used to keep positions accurate when the window resizes.
    /// </summary>
    public int ReferenceClientWidth { get; set; } = 800;

    /// <summary>
    /// Reference client height of the target window when this coordinate was recorded.
    /// </summary>
    public int ReferenceClientHeight { get; set; } = 600;

    /// <summary>
    /// Win32 Virtual-Key code for keyboard events (e.g., 0x20 for Space, 0x31 for '1').
    /// </summary>
    public ushort VirtualKeyCode { get; set; }

    public string KeyName { get; set; } = string.Empty;

    [JsonIgnore]
    public string Description => EventType switch
    {
        MacroEventType.LeftMouseClick => $"Left Click @ ({ClientX}, {ClientY}) [Hold: {HoldDurationMs}ms, Wait: {DelayBeforeMs}ms]",
        MacroEventType.RightMouseClick => $"Right Click @ ({ClientX}, {ClientY}) [Hold: {HoldDurationMs}ms, Wait: {DelayBeforeMs}ms]",
        MacroEventType.MiddleMouseClick => $"Middle Click @ ({ClientX}, {ClientY}) [Hold: {HoldDurationMs}ms, Wait: {DelayBeforeMs}ms]",
        MacroEventType.MouseDown => $"{MouseButton} Down @ ({ClientX}, {ClientY}) [Wait: {DelayBeforeMs}ms]",
        MacroEventType.MouseUp => $"{MouseButton} Up @ ({ClientX}, {ClientY}) [Wait: {DelayBeforeMs}ms]",
        MacroEventType.KeyDown => $"Key Down [{KeyName} (0x{VirtualKeyCode:X2})] [Wait: {DelayBeforeMs}ms]",
        MacroEventType.KeyUp => $"Key Up [{KeyName} (0x{VirtualKeyCode:X2})] [Wait: {DelayBeforeMs}ms]",
        _ => "Unknown Event"
    };
}

public sealed class MacroProfile
{
    public string Name { get; set; } = "Default Attack Sequence";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public bool RecordKeyboardEvents { get; set; } = true;
    public bool ScaleCoordinatesOnResize { get; set; } = true;
    public bool LoopIndefinitely { get; set; } = true;
    public int LoopCount { get; set; } = 1;
    public List<MacroEvent> Events { get; set; } = new();
}

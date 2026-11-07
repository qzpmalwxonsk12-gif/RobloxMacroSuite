using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RobloxMacro.Core.Models;
using RobloxMacro.Core.Native;

namespace RobloxMacro.Core.Services;

/// <summary>
/// Captures mouse clicks (coordinates, intervals, hold durations) and optional keyboard inputs
/// relative to the selected target window, and persists MacroProfile objects to/from JSON.
/// </summary>
public sealed class MacroRecorderService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly TargetWindowService _windowService;
    private CancellationTokenSource? _recordCts;
    private Task? _recordTask;
    private readonly List<MacroEvent> _recordedEvents = new();
    private readonly Stopwatch _eventClock = new();

    public bool IsRecording { get; private set; }
    public IReadOnlyList<MacroEvent> RecordedEvents => _recordedEvents;

    public event Action<MacroEvent>? EventRecorded;
    public event Action<string>? LogMessage;

    public MacroRecorderService(TargetWindowService windowService)
    {
        _windowService = windowService ?? throw new ArgumentNullException(nameof(windowService));
    }

    public void StartRecording(
        TargetWindowInfo? targetWindow,
        bool captureKeyboard,
        HashSet<ushort>? ignoredHotkeys = null)
    {
        StopRecording();

        _recordedEvents.Clear();
        _eventClock.Restart();
        IsRecording = true;

        var cts = new CancellationTokenSource();
        _recordCts = cts;
        _recordTask = Task.Run(() => RecordLoopAsync(targetWindow, captureKeyboard, ignoredHotkeys ?? [], cts.Token), cts.Token);

        LogMessage?.Invoke($"{DateTime.Now:HH:mm:ss.fff} [Macro Recorder] Recording started (CaptureKeyboard={captureKeyboard}).");
    }

    public MacroProfile StopRecording(string profileName = "Recorded Attack Macro")
    {
        if (_recordCts != null)
        {
            try
            {
                _recordCts.Cancel();
                _recordCts.Dispose();
            }
            catch
            {
                // Ignore
            }
            _recordCts = null;
        }

        _eventClock.Stop();
        if (IsRecording)
        {
            IsRecording = false;
            LogMessage?.Invoke($"{DateTime.Now:HH:mm:ss.fff} [Macro Recorder] Recording stopped ({_recordedEvents.Count} events captured).");
        }

        return BuildProfile(profileName);
    }

    public void ClearEvents()
    {
        _recordedEvents.Clear();
    }

    /// <summary>
    /// Allows adding a mouse click or keyboard event directly (used by manual builder and automated tests).
    /// </summary>
    public MacroEvent AddManualClickEvent(
        int clientX,
        int clientY,
        int delayBeforeMs,
        int holdDurationMs,
        int refWidth = 800,
        int refHeight = 600,
        MacroMouseButton button = MacroMouseButton.Left)
    {
        MacroEventType type = button switch
        {
            MacroMouseButton.Right => MacroEventType.RightMouseClick,
            MacroMouseButton.Middle => MacroEventType.MiddleMouseClick,
            _ => MacroEventType.LeftMouseClick
        };

        var evt = new MacroEvent
        {
            Index = _recordedEvents.Count + 1,
            EventType = type,
            MouseButton = button,
            ClientX = clientX,
            ClientY = clientY,
            DelayBeforeMs = Math.Max(0, delayBeforeMs),
            HoldDurationMs = Math.Max(5, holdDurationMs),
            ReferenceClientWidth = Math.Max(1, refWidth),
            ReferenceClientHeight = Math.Max(1, refHeight)
        };

        _recordedEvents.Add(evt);
        EventRecorded?.Invoke(evt);
        return evt;
    }

    public MacroEvent AddManualKeyEvent(
        ushort virtualKey,
        string keyName,
        bool isKeyDown,
        int delayBeforeMs)
    {
        var evt = new MacroEvent
        {
            Index = _recordedEvents.Count + 1,
            EventType = isKeyDown ? MacroEventType.KeyDown : MacroEventType.KeyUp,
            VirtualKeyCode = virtualKey,
            KeyName = keyName,
            DelayBeforeMs = Math.Max(0, delayBeforeMs)
        };

        _recordedEvents.Add(evt);
        EventRecorded?.Invoke(evt);
        return evt;
    }

    public MacroProfile BuildProfile(string name = "Recorded Attack Macro", bool scaleOnResize = true, bool loopIndefinitely = true, int loopCount = 1)
    {
        var profile = new MacroProfile
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Recorded Attack Macro" : name,
            CreatedUtc = DateTime.UtcNow,
            ScaleCoordinatesOnResize = scaleOnResize,
            LoopIndefinitely = loopIndefinitely,
            LoopCount = Math.Max(1, loopCount),
            Events = new List<MacroEvent>(_recordedEvents)
        };
        return profile;
    }

    public void LoadProfileIntoRecorder(MacroProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        StopRecording();
        _recordedEvents.Clear();
        int idx = 1;
        foreach (var e in profile.Events)
        {
            e.Index = idx++;
            _recordedEvents.Add(e);
        }
    }

    public static string SerializeProfile(MacroProfile profile)
    {
        return JsonSerializer.Serialize(profile, JsonOptions);
    }

    public static MacroProfile DeserializeProfile(string json)
    {
        MacroProfile? profile = JsonSerializer.Deserialize<MacroProfile>(json, JsonOptions);
        return profile ?? new MacroProfile();
    }

    public static void SaveProfileToFile(MacroProfile profile, string filePath)
    {
        string json = SerializeProfile(profile);
        File.WriteAllText(filePath, json);
    }

    public static MacroProfile LoadProfileFromFile(string filePath)
    {
        string json = File.ReadAllText(filePath);
        return DeserializeProfile(json);
    }

    private async Task RecordLoopAsync(
        TargetWindowInfo? targetWindow,
        bool captureKeyboard,
        HashSet<ushort> ignoredHotkeys,
        CancellationToken token)
    {
        const int VK_LBUTTON = 0x01;
        const int VK_RBUTTON = 0x02;
        const int VK_MBUTTON = 0x04;

        bool wasLeftDown = (Win32Native.GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        bool wasRightDown = (Win32Native.GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;
        bool wasMiddleDown = (Win32Native.GetAsyncKeyState(VK_MBUTTON) & 0x8000) != 0;

        long leftDownTimestampMs = 0;
        int leftDownDelayBeforeMs = 0;
        (int X, int Y, int RefW, int RefH) leftDownCoords = (0, 0, 800, 600);

        long rightDownTimestampMs = 0;
        int rightDownDelayBeforeMs = 0;
        (int X, int Y, int RefW, int RefH) rightDownCoords = (0, 0, 800, 600);

        long middleDownTimestampMs = 0;
        int middleDownDelayBeforeMs = 0;
        (int X, int Y, int RefW, int RefH) middleDownCoords = (0, 0, 800, 600);

        var keyStates = new bool[256];
        for (int vk = 0x08; vk <= 0xFE; vk++)
        {
            keyStates[vk] = (Win32Native.GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        long lastEventFinishMs = _eventClock.ElapsedMilliseconds;

        while (!token.IsCancellationRequested)
        {
            long nowMs = _eventClock.ElapsedMilliseconds;

            bool isLeftDown = (Win32Native.GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
            bool isRightDown = (Win32Native.GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;
            bool isMiddleDown = (Win32Native.GetAsyncKeyState(VK_MBUTTON) & 0x8000) != 0;

            // Left Mouse Button transition
            if (isLeftDown && !wasLeftDown)
            {
                leftDownTimestampMs = nowMs;
                leftDownDelayBeforeMs = (int)Math.Max(0, nowMs - lastEventFinishMs);
                leftDownCoords = CaptureCurrentClientCoordinates(targetWindow);
            }
            else if (!isLeftDown && wasLeftDown && leftDownTimestampMs > 0)
            {
                int holdMs = (int)Math.Max(10, nowMs - leftDownTimestampMs);
                lastEventFinishMs = nowMs;
                AddManualClickEvent(
                    leftDownCoords.X,
                    leftDownCoords.Y,
                    leftDownDelayBeforeMs,
                    holdMs,
                    leftDownCoords.RefW,
                    leftDownCoords.RefH,
                    MacroMouseButton.Left);
            }
            wasLeftDown = isLeftDown;

            // Right Mouse Button transition
            if (isRightDown && !wasRightDown)
            {
                rightDownTimestampMs = nowMs;
                rightDownDelayBeforeMs = (int)Math.Max(0, nowMs - lastEventFinishMs);
                rightDownCoords = CaptureCurrentClientCoordinates(targetWindow);
            }
            else if (!isRightDown && wasRightDown && rightDownTimestampMs > 0)
            {
                int holdMs = (int)Math.Max(10, nowMs - rightDownTimestampMs);
                lastEventFinishMs = nowMs;
                AddManualClickEvent(
                    rightDownCoords.X,
                    rightDownCoords.Y,
                    rightDownDelayBeforeMs,
                    holdMs,
                    rightDownCoords.RefW,
                    rightDownCoords.RefH,
                    MacroMouseButton.Right);
            }
            wasRightDown = isRightDown;

            // Middle Mouse Button transition
            if (isMiddleDown && !wasMiddleDown)
            {
                middleDownTimestampMs = nowMs;
                middleDownDelayBeforeMs = (int)Math.Max(0, nowMs - lastEventFinishMs);
                middleDownCoords = CaptureCurrentClientCoordinates(targetWindow);
            }
            else if (!isMiddleDown && wasMiddleDown && middleDownTimestampMs > 0)
            {
                int holdMs = (int)Math.Max(10, nowMs - middleDownTimestampMs);
                lastEventFinishMs = nowMs;
                AddManualClickEvent(
                    middleDownCoords.X,
                    middleDownCoords.Y,
                    middleDownDelayBeforeMs,
                    holdMs,
                    middleDownCoords.RefW,
                    middleDownCoords.RefH,
                    MacroMouseButton.Middle);
            }
            wasMiddleDown = isMiddleDown;

            // Optional Keyboard capture
            if (captureKeyboard)
            {
                for (int vk = 0x08; vk <= 0x9F; vk++)
                {
                    if (ignoredHotkeys.Contains((ushort)vk))
                        continue;

                    // Skip generic shift/ctrl/alt duplicates (0x10-0x12) in favor of specific or vice versa
                    if (vk is >= 0xA0 and <= 0xA5)
                        continue;

                    bool isDown = (Win32Native.GetAsyncKeyState(vk) & 0x8000) != 0;
                    if (isDown != keyStates[vk])
                    {
                        int delayBefore = (int)Math.Max(0, nowMs - lastEventFinishMs);
                        lastEventFinishMs = nowMs;
                        keyStates[vk] = isDown;
                        AddManualKeyEvent((ushort)vk, GetReadableKeyName((ushort)vk), isDown, delayBefore);
                    }
                }
            }

            await Task.Delay(10, token);
        }
    }

    private (int X, int Y, int RefW, int RefH) CaptureCurrentClientCoordinates(TargetWindowInfo? target)
    {
        Win32Native.GetCursorPos(out Win32Native.POINT pt);

        if (target != null && target.Handle != IntPtr.Zero && Win32Native.IsWindow(target.Handle))
        {
            _windowService.RefreshTargetStatus(target);
            Win32Native.POINT clientPt = pt;
            Win32Native.ScreenToClient(target.Handle, ref clientPt);
            int refW = target.ClientWidth > 0 ? target.ClientWidth : 800;
            int refH = target.ClientHeight > 0 ? target.ClientHeight : 600;
            return (clientPt.X, clientPt.Y, refW, refH);
        }

        int screenW = Math.Max(800, Win32Native.GetSystemMetrics(Win32Native.SM_CXSCREEN));
        int screenH = Math.Max(600, Win32Native.GetSystemMetrics(Win32Native.SM_CYSCREEN));
        return (pt.X, pt.Y, screenW, screenH);
    }

    public static string GetReadableKeyName(ushort vk) => vk switch
    {
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x10 => "Shift",
        0x11 => "Ctrl",
        0x12 => "Alt",
        0x1B => "Escape",
        0x20 => "Space",
        0x25 => "LeftArrow",
        0x26 => "UpArrow",
        0x27 => "RightArrow",
        0x28 => "DownArrow",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        _ => $"VK_0x{vk:X2}"
    };

    public void Dispose()
    {
        StopRecording();
    }
}

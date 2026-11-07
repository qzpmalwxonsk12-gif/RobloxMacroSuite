using System;
using System.Collections.Generic;
using RobloxMacro.Core.Native;

namespace RobloxMacro.Core.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    public const int HOTKEY_ID_START_STOP = 9001;
    public const int HOTKEY_ID_EMERGENCY_STOP = 9002;
    public const int HOTKEY_ID_PICK_POSITION = 9003;

    private IntPtr _registeredHwnd;
    private readonly HashSet<int> _registeredIds = new();

    public ushort StartStopVirtualKey { get; private set; } = 0x75; // F6
    public ushort EmergencyStopVirtualKey { get; private set; } = 0x76; // F7
    public ushort PickPositionVirtualKey { get; private set; } = 0x77; // F8

    public event Action? StartStopTriggered;
    public event Action? EmergencyStopTriggered;
    public event Action? PickPositionTriggered;
    public event Action<string>? LogMessage;

    public bool BindToWindow(IntPtr windowHandle, ushort startStopVk = 0x75, ushort emergencyStopVk = 0x76, ushort pickPosVk = 0x77)
    {
        UnregisterAll();

        _registeredHwnd = windowHandle;
        StartStopVirtualKey = startStopVk;
        EmergencyStopVirtualKey = emergencyStopVk;
        PickPositionVirtualKey = pickPosVk;

        if (windowHandle == IntPtr.Zero)
            return false;

        bool ok1 = RegisterSingle(HOTKEY_ID_START_STOP, Win32Native.MOD_NOREPEAT, startStopVk);
        bool ok2 = RegisterSingle(HOTKEY_ID_EMERGENCY_STOP, Win32Native.MOD_NOREPEAT, emergencyStopVk);
        bool ok3 = RegisterSingle(HOTKEY_ID_PICK_POSITION, Win32Native.MOD_NOREPEAT, pickPosVk);

        string s1Name = MacroRecorderService.GetReadableKeyName(startStopVk);
        string s2Name = MacroRecorderService.GetReadableKeyName(emergencyStopVk);
        string s3Name = MacroRecorderService.GetReadableKeyName(pickPosVk);

        LogMessage?.Invoke(
            $"{DateTime.Now:HH:mm:ss.fff} [Hotkeys] Registered Global Hotkeys: Start/Stop=[{s1Name}] ({(ok1 ? "OK" : "Busy")}), " +
            $"Emergency Stop=[{s2Name}] ({(ok2 ? "OK" : "Busy")}), Pick Position=[{s3Name}] ({(ok3 ? "OK" : "Busy")}).");

        return ok1 && ok2;
    }

    public bool ProcessWndProcMessage(int msg, nint wParam)
    {
        if (msg != Win32Native.WM_HOTKEY)
            return false;

        int id = (int)wParam;
        switch (id)
        {
            case HOTKEY_ID_START_STOP:
                StartStopTriggered?.Invoke();
                return true;
            case HOTKEY_ID_EMERGENCY_STOP:
                EmergencyStopTriggered?.Invoke();
                return true;
            case HOTKEY_ID_PICK_POSITION:
                PickPositionTriggered?.Invoke();
                return true;
            default:
                return false;
        }
    }

    public HashSet<ushort> GetActiveHotkeyVirtualKeys()
    {
        return [StartStopVirtualKey, EmergencyStopVirtualKey, PickPositionVirtualKey];
    }

    private bool RegisterSingle(int id, uint modifiers, ushort vk)
    {
        if (_registeredHwnd == IntPtr.Zero || vk == 0)
            return false;

        bool ok = Win32Native.RegisterHotKey(_registeredHwnd, id, modifiers, vk);
        if (ok)
        {
            _registeredIds.Add(id);
        }
        return ok;
    }

    public void UnregisterAll()
    {
        if (_registeredHwnd != IntPtr.Zero)
        {
            foreach (int id in _registeredIds)
            {
                Win32Native.UnregisterHotKey(_registeredHwnd, id);
            }
        }
        _registeredIds.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
    }
}

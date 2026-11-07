using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using RobloxMacro.Core.Models;
using RobloxMacro.Core.Native;

namespace RobloxMacro.Core.Services;

public enum InputDispatchStatus
{
    DispatchedSuccess,
    BlockedByFocusGuard,
    TargetWindowClosedOrInvalid,
    Win32ApiError
}

public readonly record struct InputDispatchResult(
    InputDispatchStatus Status,
    int Win32ErrorCode = 0,
    string Detail = "");

/// <summary>
/// Thread-safe low-level input dispatcher supporting both:
/// 1. Foreground-Guarded SendInput (guaranteed to only fire when target window is active foreground)
/// 2. Background Win32 Message delivery (PostMessageW / SendMessageTimeoutW to target HWND)
/// Also tracks all simulated held inputs (mouse buttons and keys) to guarantee release on stop/pause.
/// </summary>
public sealed class InputDispatcher : IDisposable
{
    private readonly object _stateLock = new();
    private bool _foregroundLeftHeld;
    private bool _foregroundRightHeld;
    private bool _foregroundMiddleHeld;
    private IntPtr _backgroundHeldHwnd;
    private int _backgroundHeldClientX;
    private int _backgroundHeldClientY;
    private bool _backgroundLeftHeld;
    private readonly HashSet<ushort> _foregroundHeldKeys = new();
    private readonly HashSet<(IntPtr Hwnd, ushort Vk)> _backgroundHeldKeys = new();

    /// <summary>
    /// Verifies that the target window exists, is not minimized, and is currently the OS foreground window.
    /// </summary>
    public static bool IsTargetSafeForForegroundInput(IntPtr targetHwnd)
    {
        if (targetHwnd == IntPtr.Zero || !Win32Native.IsWindow(targetHwnd))
            return false;

        if (Win32Native.IsIconic(targetHwnd) || !Win32Native.IsWindowVisible(targetHwnd))
            return false;

        return Win32Native.GetForegroundWindow() == targetHwnd;
    }

    public InputDispatchResult SendMouseButtonDown(
        TargetWindowInfo target,
        InputDeliveryMode deliveryMode,
        MacroMouseButton button,
        int clientX,
        int clientY,
        int screenX,
        int screenY,
        bool moveCursorInForeground,
        bool useSendMessageTimeout = false,
        bool broadcastToChildren = true)
    {
        if (target.Handle == IntPtr.Zero || !Win32Native.IsWindow(target.Handle))
        {
            return new InputDispatchResult(InputDispatchStatus.TargetWindowClosedOrInvalid, 0, "Target window handle is invalid or closed.");
        }

        if (deliveryMode == InputDeliveryMode.ForegroundGuardedSendInput)
        {
            if (!IsTargetSafeForForegroundInput(target.Handle))
            {
                return new InputDispatchResult(InputDispatchStatus.BlockedByFocusGuard, 0, "Target window does not have foreground focus.");
            }

            if (moveCursorInForeground)
            {
                Win32Native.SetCursorPos(screenX, screenY);
            }

            uint downFlag = button switch
            {
                MacroMouseButton.Right => Win32Native.MOUSEEVENTF_RIGHTDOWN,
                MacroMouseButton.Middle => Win32Native.MOUSEEVENTF_MIDDLEDOWN,
                _ => Win32Native.MOUSEEVENTF_LEFTDOWN
            };

            var input = new Win32Native.INPUT
            {
                Type = Win32Native.INPUT_MOUSE,
                U = new Win32Native.InputUnion
                {
                    mi = new Win32Native.MOUSEINPUT
                    {
                        dwFlags = downFlag
                    }
                }
            };

            uint sent = Win32Native.SendInput(1, [input], Win32Native.INPUT.Size);
            if (sent == 0)
            {
                int err = Marshal.GetLastWin32Error();
                return new InputDispatchResult(InputDispatchStatus.Win32ApiError, err, $"SendInput failed with Win32 error {err}.");
            }

            lock (_stateLock)
            {
                if (button == MacroMouseButton.Left) _foregroundLeftHeld = true;
                else if (button == MacroMouseButton.Right) _foregroundRightHeld = true;
                else if (button == MacroMouseButton.Middle) _foregroundMiddleHeld = true;
            }

            return new InputDispatchResult(InputDispatchStatus.DispatchedSuccess);
        }
        else
        {
            // Background Win32 Message Mode (PostMessage / SendMessageTimeout)
            nint lParam = Win32Native.MakeLParam(clientX, clientY);
            (uint downMsg, nint wParam) = button switch
            {
                MacroMouseButton.Right => (Win32Native.WM_RBUTTONDOWN, Win32Native.MK_RBUTTON),
                MacroMouseButton.Middle => (Win32Native.WM_MBUTTONDOWN, Win32Native.MK_MBUTTON),
                _ => (Win32Native.WM_LBUTTONDOWN, Win32Native.MK_LBUTTON)
            };

            DispatchWin32WindowMessage(target.Handle, Win32Native.WM_MOUSEMOVE, Win32Native.MK_NONE, lParam, useSendMessageTimeout, broadcastToChildren);
            bool ok = DispatchWin32WindowMessage(target.Handle, downMsg, wParam, lParam, useSendMessageTimeout, broadcastToChildren);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                return new InputDispatchResult(InputDispatchStatus.Win32ApiError, err, $"Win32 message dispatch failed (Error {err}).");
            }

            lock (_stateLock)
            {
                if (button == MacroMouseButton.Left)
                {
                    _backgroundLeftHeld = true;
                    _backgroundHeldHwnd = target.Handle;
                    _backgroundHeldClientX = clientX;
                    _backgroundHeldClientY = clientY;
                }
            }

            return new InputDispatchResult(InputDispatchStatus.DispatchedSuccess);
        }
    }

    public InputDispatchResult SendMouseButtonUp(
        TargetWindowInfo target,
        InputDeliveryMode deliveryMode,
        MacroMouseButton button,
        int clientX,
        int clientY,
        bool useSendMessageTimeout = false,
        bool broadcastToChildren = true)
    {
        if (deliveryMode == InputDeliveryMode.ForegroundGuardedSendInput)
        {
            uint upFlag = button switch
            {
                MacroMouseButton.Right => Win32Native.MOUSEEVENTF_RIGHTUP,
                MacroMouseButton.Middle => Win32Native.MOUSEEVENTF_MIDDLEUP,
                _ => Win32Native.MOUSEEVENTF_LEFTUP
            };

            var input = new Win32Native.INPUT
            {
                Type = Win32Native.INPUT_MOUSE,
                U = new Win32Native.InputUnion
                {
                    mi = new Win32Native.MOUSEINPUT
                    {
                        dwFlags = upFlag
                    }
                }
            };

            uint sent = Win32Native.SendInput(1, [input], Win32Native.INPUT.Size);
            lock (_stateLock)
            {
                if (button == MacroMouseButton.Left) _foregroundLeftHeld = false;
                else if (button == MacroMouseButton.Right) _foregroundRightHeld = false;
                else if (button == MacroMouseButton.Middle) _foregroundMiddleHeld = false;
            }

            if (sent == 0)
            {
                int err = Marshal.GetLastWin32Error();
                return new InputDispatchResult(InputDispatchStatus.Win32ApiError, err, $"SendInput (MouseUp) failed with error {err}.");
            }

            return new InputDispatchResult(InputDispatchStatus.DispatchedSuccess);
        }
        else
        {
            if (target.Handle == IntPtr.Zero || !Win32Native.IsWindow(target.Handle))
            {
                lock (_stateLock)
                {
                    _backgroundLeftHeld = false;
                }
                return new InputDispatchResult(InputDispatchStatus.TargetWindowClosedOrInvalid);
            }

            nint lParam = Win32Native.MakeLParam(clientX, clientY);
            uint upMsg = button switch
            {
                MacroMouseButton.Right => Win32Native.WM_RBUTTONUP,
                MacroMouseButton.Middle => Win32Native.WM_MBUTTONUP,
                _ => Win32Native.WM_LBUTTONUP
            };

            bool ok = DispatchWin32WindowMessage(target.Handle, upMsg, Win32Native.MK_NONE, lParam, useSendMessageTimeout, broadcastToChildren);
            lock (_stateLock)
            {
                if (button == MacroMouseButton.Left)
                {
                    _backgroundLeftHeld = false;
                }
            }

            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                return new InputDispatchResult(InputDispatchStatus.Win32ApiError, err, $"Win32 MouseUp failed (Error {err}).");
            }

            return new InputDispatchResult(InputDispatchStatus.DispatchedSuccess);
        }
    }

    public InputDispatchResult SendKeyEvent(
        TargetWindowInfo target,
        InputDeliveryMode deliveryMode,
        ushort virtualKey,
        bool isKeyDown,
        bool useSendMessageTimeout = false)
    {
        if (target.Handle == IntPtr.Zero || !Win32Native.IsWindow(target.Handle))
        {
            return new InputDispatchResult(InputDispatchStatus.TargetWindowClosedOrInvalid);
        }

        if (deliveryMode == InputDeliveryMode.ForegroundGuardedSendInput)
        {
            if (isKeyDown && !IsTargetSafeForForegroundInput(target.Handle))
            {
                return new InputDispatchResult(InputDispatchStatus.BlockedByFocusGuard, 0, "Target window is not focused.");
            }

            ushort scan = (ushort)Win32Native.MapVirtualKeyW(virtualKey, 0);
            var input = new Win32Native.INPUT
            {
                Type = Win32Native.INPUT_KEYBOARD,
                U = new Win32Native.InputUnion
                {
                    ki = new Win32Native.KEYBDINPUT
                    {
                        wVk = virtualKey,
                        wScan = scan,
                        dwFlags = isKeyDown ? 0u : Win32Native.KEYEVENTF_KEYUP
                    }
                }
            };

            uint sent = Win32Native.SendInput(1, [input], Win32Native.INPUT.Size);
            lock (_stateLock)
            {
                if (isKeyDown) _foregroundHeldKeys.Add(virtualKey);
                else _foregroundHeldKeys.Remove(virtualKey);
            }

            return sent > 0
                ? new InputDispatchResult(InputDispatchStatus.DispatchedSuccess)
                : new InputDispatchResult(InputDispatchStatus.Win32ApiError, Marshal.GetLastWin32Error());
        }
        else
        {
            uint scan = Win32Native.MapVirtualKeyW(virtualKey, 0);
            uint msg = isKeyDown ? Win32Native.WM_KEYDOWN : Win32Native.WM_KEYUP;
            uint lParamBits = 1u | (scan << 16);
            if (!isKeyDown)
            {
                lParamBits |= (1u << 30) | (1u << 31);
            }

            bool ok = DispatchWin32WindowMessage(target.Handle, msg, (nint)virtualKey, (nint)lParamBits, useSendMessageTimeout, broadcastToChildren: false);
            lock (_stateLock)
            {
                if (isKeyDown) _backgroundHeldKeys.Add((target.Handle, virtualKey));
                else _backgroundHeldKeys.Remove((target.Handle, virtualKey));
            }

            return ok
                ? new InputDispatchResult(InputDispatchStatus.DispatchedSuccess)
                : new InputDispatchResult(InputDispatchStatus.Win32ApiError, Marshal.GetLastWin32Error());
        }
    }

    /// <summary>
    /// Releases all currently held mouse buttons and keyboard keys across both foreground and background modes.
    /// Guaranteed to be safe to call at any time (stop, pause, focus loss, crash recovery, or dispose).
    /// </summary>
    public void ReleaseAllHeldInputs()
    {
        bool releaseFgLeft;
        bool releaseFgRight;
        bool releaseFgMiddle;
        bool releaseBgLeft;
        IntPtr bgHwnd;
        int bgX;
        int bgY;
        ushort[] fgKeys;
        (IntPtr Hwnd, ushort Vk)[] bgKeys;

        lock (_stateLock)
        {
            releaseFgLeft = _foregroundLeftHeld;
            releaseFgRight = _foregroundRightHeld;
            releaseFgMiddle = _foregroundMiddleHeld;
            _foregroundLeftHeld = false;
            _foregroundRightHeld = false;
            _foregroundMiddleHeld = false;

            releaseBgLeft = _backgroundLeftHeld;
            bgHwnd = _backgroundHeldHwnd;
            bgX = _backgroundHeldClientX;
            bgY = _backgroundHeldClientY;
            _backgroundLeftHeld = false;

            fgKeys = [.. _foregroundHeldKeys];
            _foregroundHeldKeys.Clear();

            bgKeys = [.. _backgroundHeldKeys];
            _backgroundHeldKeys.Clear();
        }

        if (releaseFgLeft)
        {
            SendForegroundMouseUpRaw(Win32Native.MOUSEEVENTF_LEFTUP);
        }
        if (releaseFgRight)
        {
            SendForegroundMouseUpRaw(Win32Native.MOUSEEVENTF_RIGHTUP);
        }
        if (releaseFgMiddle)
        {
            SendForegroundMouseUpRaw(Win32Native.MOUSEEVENTF_MIDDLEUP);
        }

        foreach (ushort vk in fgKeys)
        {
            var input = new Win32Native.INPUT
            {
                Type = Win32Native.INPUT_KEYBOARD,
                U = new Win32Native.InputUnion
                {
                    ki = new Win32Native.KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = (ushort)Win32Native.MapVirtualKeyW(vk, 0),
                        dwFlags = Win32Native.KEYEVENTF_KEYUP
                    }
                }
            };
            Win32Native.SendInput(1, [input], Win32Native.INPUT.Size);
        }

        if (releaseBgLeft && bgHwnd != IntPtr.Zero && Win32Native.IsWindow(bgHwnd))
        {
            nint lParam = Win32Native.MakeLParam(bgX, bgY);
            Win32Native.PostMessageW(bgHwnd, Win32Native.WM_LBUTTONUP, Win32Native.MK_NONE, lParam);
        }

        foreach (var (hwnd, vk) in bgKeys)
        {
            if (hwnd != IntPtr.Zero && Win32Native.IsWindow(hwnd))
            {
                uint scan = Win32Native.MapVirtualKeyW(vk, 0);
                uint lParamBits = 1u | (scan << 16) | (1u << 30) | (1u << 31);
                Win32Native.PostMessageW(hwnd, Win32Native.WM_KEYUP, (nint)vk, (nint)lParamBits);
            }
        }
    }

    private static void SendForegroundMouseUpRaw(uint mouseUpFlag)
    {
        var input = new Win32Native.INPUT
        {
            Type = Win32Native.INPUT_MOUSE,
            U = new Win32Native.InputUnion
            {
                mi = new Win32Native.MOUSEINPUT
                {
                    dwFlags = mouseUpFlag
                }
            }
        };
        Win32Native.SendInput(1, [input], Win32Native.INPUT.Size);
    }

    private static bool DispatchWin32WindowMessage(
        IntPtr targetHwnd,
        uint msg,
        nint wParam,
        nint lParam,
        bool useSendMessageTimeout,
        bool broadcastToChildren)
    {
        bool rootOk = SendSingleMessage(targetHwnd, msg, wParam, lParam, useSendMessageTimeout);

        if (broadcastToChildren)
        {
            Win32Native.EnumChildWindows(targetHwnd, (childHwnd, _) =>
            {
                if (childHwnd != IntPtr.Zero && Win32Native.IsWindowVisible(childHwnd))
                {
                    SendSingleMessage(childHwnd, msg, wParam, lParam, useSendMessageTimeout);
                }
                return true;
            }, IntPtr.Zero);
        }

        return rootOk;
    }

    private static bool SendSingleMessage(IntPtr hWnd, uint msg, nint wParam, nint lParam, bool useSendMessageTimeout)
    {
        if (useSendMessageTimeout)
        {
            IntPtr res = Win32Native.SendMessageTimeoutW(
                hWnd,
                msg,
                wParam,
                lParam,
                Win32Native.SMTO_ABORTIFHUNG | Win32Native.SMTO_NORMAL,
                150,
                out _);
            return res != IntPtr.Zero;
        }

        return Win32Native.PostMessageW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        ReleaseAllHeldInputs();
    }
}

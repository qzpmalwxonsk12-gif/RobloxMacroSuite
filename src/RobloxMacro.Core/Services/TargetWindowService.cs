using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using RobloxMacro.Core.Models;
using RobloxMacro.Core.Native;

namespace RobloxMacro.Core.Services;

/// <summary>
/// Discovers Roblox and desktop target windows, monitors their focus/minimized/closed states,
/// and resolves client-relative and screen coordinates across window moves, resizes, and DPI scaling.
/// </summary>
public sealed class TargetWindowService
{
    private static readonly string[] KnownRobloxProcessNames =
    [
        "RobloxPlayerBeta",
        "Windows10Universal",
        "RobloxStudioBeta"
    ];

    public static bool IsRobloxProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        string clean = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;

        return KnownRobloxProcessNames.Any(r => r.Equals(clean, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Scans the system for Roblox windows first, optionally including all visible top-level windows.
    /// Also includes running Roblox processes even if their window handle is not yet visible in the current desktop.
    /// </summary>
    public List<TargetWindowInfo> DiscoverTargetWindows(bool includeAllDesktopWindows = false, IntPtr excludeWindowHandle = default)
    {
        var results = new List<TargetWindowInfo>();
        var seenHandles = new HashSet<IntPtr>();
        var seenRobloxPids = new HashSet<uint>();
        uint currentPid = (uint)Environment.ProcessId;

        var processNameCache = new Dictionary<uint, string>();

        Win32Native.EnumWindows((hWnd, _) =>
        {
            if (hWnd == IntPtr.Zero || hWnd == excludeWindowHandle)
                return true;

            if (!Win32Native.IsWindowVisible(hWnd) && !Win32Native.IsIconic(hWnd))
                return true;

            Win32Native.GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0 || pid == currentPid)
                return true;

            string title = Win32Native.GetWindowTitle(hWnd);
            string className = Win32Native.GetWindowClassName(hWnd);

            if (!processNameCache.TryGetValue(pid, out string? procName))
            {
                try
                {
                    using Process p = Process.GetProcessById((int)pid);
                    procName = p.ProcessName;
                }
                catch
                {
                    procName = "Unknown";
                }
                processNameCache[pid] = procName;
            }

            bool isRoblox = IsRobloxProcessName(procName) ||
                            className.Equals("WINDOWSCLIENT", StringComparison.OrdinalIgnoreCase) ||
                            title.Equals("Roblox", StringComparison.OrdinalIgnoreCase);

            if (!isRoblox && !includeAllDesktopWindows)
                return true;

            if (!isRoblox && string.IsNullOrWhiteSpace(title))
                return true;

            var info = BuildWindowInfo(hWnd, pid, procName, title, className, isRoblox);
            results.Add(info);
            seenHandles.Add(hWnd);
            if (isRoblox)
            {
                seenRobloxPids.Add(pid);
            }

            return true;
        }, IntPtr.Zero);

        // Also check Process.GetProcessesByName for Roblox processes whose windows weren't enumerated
        foreach (string robloxName in KnownRobloxProcessNames)
        {
            Process[] procs;
            try
            {
                procs = Process.GetProcessesByName(robloxName);
            }
            catch
            {
                continue;
            }

            foreach (Process p in procs)
            {
                using (p)
                {
                    uint pid = (uint)p.Id;
                    if (seenRobloxPids.Contains(pid))
                        continue;

                    IntPtr mainHwnd = IntPtr.Zero;
                    string mainTitle = string.Empty;
                    try
                    {
                        mainHwnd = p.MainWindowHandle;
                        mainTitle = p.MainWindowTitle;
                    }
                    catch
                    {
                        // Ignore access denied on metadata
                    }

                    if (mainHwnd != IntPtr.Zero && !seenHandles.Contains(mainHwnd))
                    {
                        string cls = Win32Native.GetWindowClassName(mainHwnd);
                        results.Add(BuildWindowInfo(mainHwnd, pid, p.ProcessName, mainTitle, cls, true));
                        seenHandles.Add(mainHwnd);
                    }
                    else if (mainHwnd == IntPtr.Zero)
                    {
                        results.Add(new TargetWindowInfo
                        {
                            Handle = IntPtr.Zero,
                            ProcessId = pid,
                            ProcessName = p.ProcessName,
                            WindowTitle = string.IsNullOrWhiteSpace(mainTitle) ? "Roblox (Process Running — No Active Window in Current Session)" : mainTitle,
                            ClassName = "WINDOWSCLIENT",
                            IsRobloxProcess = true,
                            FocusState = WindowFocusState.HiddenOrUnavailable,
                            Dpi = 96
                        });
                    }

                    seenRobloxPids.Add(pid);
                }
            }
        }

        return results
            .OrderByDescending(w => w.IsRobloxProcess)
            .ThenByDescending(w => w.Handle != IntPtr.Zero)
            .ThenBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.WindowTitle, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Refreshes the state, geometry, and DPI of an existing target window.
    /// If the window handle was 0 (or closed) for a known PID, attempts to re-resolve the window handle.
    /// </summary>
    public TargetWindowInfo RefreshTargetStatus(TargetWindowInfo target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target.Handle == IntPtr.Zero && target.ProcessId != 0)
        {
            // Try to see if the process now has a window handle
            try
            {
                using Process p = Process.GetProcessById((int)target.ProcessId);
                if (p.HasExited)
                {
                    target.FocusState = WindowFocusState.Closed;
                    return target;
                }

                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    target.Handle = p.MainWindowHandle;
                }
            }
            catch
            {
                target.FocusState = WindowFocusState.Closed;
                return target;
            }
        }

        if (target.Handle == IntPtr.Zero)
        {
            // Verify if process is still alive
            if (target.ProcessId != 0 && !IsProcessAlive(target.ProcessId))
            {
                target.FocusState = WindowFocusState.Closed;
            }
            else
            {
                target.FocusState = WindowFocusState.HiddenOrUnavailable;
            }
            return target;
        }

        if (!Win32Native.IsWindow(target.Handle))
        {
            target.FocusState = WindowFocusState.Closed;
            target.ClientWidth = 0;
            target.ClientHeight = 0;
            return target;
        }

        string title = Win32Native.GetWindowTitle(target.Handle);
        if (!string.IsNullOrWhiteSpace(title))
        {
            target.WindowTitle = title;
        }

        string className = Win32Native.GetWindowClassName(target.Handle);
        if (!string.IsNullOrWhiteSpace(className))
        {
            target.ClassName = className;
        }

        uint dpi = Win32Native.GetDpiForWindow(target.Handle);
        target.Dpi = dpi > 0 ? dpi : 96u;

        if (Win32Native.IsIconic(target.Handle))
        {
            target.FocusState = WindowFocusState.Minimized;
        }
        else if (!Win32Native.IsWindowVisible(target.Handle))
        {
            target.FocusState = WindowFocusState.HiddenOrUnavailable;
        }
        else
        {
            IntPtr fg = Win32Native.GetForegroundWindow();
            target.FocusState = (fg == target.Handle)
                ? WindowFocusState.FocusedAndVisible
                : WindowFocusState.VisibleUnfocused;
        }

        if (Win32Native.GetClientRect(target.Handle, out Win32Native.RECT clientRect))
        {
            if (clientRect.Width > 0 && clientRect.Height > 0)
            {
                target.ClientWidth = clientRect.Width;
                target.ClientHeight = clientRect.Height;
            }
        }

        var origin = new Win32Native.POINT(0, 0);
        if (Win32Native.ClientToScreen(target.Handle, ref origin))
        {
            target.ScreenClientLeft = origin.X;
            target.ScreenClientTop = origin.Y;
        }

        return target;
    }

    /// <summary>
    /// Inspects the window currently underneath the physical mouse cursor and returns its TargetWindowInfo
    /// plus the cursor's client-relative (X, Y) coordinates inside that window.
    /// </summary>
    public (TargetWindowInfo? Window, int ClientX, int ClientY) InspectWindowUnderCursor()
    {
        if (!Win32Native.GetCursorPos(out Win32Native.POINT screenPt))
            return (null, 0, 0);

        IntPtr hitHwnd = Win32Native.WindowFromPoint(screenPt);
        if (hitHwnd == IntPtr.Zero)
            return (null, 0, 0);

        IntPtr rootHwnd = Win32Native.GetAncestor(hitHwnd, Win32Native.GA_ROOT);
        if (rootHwnd == IntPtr.Zero)
            rootHwnd = hitHwnd;

        Win32Native.GetWindowThreadProcessId(rootHwnd, out uint pid);
        string procName = "Unknown";
        try
        {
            using Process p = Process.GetProcessById((int)pid);
            procName = p.ProcessName;
        }
        catch
        {
            // Ignore
        }

        string title = Win32Native.GetWindowTitle(rootHwnd);
        string cls = Win32Native.GetWindowClassName(rootHwnd);
        bool isRoblox = IsRobloxProcessName(procName) ||
                        cls.Equals("WINDOWSCLIENT", StringComparison.OrdinalIgnoreCase);

        TargetWindowInfo info = BuildWindowInfo(rootHwnd, pid, procName, title, cls, isRoblox);

        Win32Native.POINT clientPt = screenPt;
        Win32Native.ScreenToClient(rootHwnd, ref clientPt);

        return (info, clientPt.X, clientPt.Y);
    }

    /// <summary>
    /// Computes the exact client-relative (X, Y) and screen-space (ScreenX, ScreenY) coordinates
    /// for a click event, automatically compensating for window moves, resizes, and DPI changes.
    /// </summary>
    public static (int ClientX, int ClientY, int ScreenX, int ScreenY) ResolveClickCoordinates(
        TargetWindowInfo target,
        AutomationConfig config)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(config);

        int clientW = target.ClientWidth > 0 ? target.ClientWidth : Math.Max(1, config.ReferenceClientWidth);
        int clientH = target.ClientHeight > 0 ? target.ClientHeight : Math.Max(1, config.ReferenceClientHeight);

        int resolvedClientX;
        int resolvedClientY;

        switch (config.PositionMode)
        {
            case ClickPositionMode.ClientCenter:
                resolvedClientX = clientW / 2;
                resolvedClientY = clientH / 2;
                break;

            case ClickPositionMode.FixedClientPosition:
                if (config.ScaleCoordinatesOnResize &&
                    config.ReferenceClientWidth > 0 &&
                    config.ReferenceClientHeight > 0 &&
                    target.ClientWidth > 0 &&
                    target.ClientHeight > 0)
                {
                    double scaleX = (double)target.ClientWidth / config.ReferenceClientWidth;
                    double scaleY = (double)target.ClientHeight / config.ReferenceClientHeight;
                    resolvedClientX = (int)Math.Round(config.TargetClientX * scaleX);
                    resolvedClientY = (int)Math.Round(config.TargetClientY * scaleY);
                }
                else
                {
                    resolvedClientX = config.TargetClientX;
                    resolvedClientY = config.TargetClientY;
                }
                break;

            case ClickPositionMode.CurrentCursorPosition:
            default:
                if (Win32Native.GetCursorPos(out Win32Native.POINT curScreen))
                {
                    Win32Native.POINT curClient = curScreen;
                    if (target.Handle != IntPtr.Zero && Win32Native.ScreenToClient(target.Handle, ref curClient))
                    {
                        resolvedClientX = Math.Clamp(curClient.X, 1, Math.Max(1, clientW - 2));
                        resolvedClientY = Math.Clamp(curClient.Y, 1, Math.Max(1, clientH - 2));
                        return (resolvedClientX, resolvedClientY, curScreen.X, curScreen.Y);
                    }
                }
                resolvedClientX = clientW / 2;
                resolvedClientY = clientH / 2;
                break;
        }

        // Clamp within safe client bounds so clicks never hit window resize borders or title bars
        if (clientW > 4)
            resolvedClientX = Math.Clamp(resolvedClientX, 2, clientW - 2);
        if (clientH > 4)
            resolvedClientY = Math.Clamp(resolvedClientY, 2, clientH - 2);

        var ptScreen = new Win32Native.POINT(resolvedClientX, resolvedClientY);
        if (target.Handle != IntPtr.Zero && Win32Native.IsWindow(target.Handle))
        {
            Win32Native.ClientToScreen(target.Handle, ref ptScreen);
        }
        else
        {
            ptScreen.X = target.ScreenClientLeft + resolvedClientX;
            ptScreen.Y = target.ScreenClientTop + resolvedClientY;
        }

        return (resolvedClientX, resolvedClientY, ptScreen.X, ptScreen.Y);
    }

    /// <summary>
    /// Resolves a recorded MacroEvent's client coordinates against the current target window geometry.
    /// </summary>
    public static (int ClientX, int ClientY, int ScreenX, int ScreenY) ResolveMacroEventCoordinates(
        TargetWindowInfo target,
        MacroEvent evt,
        bool scaleOnResize)
    {
        int clientW = target.ClientWidth > 0 ? target.ClientWidth : Math.Max(1, evt.ReferenceClientWidth);
        int clientH = target.ClientHeight > 0 ? target.ClientHeight : Math.Max(1, evt.ReferenceClientHeight);

        int cx = evt.ClientX;
        int cy = evt.ClientY;

        if (scaleOnResize &&
            evt.ReferenceClientWidth > 0 &&
            evt.ReferenceClientHeight > 0 &&
            target.ClientWidth > 0 &&
            target.ClientHeight > 0)
        {
            cx = (int)Math.Round(evt.ClientX * ((double)target.ClientWidth / evt.ReferenceClientWidth));
            cy = (int)Math.Round(evt.ClientY * ((double)target.ClientHeight / evt.ReferenceClientHeight));
        }

        if (clientW > 4) cx = Math.Clamp(cx, 2, clientW - 2);
        if (clientH > 4) cy = Math.Clamp(cy, 2, clientH - 2);

        var ptScreen = new Win32Native.POINT(cx, cy);
        if (target.Handle != IntPtr.Zero && Win32Native.IsWindow(target.Handle))
        {
            Win32Native.ClientToScreen(target.Handle, ref ptScreen);
        }

        return (cx, cy, ptScreen.X, ptScreen.Y);
    }

    private TargetWindowInfo BuildWindowInfo(IntPtr hWnd, uint pid, string procName, string title, string className, bool isRoblox)
    {
        var info = new TargetWindowInfo
        {
            Handle = hWnd,
            ProcessId = pid,
            ProcessName = procName,
            WindowTitle = title,
            ClassName = className,
            IsRobloxProcess = isRoblox
        };

        return RefreshTargetStatus(info);
    }

    private static bool IsProcessAlive(uint pid)
    {
        try
        {
            using Process p = Process.GetProcessById((int)pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }
}

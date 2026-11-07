using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using RobloxMacro.Core.Models;
using RobloxMacro.Core.Native;

namespace RobloxMacro.Core.Services;

public sealed class AutomationTelemetrySnapshot
{
    public AutomationRunState RunState { get; init; }
    public long DispatchedClickCount { get; init; }
    public TimeSpan ElapsedActiveTime { get; init; }
    public int LastResolvedClientX { get; init; }
    public int LastResolvedClientY { get; init; }
    public int RemainingCountdownMs { get; init; }
    public string StatusMessage { get; init; } = string.Empty;
}

/// <summary>
/// High-precision, low-CPU input automation and macro playback engine.
/// Enforces focus-guard safety in foreground mode and guarantees release of held inputs on pause/stop.
/// </summary>
public sealed class AutomationEngine : IDisposable
{
    private readonly TargetWindowService _windowService;
    private readonly InputDispatcher _dispatcher;
    private readonly object _sync = new();

    private CancellationTokenSource? _cts;
    private Task? _activeTask;

    public AutomationRunState CurrentState { get; private set; } = AutomationRunState.Idle;
    public long DispatchedClickCount { get; private set; }
    public bool IsRunning => CurrentState is AutomationRunState.Countdown or AutomationRunState.Running or AutomationRunState.AutoPausedFocusLost;

    public event Action<AutomationTelemetrySnapshot>? TelemetryUpdated;
    public event Action<string>? LogMessage;

    public AutomationEngine(TargetWindowService windowService, InputDispatcher dispatcher)
    {
        _windowService = windowService ?? throw new ArgumentNullException(nameof(windowService));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public void StartClickAutomation(TargetWindowInfo target, AutomationConfig config)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(config);

        Stop("Restarting automation session.");

        AutomationConfig snapshotConfig = config.Clone();
        var cts = new CancellationTokenSource();
        lock (_sync)
        {
            _cts = cts;
            DispatchedClickCount = 0;
            CurrentState = snapshotConfig.StartDelayMs > 0 ? AutomationRunState.Countdown : AutomationRunState.Running;
            _activeTask = Task.Run(() => ExecuteClickLoopAsync(target, snapshotConfig, cts.Token), cts.Token);
        }
    }

    public void StartMacroPlayback(TargetWindowInfo target, MacroProfile profile, AutomationConfig config)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(config);

        Stop("Starting macro playback.");

        AutomationConfig snapshotConfig = config.Clone();
        var cts = new CancellationTokenSource();
        lock (_sync)
        {
            _cts = cts;
            DispatchedClickCount = 0;
            CurrentState = snapshotConfig.StartDelayMs > 0 ? AutomationRunState.Countdown : AutomationRunState.Running;
            _activeTask = Task.Run(() => ExecuteMacroLoopAsync(target, profile, snapshotConfig, cts.Token), cts.Token);
        }
    }

    public void Stop(string reason = "Stopped by user.")
    {
        CancellationTokenSource? ctsToCancel;
        lock (_sync)
        {
            ctsToCancel = _cts;
            _cts = null;
        }

        if (ctsToCancel != null)
        {
            try
            {
                ctsToCancel.Cancel();
                ctsToCancel.Dispose();
            }
            catch
            {
                // Ignore disposal race
            }
        }

        _dispatcher.ReleaseAllHeldInputs();

        if (CurrentState != AutomationRunState.Idle && CurrentState != AutomationRunState.Stopped)
        {
            CurrentState = AutomationRunState.Stopped;
            EmitLog($"[Engine] {reason} All simulated held inputs released.");
            EmitTelemetry(AutomationRunState.Stopped, DispatchedClickCount, TimeSpan.Zero, 0, 0, 0, reason);
        }
    }

    /// <summary>
    /// Sends a controlled burst of background Win32 left-clicks for the 3-Scenario Verification Wizard.
    /// Returns (messagesSent, messagesSucceeded).
    /// </summary>
    public async Task<(int Sent, int Succeeded)> RunVerificationBurstAsync(
        TargetWindowInfo target,
        AutomationConfig config,
        int clickCount,
        int delayBeforeStartMs,
        CancellationToken cancellationToken = default)
    {
        if (delayBeforeStartMs > 0)
        {
            await Task.Delay(delayBeforeStartMs, cancellationToken);
        }

        int sent = 0;
        int succeeded = 0;

        for (int i = 0; i < clickCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _windowService.RefreshTargetStatus(target);

            var (cx, cy, sx, sy) = TargetWindowService.ResolveClickCoordinates(target, config);
            sent++;

            var downRes = _dispatcher.SendMouseButtonDown(
                target,
                InputDeliveryMode.BackgroundWin32Message,
                MacroMouseButton.Left,
                cx,
                cy,
                sx,
                sy,
                moveCursorInForeground: false,
                useSendMessageTimeout: config.UseSendMessageTimeoutInBackground,
                broadcastToChildren: config.BroadcastToChildWindowsInBackground);

            await Task.Delay(Math.Clamp(config.ClickHoldDurationMs, 15, 100), cancellationToken);

            var upRes = _dispatcher.SendMouseButtonUp(
                target,
                InputDeliveryMode.BackgroundWin32Message,
                MacroMouseButton.Left,
                cx,
                cy,
                useSendMessageTimeout: config.UseSendMessageTimeoutInBackground,
                broadcastToChildren: config.BroadcastToChildWindowsInBackground);

            if (downRes.Status == InputDispatchStatus.DispatchedSuccess &&
                upRes.Status == InputDispatchStatus.DispatchedSuccess)
            {
                succeeded++;
            }

            if (i < clickCount - 1)
            {
                await Task.Delay(Math.Clamp(config.ClickIntervalMs, 30, 500), cancellationToken);
            }
        }

        return (sent, succeeded);
    }

    private async Task ExecuteClickLoopAsync(TargetWindowInfo target, AutomationConfig config, CancellationToken token)
    {
        var activeRuntimeWatch = new Stopwatch();
        bool isButtonHeldInHoldMode = false;
        int lastClientX = 0;
        int lastClientY = 0;

        try
        {
            // 1. Optional Start Countdown
            if (config.StartDelayMs > 0)
            {
                CurrentState = AutomationRunState.Countdown;
                EmitLog($"[Engine] Starting countdown ({config.StartDelayMs} ms) for target {target.DisplayName}...");

                var countdownSw = Stopwatch.StartNew();
                while (countdownSw.ElapsedMilliseconds < config.StartDelayMs)
                {
                    token.ThrowIfCancellationRequested();
                    int remaining = Math.Max(0, config.StartDelayMs - (int)countdownSw.ElapsedMilliseconds);
                    EmitTelemetry(
                        AutomationRunState.Countdown,
                        0,
                        TimeSpan.Zero,
                        lastClientX,
                        lastClientY,
                        remaining,
                        $"Starting in {remaining / 1000.0:0.0}s...");
                    await Task.Delay(Math.Min(50, remaining + 1), token);
                }
            }

            CurrentState = AutomationRunState.Running;
            activeRuntimeWatch.Start();

            string modeDesc = config.DeliveryMode == InputDeliveryMode.ForegroundGuardedSendInput
                ? "Foreground-Guarded (SendInput + Focus Guard)"
                : "Background Win32 Message (PostMessage/SendMessage — Unverified in Roblox)";
            EmitLog($"[Engine] Automation running in [{modeDesc}] mode. Action={config.ActionMode}, Interval={config.ClickIntervalMs}ms.");

            bool loggedFocusPause = false;

            while (!token.IsCancellationRequested)
            {
                _windowService.RefreshTargetStatus(target);

                // Check if target closed or became invalid
                if (target.FocusState == WindowFocusState.Closed || target.FocusState == WindowFocusState.HiddenOrUnavailable)
                {
                    _dispatcher.ReleaseAllHeldInputs();
                    isButtonHeldInHoldMode = false;
                    activeRuntimeWatch.Stop();
                    CurrentState = AutomationRunState.Stopped;
                    string msg = target.FocusState == WindowFocusState.Closed
                        ? "Target window/process closed. Automation stopped safely."
                        : "Target window has no active window handle (0x0) in the current session.";
                    EmitLog($"[Engine] {msg}");
                    EmitTelemetry(AutomationRunState.Stopped, DispatchedClickCount, activeRuntimeWatch.Elapsed, lastClientX, lastClientY, 0, msg);
                    return;
                }

                // Focus Guard check for Foreground-Guarded mode
                if (config.DeliveryMode == InputDeliveryMode.ForegroundGuardedSendInput)
                {
                    bool hasFocus = target.FocusState == WindowFocusState.FocusedAndVisible &&
                                    InputDispatcher.IsTargetSafeForForegroundInput(target.Handle);

                    if (!hasFocus)
                    {
                        if (isButtonHeldInHoldMode)
                        {
                            _dispatcher.ReleaseAllHeldInputs();
                            isButtonHeldInHoldMode = false;
                        }
                        else
                        {
                            _dispatcher.ReleaseAllHeldInputs();
                        }

                        activeRuntimeWatch.Stop();

                        if (!config.AutoResumeWhenTargetRefocused)
                        {
                            CurrentState = AutomationRunState.Stopped;
                            const string stopFocusMsg = "Target window lost focus. Stopped safely (Auto-Resume disabled).";
                            EmitLog($"[Safety Guard] {stopFocusMsg}");
                            EmitTelemetry(AutomationRunState.Stopped, DispatchedClickCount, activeRuntimeWatch.Elapsed, lastClientX, lastClientY, 0, stopFocusMsg);
                            return;
                        }

                        CurrentState = AutomationRunState.AutoPausedFocusLost;
                        if (!loggedFocusPause)
                        {
                            loggedFocusPause = true;
                            EmitLog("[Safety Guard] Target window unfocused or minimized — Auto-Paused immediately so clicks never affect other apps.");
                        }

                        EmitTelemetry(
                            AutomationRunState.AutoPausedFocusLost,
                            DispatchedClickCount,
                            activeRuntimeWatch.Elapsed,
                            lastClientX,
                            lastClientY,
                            0,
                            "Auto-Paused: Waiting for Roblox window to regain focus...");

                        await Task.Delay(40, token);
                        continue;
                    }

                    if (loggedFocusPause)
                    {
                        loggedFocusPause = false;
                        CurrentState = AutomationRunState.Running;
                        activeRuntimeWatch.Start();
                        EmitLog("[Safety Guard] Target window regained focus — Resuming automation.");
                    }
                }

                // Resolve client & screen coordinates (auto-compensates for window move, resize, or DPI change)
                var (clientX, clientY, screenX, screenY) = TargetWindowService.ResolveClickCoordinates(target, config);
                lastClientX = clientX;
                lastClientY = clientY;

                bool moveCursorInFg = config.DeliveryMode == InputDeliveryMode.ForegroundGuardedSendInput &&
                                      config.PositionMode != ClickPositionMode.CurrentCursorPosition;

                if (config.ActionMode == ClickActionMode.ClickAndHold)
                {
                    if (!isButtonHeldInHoldMode)
                    {
                        var downRes = _dispatcher.SendMouseButtonDown(
                            target,
                            config.DeliveryMode,
                            MacroMouseButton.Left,
                            clientX,
                            clientY,
                            screenX,
                            screenY,
                            moveCursorInFg,
                            config.UseSendMessageTimeoutInBackground,
                            config.BroadcastToChildWindowsInBackground);

                        if (downRes.Status == InputDispatchStatus.DispatchedSuccess)
                        {
                            isButtonHeldInHoldMode = true;
                            DispatchedClickCount++;
                            EmitLog($"[Engine] Left-Button HOLD engaged at client ({clientX}, {clientY}).");
                        }
                    }

                    EmitTelemetry(
                        AutomationRunState.Running,
                        DispatchedClickCount,
                        activeRuntimeWatch.Elapsed,
                        clientX,
                        clientY,
                        0,
                        $"Holding Left Mouse Button @ ({clientX}, {clientY})");

                    await Task.Delay(35, token);
                }
                else
                {
                    // RepeatedLeftClick Mode
                    Win32Native.POINT savedCursor = default;
                    bool restoreCursor = moveCursorInFg && config.RestoreCursorAfterForegroundClick &&
                                         Win32Native.GetCursorPos(out savedCursor);

                    var downRes = _dispatcher.SendMouseButtonDown(
                        target,
                        config.DeliveryMode,
                        MacroMouseButton.Left,
                        clientX,
                        clientY,
                        screenX,
                        screenY,
                        moveCursorInFg,
                        config.UseSendMessageTimeoutInBackground,
                        config.BroadcastToChildWindowsInBackground);

                    int holdMs = Math.Clamp(config.ClickHoldDurationMs, 5, 5000);
                    await Task.Delay(holdMs, token);

                    var upRes = _dispatcher.SendMouseButtonUp(
                        target,
                        config.DeliveryMode,
                        MacroMouseButton.Left,
                        clientX,
                        clientY,
                        config.UseSendMessageTimeoutInBackground,
                        config.BroadcastToChildWindowsInBackground);

                    if (restoreCursor)
                    {
                        Win32Native.SetCursorPos(savedCursor.X, savedCursor.Y);
                    }

                    if (downRes.Status == InputDispatchStatus.DispatchedSuccess &&
                        upRes.Status == InputDispatchStatus.DispatchedSuccess)
                    {
                        DispatchedClickCount++;
                    }
                    else if (downRes.Status == InputDispatchStatus.Win32ApiError)
                    {
                        EmitLog($"[Engine Warning] Win32 dispatch error: {downRes.Detail}");
                    }

                    string deliveryNote = config.DeliveryMode == InputDeliveryMode.ForegroundGuardedSendInput
                        ? $"Foreground Click #{DispatchedClickCount} @ ({clientX}, {clientY})"
                        : $"Win32 Msg #{DispatchedClickCount} @ ({clientX}, {clientY}) [Unverified In-Game]";

                    EmitTelemetry(
                        AutomationRunState.Running,
                        DispatchedClickCount,
                        activeRuntimeWatch.Elapsed,
                        clientX,
                        clientY,
                        0,
                        deliveryNote);

                    if (config.EnableClickLimit && config.MaxClickCount > 0 && DispatchedClickCount >= config.MaxClickCount)
                    {
                        activeRuntimeWatch.Stop();
                        CurrentState = AutomationRunState.Stopped;
                        string limitMsg = $"Completed target click limit ({DispatchedClickCount} / {config.MaxClickCount}).";
                        EmitLog($"[Engine] {limitMsg}");
                        EmitTelemetry(AutomationRunState.Stopped, DispatchedClickCount, activeRuntimeWatch.Elapsed, clientX, clientY, 0, limitMsg);
                        return;
                    }

                    int waitMs = Math.Max(5, config.ClickIntervalMs);
                    await Task.Delay(waitMs, token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            EmitLog($"[Engine Error] Unexpected exception in click loop: {ex.Message}");
        }
        finally
        {
            _dispatcher.ReleaseAllHeldInputs();
            activeRuntimeWatch.Stop();
        }
    }

    private async Task ExecuteMacroLoopAsync(
        TargetWindowInfo target,
        MacroProfile profile,
        AutomationConfig config,
        CancellationToken token)
    {
        var activeRuntimeWatch = new Stopwatch();
        try
        {
            if (profile.Events.Count == 0)
            {
                EmitLog("[Macro Player] Cannot play empty macro profile.");
                CurrentState = AutomationRunState.Stopped;
                return;
            }

            if (config.StartDelayMs > 0)
            {
                CurrentState = AutomationRunState.Countdown;
                var countdownSw = Stopwatch.StartNew();
                while (countdownSw.ElapsedMilliseconds < config.StartDelayMs)
                {
                    token.ThrowIfCancellationRequested();
                    int remaining = Math.Max(0, config.StartDelayMs - (int)countdownSw.ElapsedMilliseconds);
                    EmitTelemetry(AutomationRunState.Countdown, 0, TimeSpan.Zero, 0, 0, remaining, $"Macro starting in {remaining / 1000.0:0.0}s...");
                    await Task.Delay(Math.Min(50, remaining + 1), token);
                }
            }

            CurrentState = AutomationRunState.Running;
            activeRuntimeWatch.Start();
            int maxLoops = profile.LoopIndefinitely ? int.MaxValue : Math.Max(1, profile.LoopCount);

            EmitLog($"[Macro Player] Playing profile \"{profile.Name}\" ({profile.Events.Count} events, Loops={(profile.LoopIndefinitely ? "Infinite" : maxLoops.ToString())}).");

            for (int loop = 0; loop < maxLoops && !token.IsCancellationRequested; loop++)
            {
                foreach (MacroEvent evt in profile.Events)
                {
                    token.ThrowIfCancellationRequested();

                    if (evt.DelayBeforeMs > 0)
                    {
                        await Task.Delay(Math.Clamp(evt.DelayBeforeMs, 1, 60000), token);
                    }

                    // Wait for focus if Foreground-Guarded mode is active
                    while (!token.IsCancellationRequested)
                    {
                        _windowService.RefreshTargetStatus(target);
                        if (target.FocusState is WindowFocusState.Closed or WindowFocusState.HiddenOrUnavailable)
                        {
                            _dispatcher.ReleaseAllHeldInputs();
                            CurrentState = AutomationRunState.Stopped;
                            EmitLog("[Macro Player] Target window closed or unavailable. Stopping macro playback.");
                            return;
                        }

                        if (config.DeliveryMode == InputDeliveryMode.ForegroundGuardedSendInput &&
                            !InputDispatcher.IsTargetSafeForForegroundInput(target.Handle))
                        {
                            _dispatcher.ReleaseAllHeldInputs();
                            activeRuntimeWatch.Stop();
                            CurrentState = AutomationRunState.AutoPausedFocusLost;
                            EmitTelemetry(
                                AutomationRunState.AutoPausedFocusLost,
                                DispatchedClickCount,
                                activeRuntimeWatch.Elapsed,
                                evt.ClientX,
                                evt.ClientY,
                                0,
                                "Macro Paused: Waiting for target window focus...");
                            await Task.Delay(40, token);
                            continue;
                        }

                        if (CurrentState == AutomationRunState.AutoPausedFocusLost)
                        {
                            CurrentState = AutomationRunState.Running;
                            activeRuntimeWatch.Start();
                        }
                        break;
                    }

                    var (cx, cy, sx, sy) = TargetWindowService.ResolveMacroEventCoordinates(
                        target,
                        evt,
                        profile.ScaleCoordinatesOnResize);

                    bool moveCursor = config.DeliveryMode == InputDeliveryMode.ForegroundGuardedSendInput;

                    switch (evt.EventType)
                    {
                        case MacroEventType.LeftMouseClick:
                        case MacroEventType.RightMouseClick:
                        case MacroEventType.MiddleMouseClick:
                        {
                            MacroMouseButton btn = evt.EventType switch
                            {
                                MacroEventType.RightMouseClick => MacroMouseButton.Right,
                                MacroEventType.MiddleMouseClick => MacroMouseButton.Middle,
                                _ => MacroMouseButton.Left
                            };

                            _dispatcher.SendMouseButtonDown(
                                target,
                                config.DeliveryMode,
                                btn,
                                cx,
                                cy,
                                sx,
                                sy,
                                moveCursor,
                                config.UseSendMessageTimeoutInBackground,
                                config.BroadcastToChildWindowsInBackground);

                            await Task.Delay(Math.Clamp(evt.HoldDurationMs, 5, 10000), token);

                            _dispatcher.SendMouseButtonUp(
                                target,
                                config.DeliveryMode,
                                btn,
                                cx,
                                cy,
                                config.UseSendMessageTimeoutInBackground,
                                config.BroadcastToChildWindowsInBackground);

                            DispatchedClickCount++;
                            break;
                        }

                        case MacroEventType.MouseDown:
                            _dispatcher.SendMouseButtonDown(
                                target,
                                config.DeliveryMode,
                                evt.MouseButton,
                                cx,
                                cy,
                                sx,
                                sy,
                                moveCursor,
                                config.UseSendMessageTimeoutInBackground,
                                config.BroadcastToChildWindowsInBackground);
                            break;

                        case MacroEventType.MouseUp:
                            _dispatcher.SendMouseButtonUp(
                                target,
                                config.DeliveryMode,
                                evt.MouseButton,
                                cx,
                                cy,
                                config.UseSendMessageTimeoutInBackground,
                                config.BroadcastToChildWindowsInBackground);
                            DispatchedClickCount++;
                            break;

                        case MacroEventType.KeyDown:
                            _dispatcher.SendKeyEvent(
                                target,
                                config.DeliveryMode,
                                evt.VirtualKeyCode,
                                isKeyDown: true,
                                config.UseSendMessageTimeoutInBackground);
                            break;

                        case MacroEventType.KeyUp:
                            _dispatcher.SendKeyEvent(
                                target,
                                config.DeliveryMode,
                                evt.VirtualKeyCode,
                                isKeyDown: false,
                                config.UseSendMessageTimeoutInBackground);
                            break;
                    }

                    EmitTelemetry(
                        AutomationRunState.Running,
                        DispatchedClickCount,
                        activeRuntimeWatch.Elapsed,
                        cx,
                        cy,
                        0,
                        $"Macro Event #{evt.Index}: {evt.Description}");
                }
            }

            activeRuntimeWatch.Stop();
            CurrentState = AutomationRunState.Stopped;
            EmitLog($"[Macro Player] Finished playing macro \"{profile.Name}\".");
            EmitTelemetry(AutomationRunState.Stopped, DispatchedClickCount, activeRuntimeWatch.Elapsed, 0, 0, 0, "Macro playback finished.");
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            EmitLog($"[Macro Player Error] {ex.Message}");
        }
        finally
        {
            _dispatcher.ReleaseAllHeldInputs();
            activeRuntimeWatch.Stop();
        }
    }

    private void EmitTelemetry(
        AutomationRunState state,
        long clicks,
        TimeSpan elapsed,
        int clientX,
        int clientY,
        int countdownMs,
        string message)
    {
        TelemetryUpdated?.Invoke(new AutomationTelemetrySnapshot
        {
            RunState = state,
            DispatchedClickCount = clicks,
            ElapsedActiveTime = elapsed,
            LastResolvedClientX = clientX,
            LastResolvedClientY = clientY,
            RemainingCountdownMs = countdownMs,
            StatusMessage = message
        });
    }

    private void EmitLog(string message)
    {
        LogMessage?.Invoke($"{DateTime.Now:HH:mm:ss.fff} {message}");
    }

    public void Dispose()
    {
        Stop("Application shutting down.");
        _dispatcher.Dispose();
    }
}

using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using RobloxMacro.Core.Models;
using RobloxMacro.Core.Native;
using RobloxMacro.Core.Services;

namespace RobloxMacro.App;

public partial class MainWindow : Window
{
    private readonly TargetWindowService _windowService = new();
    private readonly InputDispatcher _dispatcher = new();
    private readonly AutomationEngine _engine;
    private readonly MacroRecorderService _macroRecorder;
    private readonly GlobalHotkeyService _hotkeyService = new();
    private readonly BackgroundCompatibilityReport _compatReport = new();
    private readonly ObservableCollection<MacroEvent> _macroEvents = new();
    private readonly DispatcherTimer _statusMonitorTimer;

    private HwndSource? _hwndSource;
    private IntPtr _ownHwnd;
    private TargetWindowInfo? _selectedTarget;
    private TestTargetWindow? _testTargetWindow;

    private readonly (string Label, ushort Vk)[] _hotkeyChoices =
    [
        ("F1", 0x70), ("F2", 0x71), ("F3", 0x72), ("F4", 0x73),
        ("F5", 0x74), ("F6", 0x75), ("F7", 0x76), ("F8", 0x77),
        ("F9", 0x78), ("F10", 0x79), ("F11", 0x7A), ("F12", 0x7B)
    ];

    public MainWindow()
    {
        InitializeComponent();

        _engine = new AutomationEngine(_windowService, _dispatcher);
        _macroRecorder = new MacroRecorderService(_windowService);
        MacroEventsGrid.ItemsSource = _macroEvents;

        _engine.TelemetryUpdated += OnEngineTelemetryUpdated;
        _engine.LogMessage += AppendLogThreadSafe;
        _macroRecorder.EventRecorded += OnMacroEventRecorded;
        _macroRecorder.LogMessage += AppendLogThreadSafe;
        _hotkeyService.LogMessage += AppendLogThreadSafe;

        _hotkeyService.StartStopTriggered += OnHotkeyStartStop;
        _hotkeyService.EmergencyStopTriggered += OnHotkeyEmergencyStop;
        _hotkeyService.PickPositionTriggered += OnHotkeyPickPosition;

        PopulateHotkeyDropdowns();

        _statusMonitorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _statusMonitorTimer.Tick += OnStatusMonitorTick;

        SourceInitialized += OnWindowSourceInitialized;
        Loaded += OnWindowLoaded;
        Closing += OnWindowClosing;
    }

    private void PopulateHotkeyDropdowns()
    {
        foreach (var (label, _) in _hotkeyChoices)
        {
            StartStopHotkeyCombo.Items.Add(label);
            EmergencyStopHotkeyCombo.Items.Add(label);
            PickPosHotkeyCombo.Items.Add(label);
        }

        StartStopHotkeyCombo.SelectedIndex = 5;    // F6
        EmergencyStopHotkeyCombo.SelectedIndex = 6; // F7
        PickPosHotkeyCombo.SelectedIndex = 7;       // F8
    }

    private void OnWindowSourceInitialized(object? sender, EventArgs e)
    {
        _ownHwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_ownHwnd);
        _hwndSource?.AddHook(WndProc);
        ApplySelectedHotkeys();
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        AppendLog("[System] Roblox Attack Macro & Input Suite initialized (Standard User-Mode, Per-Monitor V2 DPI Aware).");
        RefreshTargetList();
        UpdateCompatibilityBanner();
        _statusMonitorTimer.Start();

        if (App.IsSmokeTestMode)
        {
            AppendLog("[SmokeTest] Smoke test mode active — validating UI and engine startup, then exiting cleanly.");
            Dispatcher.InvokeAsync(async () =>
            {
                await System.Threading.Tasks.Task.Delay(350);
                Close();
            });
        }
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _statusMonitorTimer.Stop();
        _macroRecorder.Dispose();
        _engine.Dispose();
        _hotkeyService.Dispose();
        _hwndSource?.RemoveHook(WndProc);

        if (_testTargetWindow != null)
        {
            try
            {
                _testTargetWindow.Close();
            }
            catch
            {
                // Ignore
            }
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_hotkeyService.ProcessWndProcMessage(msg, wParam))
        {
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void RefreshTargetList()
    {
        bool includeAll = ShowAllWindowsCheck.IsChecked == true;
        var targets = _windowService.DiscoverTargetWindows(includeAll, _ownHwnd);

        IntPtr previousHwnd = _selectedTarget?.Handle ?? IntPtr.Zero;
        uint previousPid = _selectedTarget?.ProcessId ?? 0;

        TargetWindowCombo.ItemsSource = targets;

        TargetWindowInfo? match = targets.FirstOrDefault(t =>
            (previousHwnd != IntPtr.Zero && t.Handle == previousHwnd) ||
            (previousPid != 0 && t.ProcessId == previousPid));

        match ??= targets.FirstOrDefault(t => t.IsRobloxProcess) ?? targets.FirstOrDefault();

        if (match != null)
        {
            TargetWindowCombo.SelectedItem = match;
            _selectedTarget = match;
        }
        else
        {
            _selectedTarget = null;
        }

        UpdateSelectedTargetStatusUI();
    }

    private void OnStatusMonitorTick(object? sender, EventArgs e)
    {
        if (_selectedTarget != null)
        {
            _windowService.RefreshTargetStatus(_selectedTarget);
            UpdateSelectedTargetStatusUI();
        }
    }

    private void UpdateSelectedTargetStatusUI()
    {
        if (_selectedTarget == null)
        {
            TargetFocusBadge.Background = new SolidColorBrush(Color.FromRgb(30, 41, 59));
            TargetFocusBadgeText.Text = "NO TARGET";
            TargetFocusBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
            TargetDetailsText.Text = "No target window selected. Launch Roblox or click 'Launch Test Target Window'.";
            return;
        }

        switch (_selectedTarget.FocusState)
        {
            case WindowFocusState.FocusedAndVisible:
                TargetFocusBadge.Background = new SolidColorBrush(Color.FromRgb(6, 78, 59));
                TargetFocusBadgeText.Text = "FOCUSED & VISIBLE";
                TargetFocusBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
                break;

            case WindowFocusState.VisibleUnfocused:
                TargetFocusBadge.Background = new SolidColorBrush(Color.FromRgb(120, 53, 15));
                TargetFocusBadgeText.Text = "VISIBLE (UNFOCUSED)";
                TargetFocusBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
                break;

            case WindowFocusState.Minimized:
                TargetFocusBadge.Background = new SolidColorBrush(Color.FromRgb(127, 29, 29));
                TargetFocusBadgeText.Text = "MINIMIZED (ICONIC)";
                TargetFocusBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
                break;

            case WindowFocusState.HiddenOrUnavailable:
                TargetFocusBadge.Background = new SolidColorBrush(Color.FromRgb(68, 64, 60));
                TargetFocusBadgeText.Text = "HANDLE 0x0 / HIDDEN";
                TargetFocusBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(253, 186, 116));
                break;

            case WindowFocusState.Closed:
            default:
                TargetFocusBadge.Background = new SolidColorBrush(Color.FromRgb(127, 29, 29));
                TargetFocusBadgeText.Text = "CLOSED";
                TargetFocusBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
                break;
        }

        TargetDetailsText.Text =
            $"Process: {_selectedTarget.ProcessName}.exe (PID {_selectedTarget.ProcessId}) | " +
            $"HWND: {_selectedTarget.HandleHex} | Class: {_selectedTarget.ClassName} | " +
            $"State: {_selectedTarget.StatusSummary}";
    }

    private void UpdateCompatibilityBanner()
    {
        bool isBgSelected = ModeBackgroundWin32Radio?.IsChecked == true;
        CompatibilityHeadlineText.Text = $"Background Input Status: {_compatReport.HeadlineStatus}";
        CompatibilityDetailText.Text = _compatReport.TechnicalExplanation;

        if (_compatReport.IsBackgroundVerifiedWorking)
        {
            CompatibilityBannerBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            CompatibilityHeadlineText.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
        }
        else if (isBgSelected)
        {
            CompatibilityBannerBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            CompatibilityHeadlineText.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
        }
        else
        {
            CompatibilityBannerBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            CompatibilityHeadlineText.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
        }

        if (Scenario1StatusText != null)
            Scenario1StatusText.Text = $"Status: {_compatReport.Scenario1Focused.SummaryText}";
        if (Scenario2StatusText != null)
            Scenario2StatusText.Text = $"Status: {_compatReport.Scenario2UnfocusedVisible.SummaryText}";
        if (Scenario3StatusText != null)
            Scenario3StatusText.Text = $"Status: {_compatReport.Scenario3Minimized.SummaryText}";
    }

    private AutomationConfig ReadConfigFromUI()
    {
        int intervalMs = ParsePositiveInt(ClickIntervalInput.Text, 100, 5, 60000);
        int holdMs = ParsePositiveInt(ClickHoldInput.Text, 35, 5, 30000);
        int startDelayMs = ParsePositiveInt(StartDelayInput.Text, 1500, 0, 30000);
        long maxClicks = ParsePositiveLong(MaxClicksInput.Text, 500, 1, 100_000_000);

        int clientX = ParsePositiveInt(ClientXInput.Text, 400, 0, 16384);
        int clientY = ParsePositiveInt(ClientYInput.Text, 300, 0, 16384);
        int refW = ParsePositiveInt(RefWidthInput.Text, 800, 1, 16384);
        int refH = ParsePositiveInt(RefHeightInput.Text, 600, 1, 16384);

        var deliveryMode = ModeBackgroundWin32Radio.IsChecked == true
            ? InputDeliveryMode.BackgroundWin32Message
            : InputDeliveryMode.ForegroundGuardedSendInput;

        var actionMode = ActionClickAndHoldRadio.IsChecked == true
            ? ClickActionMode.ClickAndHold
            : ClickActionMode.RepeatedLeftClick;

        var posMode = ClickPositionMode.ClientCenter;
        if (PosFixedClientRadio.IsChecked == true)
            posMode = ClickPositionMode.FixedClientPosition;
        else if (PosCurrentCursorRadio.IsChecked == true)
            posMode = ClickPositionMode.CurrentCursorPosition;

        return new AutomationConfig
        {
            DeliveryMode = deliveryMode,
            ActionMode = actionMode,
            PositionMode = posMode,
            ClickIntervalMs = intervalMs,
            ClickHoldDurationMs = holdMs,
            StartDelayMs = startDelayMs,
            EnableClickLimit = EnableClickLimitCheck.IsChecked == true,
            MaxClickCount = maxClicks,
            TargetClientX = clientX,
            TargetClientY = clientY,
            ReferenceClientWidth = refW,
            ReferenceClientHeight = refH,
            ScaleCoordinatesOnResize = ScaleOnResizeCheck.IsChecked == true,
            AutoResumeWhenTargetRefocused = AutoResumeCheck.IsChecked == true,
            RestoreCursorAfterForegroundClick = RestoreCursorCheck.IsChecked == true,
            BroadcastToChildWindowsInBackground = BroadcastChildrenCheck.IsChecked == true,
            UseSendMessageTimeoutInBackground = UseSendMessageTimeoutCheck.IsChecked == true
        };
    }

    private void OnEngineTelemetryUpdated(AutomationTelemetrySnapshot snap)
    {
        Dispatcher.BeginInvoke(() =>
        {
            StateValueText.Text = snap.RunState switch
            {
                AutomationRunState.Idle => "IDLE",
                AutomationRunState.Countdown => $"COUNTDOWN ({snap.RemainingCountdownMs / 1000.0:0.0}s)",
                AutomationRunState.Running => "RUNNING",
                AutomationRunState.AutoPausedFocusLost => "AUTO-PAUSED (FOCUS)",
                AutomationRunState.Stopped => "STOPPED",
                _ => snap.RunState.ToString().ToUpperInvariant()
            };

            StateValueText.Foreground = snap.RunState switch
            {
                AutomationRunState.Running => new SolidColorBrush(Color.FromRgb(16, 185, 129)),
                AutomationRunState.AutoPausedFocusLost => new SolidColorBrush(Color.FromRgb(245, 158, 11)),
                AutomationRunState.Countdown => new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                _ => new SolidColorBrush(Color.FromRgb(148, 163, 184))
            };

            ClickCountValueText.Text = snap.DispatchedClickCount.ToString("N0");
            ElapsedTimeValueText.Text = snap.ElapsedActiveTime.ToString(@"hh\:mm\:ss\.f");
            ResolvedCoordsValueText.Text = $"({snap.LastResolvedClientX}, {snap.LastResolvedClientY})";
        });
    }

    private void OnMacroEventRecorded(MacroEvent evt)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _macroEvents.Add(evt);
        });
    }

    private void TargetWindowCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TargetWindowCombo.SelectedItem is TargetWindowInfo info)
        {
            _selectedTarget = info;
            _windowService.RefreshTargetStatus(_selectedTarget);
            if (_selectedTarget.ClientWidth > 0 && _selectedTarget.ClientHeight > 0)
            {
                RefWidthInput.Text = _selectedTarget.ClientWidth.ToString();
                RefHeightInput.Text = _selectedTarget.ClientHeight.ToString();
            }
            UpdateSelectedTargetStatusUI();
        }
    }

    private void ShowAllWindowsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            RefreshTargetList();
        }
    }

    private void RefreshTargets_Click(object sender, RoutedEventArgs e)
    {
        RefreshTargetList();
        AppendLog("[Target] Refreshed target window list.");
    }

    private void PickWindowUnderCursor_Click(object sender, RoutedEventArgs e)
    {
        CaptureWindowAndCoordinatesUnderCursor();
    }

    private void CaptureWindowAndCoordinatesUnderCursor()
    {
        var (win, cx, cy) = _windowService.InspectWindowUnderCursor();
        if (win == null || win.Handle == _ownHwnd)
        {
            AppendLog("[Picker] Move your mouse cursor over the target Roblox/test window and press F8 to capture its window & client coordinates.");
            return;
        }

        _selectedTarget = win;
        ShowAllWindowsCheck.IsChecked = true;
        RefreshTargetList();

        ClientXInput.Text = Math.Max(0, cx).ToString();
        ClientYInput.Text = Math.Max(0, cy).ToString();
        if (win.ClientWidth > 0 && win.ClientHeight > 0)
        {
            RefWidthInput.Text = win.ClientWidth.ToString();
            RefHeightInput.Text = win.ClientHeight.ToString();
        }
        PosFixedClientRadio.IsChecked = true;

        AppendLog($"[Picker] Captured target {win.DisplayName} at client coordinate ({cx}, {cy}) [Ref Size: {win.ClientWidth}x{win.ClientHeight}].");
    }

    private void LaunchTestTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_testTargetWindow == null || !_testTargetWindow.IsLoaded)
        {
            _testTargetWindow = new TestTargetWindow();
            _testTargetWindow.Closed += (_, _) => _testTargetWindow = null;
            _testTargetWindow.Show();
        }
        else
        {
            _testTargetWindow.Activate();
        }

        ShowAllWindowsCheck.IsChecked = true;
        RefreshTargetList();

        if (_testTargetWindow.WindowHandle != IntPtr.Zero)
        {
            var items = TargetWindowCombo.ItemsSource as System.Collections.Generic.IEnumerable<TargetWindowInfo>;
            var match = items?.FirstOrDefault(w => w.Handle == _testTargetWindow.WindowHandle);
            if (match != null)
            {
                TargetWindowCombo.SelectedItem = match;
                _selectedTarget = match;
                UpdateSelectedTargetStatusUI();
            }
        }

        AppendLog("[Test Target] Launched Controlled Win32 Target Window (simulates both raw Win32 WndProc and Roblox Focus-Gated input).");
    }

    private void InputMode_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            UpdateCompatibilityBanner();
        }
    }

    private void ApplyHotkeys_Click(object sender, RoutedEventArgs e)
    {
        ApplySelectedHotkeys();
    }

    private void ApplySelectedHotkeys()
    {
        int sIdx = Math.Clamp(StartStopHotkeyCombo.SelectedIndex, 0, _hotkeyChoices.Length - 1);
        int eIdx = Math.Clamp(EmergencyStopHotkeyCombo.SelectedIndex, 0, _hotkeyChoices.Length - 1);
        int pIdx = Math.Clamp(PickPosHotkeyCombo.SelectedIndex, 0, _hotkeyChoices.Length - 1);

        ushort sVk = _hotkeyChoices[sIdx].Vk;
        ushort eVk = _hotkeyChoices[eIdx].Vk;
        ushort pVk = _hotkeyChoices[pIdx].Vk;

        _hotkeyService.BindToWindow(_ownHwnd, sVk, eVk, pVk);
        HotkeySummaryBadge.Text =
            $"Hotkeys: Start/Stop [{_hotkeyChoices[sIdx].Label}] | Emergency Stop [{_hotkeyChoices[eIdx].Label}] | Pick Pos [{_hotkeyChoices[pIdx].Label}]";
    }

    private void StartAutomation_Click(object sender, RoutedEventArgs e)
    {
        StartClickAutomationFromUI();
    }

    private void StopAutomation_Click(object sender, RoutedEventArgs e)
    {
        _engine.Stop("Emergency Stop triggered by user.");
    }

    private void StartClickAutomationFromUI()
    {
        if (_selectedTarget == null)
        {
            MessageBox.Show(
                this,
                "Please select a Roblox target window (or launch the Controlled Test Target Window) before starting automation.",
                "No Target Selected",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        AutomationConfig cfg = ReadConfigFromUI();
        _engine.StartClickAutomation(_selectedTarget, cfg);
    }

    private void OnHotkeyStartStop()
    {
        if (_engine.IsRunning)
        {
            _engine.Stop("Stopped via Global Start/Stop Hotkey.");
        }
        else
        {
            StartClickAutomationFromUI();
        }
    }

    private void OnHotkeyEmergencyStop()
    {
        if (_macroRecorder.IsRecording)
        {
            StopRecordingMacroUI();
        }
        _engine.Stop("EMERGENCY STOP Hotkey pressed — all automation halted and held inputs released.");
    }

    private void OnHotkeyPickPosition()
    {
        CaptureWindowAndCoordinatesUnderCursor();
    }

    // -------------------------------------------------------------------------
    // MACRO RECORDER & PLAYBACK HANDLERS
    // -------------------------------------------------------------------------

    private void RecordMacro_Click(object sender, RoutedEventArgs e)
    {
        _engine.Stop("Stopping engine before recording macro.");
        _macroEvents.Clear();

        bool captureKeys = MacroCaptureKeyboardCheck.IsChecked == true;
        _macroRecorder.StartRecording(_selectedTarget, captureKeys, _hotkeyService.GetActiveHotkeyVirtualKeys());

        RecordMacroBtn.IsEnabled = false;
        StopRecordMacroBtn.IsEnabled = true;
        PlayMacroBtn.IsEnabled = false;
    }

    private void StopRecordMacro_Click(object sender, RoutedEventArgs e)
    {
        StopRecordingMacroUI();
    }

    private void StopRecordingMacroUI()
    {
        _macroRecorder.StopRecording();
        RecordMacroBtn.IsEnabled = true;
        StopRecordMacroBtn.IsEnabled = false;
        PlayMacroBtn.IsEnabled = true;
    }

    private void PlayMacro_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTarget == null)
        {
            MessageBox.Show(this, "Please select a target window first.", "No Target", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_macroRecorder.RecordedEvents.Count == 0)
        {
            MessageBox.Show(this, "No macro events recorded or loaded. Record a sequence or insert events first.", "Empty Macro", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int loops = ParsePositiveInt(MacroLoopCountInput.Text, 1, 1, 100000);
        MacroProfile profile = _macroRecorder.BuildProfile(
            "Active Macro Sequence",
            scaleOnResize: ScaleOnResizeCheck.IsChecked == true,
            loopIndefinitely: MacroLoopInfiniteCheck.IsChecked == true,
            loopCount: loops);

        AutomationConfig cfg = ReadConfigFromUI();
        _engine.StartMacroPlayback(_selectedTarget, profile, cfg);
    }

    private void InsertManualClickEvent_Click(object sender, RoutedEventArgs e)
    {
        AutomationConfig cfg = ReadConfigFromUI();
        int cx = cfg.TargetClientX;
        int cy = cfg.TargetClientY;
        int refW = cfg.ReferenceClientWidth;
        int refH = cfg.ReferenceClientHeight;

        if (_selectedTarget != null && _selectedTarget.ClientWidth > 0 && _selectedTarget.ClientHeight > 0)
        {
            refW = _selectedTarget.ClientWidth;
            refH = _selectedTarget.ClientHeight;
            if (cfg.PositionMode == ClickPositionMode.ClientCenter)
            {
                cx = refW / 2;
                cy = refH / 2;
            }
        }

        _macroRecorder.AddManualClickEvent(cx, cy, cfg.ClickIntervalMs, cfg.ClickHoldDurationMs, refW, refH, MacroMouseButton.Left);
        AppendLog($"[Macro Builder] Inserted Left Click @ ({cx}, {cy}) [Wait={cfg.ClickIntervalMs}ms, Hold={cfg.ClickHoldDurationMs}ms].");
    }

    private void InsertManualSpaceKey_Click(object sender, RoutedEventArgs e)
    {
        _macroRecorder.AddManualKeyEvent(0x20, "Space", isKeyDown: true, delayBeforeMs: 100);
        _macroRecorder.AddManualKeyEvent(0x20, "Space", isKeyDown: false, delayBeforeMs: 65);
        AppendLog("[Macro Builder] Inserted Spacebar Press + Release sequence.");
    }

    private void ClearMacroEvents_Click(object sender, RoutedEventArgs e)
    {
        _macroRecorder.ClearEvents();
        _macroEvents.Clear();
        AppendLog("[Macro Recorder] Cleared macro event list.");
    }

    private void SaveMacroProfile_Click(object sender, RoutedEventArgs e)
    {
        int loops = ParsePositiveInt(MacroLoopCountInput.Text, 1, 1, 100000);
        MacroProfile profile = _macroRecorder.BuildProfile(
            "Saved Roblox Attack Profile",
            scaleOnResize: ScaleOnResizeCheck.IsChecked == true,
            loopIndefinitely: MacroLoopInfiniteCheck.IsChecked == true,
            loopCount: loops);

        var dlg = new SaveFileDialog
        {
            Title = "Save Macro Profile",
            Filter = "Macro Profile JSON (*.json)|*.json",
            FileName = "RobloxAttackMacro.json"
        };

        if (dlg.ShowDialog(this) == true)
        {
            MacroRecorderService.SaveProfileToFile(profile, dlg.FileName);
            AppendLog($"[Macro Profile] Saved {profile.Events.Count} events to '{dlg.FileName}'.");
        }
    }

    private void LoadMacroProfile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Load Macro Profile",
            Filter = "Macro Profile JSON (*.json)|*.json"
        };

        if (dlg.ShowDialog(this) == true && File.Exists(dlg.FileName))
        {
            MacroProfile profile = MacroRecorderService.LoadProfileFromFile(dlg.FileName);
            _macroRecorder.LoadProfileIntoRecorder(profile);
            _macroEvents.Clear();
            foreach (MacroEvent evt in _macroRecorder.RecordedEvents)
            {
                _macroEvents.Add(evt);
            }

            MacroLoopInfiniteCheck.IsChecked = profile.LoopIndefinitely;
            MacroLoopCountInput.Text = profile.LoopCount.ToString();
            AppendLog($"[Macro Profile] Loaded '{profile.Name}' ({profile.Events.Count} events) from '{dlg.FileName}'.");
        }
    }

    // -------------------------------------------------------------------------
    // TAB 3: 3-SCENARIO VERIFICATION LAB HANDLERS
    // -------------------------------------------------------------------------

    private async void RunScenario1Test_Click(object sender, RoutedEventArgs e)
    {
        await RunScenarioBurstUIAsync(_compatReport.Scenario1Focused, delayMs: 3000);
    }

    private async void RunScenario2Test_Click(object sender, RoutedEventArgs e)
    {
        await RunScenarioBurstUIAsync(_compatReport.Scenario2UnfocusedVisible, delayMs: 300);
    }

    private async void RunScenario3Test_Click(object sender, RoutedEventArgs e)
    {
        await RunScenarioBurstUIAsync(_compatReport.Scenario3Minimized, delayMs: 300);
    }

    private async System.Threading.Tasks.Task RunScenarioBurstUIAsync(ScenarioVerificationResult scenario, int delayMs)
    {
        if (_selectedTarget == null || _selectedTarget.Handle == IntPtr.Zero)
        {
            MessageBox.Show(this, "Select a valid target window with a non-zero HWND before running the verification burst.", "Target Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AutomationConfig cfg = ReadConfigFromUI();
        AppendLog($"[Verification Lab] Running 10-click background PostMessage burst for [{scenario.ScenarioName}] (Delay={delayMs}ms)...");

        var (sent, succeeded) = await _engine.RunVerificationBurstAsync(_selectedTarget, cfg, clickCount: 10, delayBeforeStartMs: delayMs);
        scenario.Win32MessagesSent = sent;
        scenario.Win32MessagesSucceeded = succeeded;
        scenario.LastTestedUtc = DateTime.UtcNow;
        scenario.Outcome = succeeded > 0
            ? ScenarioObservationOutcome.Win32Delivered_InGameUnverified
            : ScenarioObservationOutcome.Win32DeliveryFailed;

        AppendLog($"[Verification Lab] {scenario.ScenarioName}: Win32 PostMessage succeeded for {succeeded}/{sent} clicks. Now record your manual observation inside the target window.");
        UpdateCompatibilityBanner();
    }

    private void ConfirmScenario1Working_Click(object sender, RoutedEventArgs e)
    {
        RecordManualScenarioOutcome(_compatReport.Scenario1Focused, working: true);
    }

    private void ConfirmScenario1Ignored_Click(object sender, RoutedEventArgs e)
    {
        RecordManualScenarioOutcome(_compatReport.Scenario1Focused, working: false);
    }

    private void ConfirmScenario2Working_Click(object sender, RoutedEventArgs e)
    {
        RecordManualScenarioOutcome(_compatReport.Scenario2UnfocusedVisible, working: true);
    }

    private void ConfirmScenario2Ignored_Click(object sender, RoutedEventArgs e)
    {
        RecordManualScenarioOutcome(_compatReport.Scenario2UnfocusedVisible, working: false);
    }

    private void ConfirmScenario3Working_Click(object sender, RoutedEventArgs e)
    {
        RecordManualScenarioOutcome(_compatReport.Scenario3Minimized, working: true);
    }

    private void ConfirmScenario3Ignored_Click(object sender, RoutedEventArgs e)
    {
        RecordManualScenarioOutcome(_compatReport.Scenario3Minimized, working: false);
    }

    private void RecordManualScenarioOutcome(ScenarioVerificationResult scenario, bool working)
    {
        if (scenario.Win32MessagesSent == 0)
        {
            scenario.Win32MessagesSent = 10;
            scenario.Win32MessagesSucceeded = 10;
        }

        scenario.Outcome = working
            ? ScenarioObservationOutcome.Win32Delivered_InGameConfirmedWorking
            : ScenarioObservationOutcome.Win32Delivered_InGameConfirmedIgnored;

        AppendLog($"[Verification Lab] Manual observation recorded for [{scenario.ScenarioName}]: {(working ? "ATTACK OBSERVED IN-GAME" : "IGNORED BY TARGET ENGINE")}.");
        UpdateCompatibilityBanner();
    }

    private void CopyLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(LogOutputBox.Text);
        }
        catch
        {
            // Ignore clipboard lock
        }
    }

    private void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        LogOutputBox.Clear();
    }

    private void AppendLogThreadSafe(string line)
    {
        Dispatcher.BeginInvoke(() => AppendLog(line));
    }

    private void AppendLog(string line)
    {
        LogOutputBox.AppendText(line + Environment.NewLine);
        LogOutputBox.ScrollToEnd();
    }

    private static int ParsePositiveInt(string? raw, int fallback, int min, int max)
    {
        if (int.TryParse(raw?.Trim(), out int val))
        {
            return Math.Clamp(val, min, max);
        }
        return fallback;
    }

    private static long ParsePositiveLong(string? raw, long fallback, long min, long max)
    {
        if (long.TryParse(raw?.Trim(), out long val))
        {
            return Math.Clamp(val, min, max);
        }
        return fallback;
    }
}

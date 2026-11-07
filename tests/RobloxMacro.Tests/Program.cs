using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using RobloxMacro.Core.Models;
using RobloxMacro.Core.Native;
using RobloxMacro.Core.Services;

namespace RobloxMacro.Tests;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("================================================================================");
        Console.WriteLine("  Phase 4: Automated Test Suite — RobloxMacro.Core & Win32 Input Engine");
        Console.WriteLine("================================================================================");
        Console.WriteLine();

        RunTest("1. Roblox Process Name Detection (RobloxPlayerBeta & UWP)", Test_RobloxProcessNameDetection);
        RunTest("2. Coordinate Resolution: Client Center & Proportional Resize Scaling", Test_CoordinateResolution_CenterAndScaling);
        RunTest("3. Coordinate Resolution: Safe Client Bounds Clamping", Test_CoordinateResolution_BoundsClamping);
        RunTest("4. Background Compatibility Report: Honest Default & Manual Confirmation", Test_BackgroundCompatibilityReport_HonestyDefaults);
        RunTest("5. Win32 Background Message Delivery Across All 3 Scenarios + Focus Gate", Test_Win32MessageDelivery_AllThreeScenarios);
        RunTest("6. Foreground Focus Guard: Blocks Unfocused & Minimized Clicks (Zero Leakage)", Test_ForegroundFocusGuard_BlocksUnfocusedAndMinimized);
        RunTest("7. Safety Watchdog: Releases Held Mouse Buttons on Stop/Pause", Test_SafetyWatchdog_ReleasesHeldInputs);
        RunTest("8. Automation Engine: Click Limit Enforcement & Immediate Cancellation", Test_AutomationEngine_ClickLimitAndStop);
        RunTest("9. Macro Recorder & Player: Event Capture, JSON Persistence & Replay", Test_MacroRecorder_SaveLoadAndReplay);

        Console.WriteLine();
        Console.WriteLine("================================================================================");
        Console.WriteLine($"  TEST SUITE SUMMARY: {_passed} Passed, {_failed} Failed (Total: {_passed + _failed})");
        Console.WriteLine("================================================================================");

        return _failed == 0 ? 0 : 1;
    }

    private static void RunTest(string name, Action testBody)
    {
        Console.Write($"  [RUN ] {name} ... ");
        var sw = Stopwatch.StartNew();
        try
        {
            testBody();
            sw.Stop();
            _passed++;
            Console.WriteLine($"PASS ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            sw.Stop();
            _failed++;
            Console.WriteLine($"FAIL ({sw.ElapsedMilliseconds} ms)");
            Console.WriteLine($"         Reason: {ex.Message}");
        }
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!Equals(expected, actual))
            throw new InvalidOperationException($"{message} (Expected: {expected}, Actual: {actual})");
    }

    private static void Test_RobloxProcessNameDetection()
    {
        AssertTrue(TargetWindowService.IsRobloxProcessName("RobloxPlayerBeta"), "Should detect RobloxPlayerBeta");
        AssertTrue(TargetWindowService.IsRobloxProcessName("RobloxPlayerBeta.exe"), "Should detect RobloxPlayerBeta.exe");
        AssertTrue(TargetWindowService.IsRobloxProcessName("Windows10Universal"), "Should detect Windows10Universal (UWP Roblox)");
        AssertTrue(!TargetWindowService.IsRobloxProcessName("notepad"), "Should reject notepad");
        AssertTrue(!TargetWindowService.IsRobloxProcessName(""), "Should reject empty process name");
    }

    private static void Test_CoordinateResolution_CenterAndScaling()
    {
        var target = new TargetWindowInfo
        {
            ClientWidth = 1600,
            ClientHeight = 900,
            ScreenClientLeft = 100,
            ScreenClientTop = 50
        };

        var cfgCenter = new AutomationConfig
        {
            PositionMode = ClickPositionMode.ClientCenter
        };

        var (cx, cy, sx, sy) = TargetWindowService.ResolveClickCoordinates(target, cfgCenter);
        AssertEqual(800, cx, "Center X should be 800");
        AssertEqual(450, cy, "Center Y should be 450");
        AssertEqual(900, sx, "Screen X should be 100 + 800");
        AssertEqual(500, sy, "Screen Y should be 50 + 450");

        var cfgScaled = new AutomationConfig
        {
            PositionMode = ClickPositionMode.FixedClientPosition,
            TargetClientX = 200,
            TargetClientY = 150,
            ReferenceClientWidth = 800,
            ReferenceClientHeight = 450,
            ScaleCoordinatesOnResize = true
        };

        var (scx, scy, _, _) = TargetWindowService.ResolveClickCoordinates(target, cfgScaled);
        AssertEqual(400, scx, "Scaled X should double when width doubles from 800 to 1600");
        AssertEqual(300, scy, "Scaled Y should double when height doubles from 450 to 900");
    }

    private static void Test_CoordinateResolution_BoundsClamping()
    {
        var target = new TargetWindowInfo
        {
            ClientWidth = 400,
            ClientHeight = 300
        };

        var cfgOut = new AutomationConfig
        {
            PositionMode = ClickPositionMode.FixedClientPosition,
            TargetClientX = 9999,
            TargetClientY = -50,
            ScaleCoordinatesOnResize = false
        };

        var (cx, cy, _, _) = TargetWindowService.ResolveClickCoordinates(target, cfgOut);
        AssertEqual(398, cx, "Out-of-bounds X should clamp to ClientWidth - 2");
        AssertEqual(2, cy, "Negative Y should clamp to 2");
    }

    private static void Test_BackgroundCompatibilityReport_HonestyDefaults()
    {
        var report = new BackgroundCompatibilityReport();
        AssertTrue(!report.IsBackgroundVerifiedWorking, "Default report must NOT claim background compatibility is verified");
        AssertTrue(report.HeadlineStatus.Contains("Unsupported", StringComparison.OrdinalIgnoreCase),
            "Default headline must explicitly warn that background input is unsupported in Roblox");

        report.Scenario2UnfocusedVisible.Outcome = ScenarioObservationOutcome.Win32Delivered_InGameConfirmedIgnored;
        report.Scenario3Minimized.Outcome = ScenarioObservationOutcome.Win32Delivered_InGameConfirmedIgnored;
        AssertTrue(!report.IsBackgroundVerifiedWorking, "Confirmed ignored scenarios must keep IsBackgroundVerifiedWorking = false");

        report.Scenario2UnfocusedVisible.Outcome = ScenarioObservationOutcome.Win32Delivered_InGameConfirmedWorking;
        AssertTrue(report.IsBackgroundVerifiedWorking, "Manual confirmation should update IsBackgroundVerifiedWorking = true");
    }

    private static void Test_Win32MessageDelivery_AllThreeScenarios()
    {
        using var targetForm = new TestWin32Form("TestTarget");
        using var distractorForm = new TestWin32Form("Distractor");
        using var dispatcher = new InputDispatcher();

        targetForm.Show();
        distractorForm.Show();
        PumpMessages(80);

        var targetInfo = new TargetWindowInfo
        {
            Handle = targetForm.Handle,
            ClientWidth = targetForm.ClientSize.Width,
            ClientHeight = targetForm.ClientSize.Height
        };

        // Scenario 1: Focused & Visible
        Win32Native.ShowWindow(targetForm.Handle, Win32Native.SW_RESTORE);
        Win32Native.SetForegroundWindow(targetForm.Handle);
        targetForm.Activate();
        PumpMessages(80);
        targetForm.Reset(simulatedFocus: true);

        for (int i = 0; i < 4; i++)
        {
            var d = dispatcher.SendMouseButtonDown(targetInfo, InputDeliveryMode.BackgroundWin32Message, MacroMouseButton.Left, 120, 95, 0, 0, false);
            PumpMessages(15);
            var u = dispatcher.SendMouseButtonUp(targetInfo, InputDeliveryMode.BackgroundWin32Message, MacroMouseButton.Left, 120, 95);
            PumpMessages(15);
            AssertEqual(InputDispatchStatus.DispatchedSuccess, d.Status, "S1 MouseDown should succeed");
            AssertEqual(InputDispatchStatus.DispatchedSuccess, u.Status, "S1 MouseUp should succeed");
        }

        AssertEqual(4, targetForm.RawClicks, "Scenario 1 should receive 4 raw Win32 clicks");
        AssertEqual(4, targetForm.FocusGatedClicks, "Scenario 1 should register 4 focus-gated clicks");
        AssertEqual(120, targetForm.LastX, "Scenario 1 X coordinate should match");
        AssertEqual(95, targetForm.LastY, "Scenario 1 Y coordinate should match");

        // Scenario 2: Visible, Unfocused
        Win32Native.SetForegroundWindow(distractorForm.Handle);
        distractorForm.Activate();
        PumpMessages(80);
        targetForm.Reset(simulatedFocus: false);

        for (int i = 0; i < 4; i++)
        {
            dispatcher.SendMouseButtonDown(targetInfo, InputDeliveryMode.BackgroundWin32Message, MacroMouseButton.Left, 140, 105, 0, 0, false);
            PumpMessages(15);
            dispatcher.SendMouseButtonUp(targetInfo, InputDeliveryMode.BackgroundWin32Message, MacroMouseButton.Left, 140, 105);
            PumpMessages(15);
        }

        AssertEqual(4, targetForm.RawClicks, "Scenario 2 should still receive 4 raw Win32 clicks in background");
        AssertEqual(0, targetForm.FocusGatedClicks, "Scenario 2 focus-gated engine must reject unfocused clicks");
        AssertEqual(0, distractorForm.RawClicks, "Distractor window must receive 0 stray clicks");

        // Scenario 3: Minimized
        Win32Native.ShowWindow(targetForm.Handle, Win32Native.SW_MINIMIZE);
        PumpMessages(100);
        AssertTrue(Win32Native.IsIconic(targetForm.Handle), "Target window should be minimized (IsIconic)");
        targetForm.Reset(simulatedFocus: false);

        for (int i = 0; i < 4; i++)
        {
            dispatcher.SendMouseButtonDown(targetInfo, InputDeliveryMode.BackgroundWin32Message, MacroMouseButton.Left, 140, 105, 0, 0, false);
            PumpMessages(15);
            dispatcher.SendMouseButtonUp(targetInfo, InputDeliveryMode.BackgroundWin32Message, MacroMouseButton.Left, 140, 105);
            PumpMessages(15);
        }

        AssertEqual(4, targetForm.RawClicks, "Scenario 3 should receive 4 raw Win32 clicks even while minimized");
        AssertEqual(0, targetForm.FocusGatedClicks, "Scenario 3 focus-gated engine must reject minimized clicks");
    }

    private static void Test_ForegroundFocusGuard_BlocksUnfocusedAndMinimized()
    {
        using var targetForm = new TestWin32Form("GuardedTarget");
        using var distractorForm = new TestWin32Form("ActiveUserApp");
        using var dispatcher = new InputDispatcher();

        targetForm.Show();
        distractorForm.Show();
        Win32Native.ShowWindow(targetForm.Handle, Win32Native.SW_MINIMIZE);
        Win32Native.SetForegroundWindow(distractorForm.Handle);
        distractorForm.Activate();
        PumpMessages(100);

        var targetInfo = new TargetWindowInfo
        {
            Handle = targetForm.Handle,
            ClientWidth = 400,
            ClientHeight = 300
        };

        var res = dispatcher.SendMouseButtonDown(
            targetInfo,
            InputDeliveryMode.ForegroundGuardedSendInput,
            MacroMouseButton.Left,
            200,
            150,
            200,
            150,
            moveCursorInForeground: false);

        AssertEqual(InputDispatchStatus.BlockedByFocusGuard, res.Status,
            "ForegroundGuardedSendInput must block clicks when target is minimized or unfocused");
        AssertEqual(0, distractorForm.RawClicks, "Active user app must never receive a blocked click");
    }

    private static void Test_SafetyWatchdog_ReleasesHeldInputs()
    {
        using var targetForm = new TestWin32Form("HoldTarget");
        using var dispatcher = new InputDispatcher();

        targetForm.Show();
        PumpMessages(50);

        var targetInfo = new TargetWindowInfo
        {
            Handle = targetForm.Handle,
            ClientWidth = 400,
            ClientHeight = 300
        };

        // Press left mouse button down without releasing
        dispatcher.SendMouseButtonDown(targetInfo, InputDeliveryMode.BackgroundWin32Message, MacroMouseButton.Left, 100, 100, 0, 0, false);
        PumpMessages(30);
        AssertTrue(targetForm.IsLeftButtonCurrentlyDown, "Target should see left button held down");
        AssertEqual(0, targetForm.RawClicks, "Click is not complete until button up");

        // Now call ReleaseAllHeldInputs()
        dispatcher.ReleaseAllHeldInputs();
        PumpMessages(30);

        AssertTrue(!targetForm.IsLeftButtonCurrentlyDown, "ReleaseAllHeldInputs must release held left button");
        AssertEqual(1, targetForm.RawClicks, "Target should have received the matching WM_LBUTTONUP");
    }

    private static void Test_AutomationEngine_ClickLimitAndStop()
    {
        using var targetForm = new TestWin32Form("EngineTarget");
        targetForm.Show();
        PumpMessages(50);

        var winService = new TargetWindowService();
        using var dispatcher = new InputDispatcher();
        using var engine = new AutomationEngine(winService, dispatcher);

        var targetInfo = new TargetWindowInfo
        {
            Handle = targetForm.Handle,
            ProcessId = (uint)Environment.ProcessId,
            ProcessName = "RobloxMacro.Tests",
            WindowTitle = targetForm.Text,
            ClientWidth = 400,
            ClientHeight = 300,
            FocusState = WindowFocusState.FocusedAndVisible
        };

        var config = new AutomationConfig
        {
            DeliveryMode = InputDeliveryMode.BackgroundWin32Message,
            ActionMode = ClickActionMode.RepeatedLeftClick,
            PositionMode = ClickPositionMode.FixedClientPosition,
            TargetClientX = 180,
            TargetClientY = 130,
            ClickIntervalMs = 15,
            ClickHoldDurationMs = 10,
            StartDelayMs = 0,
            EnableClickLimit = true,
            MaxClickCount = 5
        };

        engine.StartClickAutomation(targetInfo, config);

        var sw = Stopwatch.StartNew();
        while (engine.IsRunning && sw.ElapsedMilliseconds < 3000)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
        PumpMessages(60);

        AssertEqual(5L, engine.DispatchedClickCount, "Engine should stop at exact MaxClickCount = 5");
        AssertEqual(5, targetForm.RawClicks, "Target window should have received all 5 clicks");
        AssertEqual(AutomationRunState.Stopped, engine.CurrentState, "Engine state should be Stopped");
    }

    private static void Test_MacroRecorder_SaveLoadAndReplay()
    {
        using var targetForm = new TestWin32Form("MacroReplayTarget");
        targetForm.Show();
        PumpMessages(50);

        var winService = new TargetWindowService();
        using var recorder = new MacroRecorderService(winService);

        recorder.AddManualClickEvent(110, 85, delayBeforeMs: 10, holdDurationMs: 15, refWidth: 400, refHeight: 300);
        recorder.AddManualClickEvent(220, 170, delayBeforeMs: 10, holdDurationMs: 15, refWidth: 400, refHeight: 300);
        recorder.AddManualKeyEvent(0x20, "Space", isKeyDown: true, delayBeforeMs: 10);
        recorder.AddManualKeyEvent(0x20, "Space", isKeyDown: false, delayBeforeMs: 10);

        MacroProfile built = recorder.BuildProfile("UnitTestProfile", scaleOnResize: true, loopIndefinitely: false, loopCount: 2);
        string tempFile = Path.Combine(Path.GetTempPath(), $"RobloxMacroTest_{Guid.NewGuid():N}.json");

        try
        {
            MacroRecorderService.SaveProfileToFile(built, tempFile);
            AssertTrue(File.Exists(tempFile), "Saved macro JSON file should exist");

            MacroProfile loaded = MacroRecorderService.LoadProfileFromFile(tempFile);
            AssertEqual("UnitTestProfile", loaded.Name, "Loaded profile name should match");
            AssertEqual(4, loaded.Events.Count, "Loaded profile should contain 4 events");
            AssertEqual(2, loaded.LoopCount, "Loaded profile LoopCount should be 2");

            using var dispatcher = new InputDispatcher();
            using var engine = new AutomationEngine(winService, dispatcher);

            var targetInfo = new TargetWindowInfo
            {
                Handle = targetForm.Handle,
                ProcessId = (uint)Environment.ProcessId,
                ClientWidth = 400,
                ClientHeight = 300,
                FocusState = WindowFocusState.FocusedAndVisible
            };

            var cfg = new AutomationConfig
            {
                DeliveryMode = InputDeliveryMode.BackgroundWin32Message,
                StartDelayMs = 0
            };

            engine.StartMacroPlayback(targetInfo, loaded, cfg);
            var sw = Stopwatch.StartNew();
            while (engine.IsRunning && sw.ElapsedMilliseconds < 3000)
            {
                Application.DoEvents();
                Thread.Sleep(5);
            }
            PumpMessages(60);

            // 2 clicks per loop * 2 loops = 4 clicks
            AssertEqual(4L, engine.DispatchedClickCount, "Macro playback should execute 2 clicks * 2 loops = 4 clicks");
            AssertEqual(4, targetForm.RawClicks, "Target window should receive 4 clicks from macro playback");
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    private static void PumpMessages(int durationMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < durationMs)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    private sealed class TestWin32Form : Form
    {
        private bool _simulatedFocus;
        public bool IsLeftButtonCurrentlyDown { get; private set; }
        public int RawClicks { get; private set; }
        public int FocusGatedClicks { get; private set; }
        public int LastX { get; private set; }
        public int LastY { get; private set; }

        public TestWin32Form(string title)
        {
            Text = title;
            Size = new Size(400, 300);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(120, 120);
        }

        public void Reset(bool simulatedFocus)
        {
            _simulatedFocus = simulatedFocus;
            IsLeftButtonCurrentlyDown = false;
            RawClicks = 0;
            FocusGatedClicks = 0;
            LastX = 0;
            LastY = 0;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Win32Native.WM_LBUTTONDOWN)
            {
                IsLeftButtonCurrentlyDown = true;
                LastX = Win32Native.GetXLParam(m.LParam);
                LastY = Win32Native.GetYLParam(m.LParam);
            }
            else if (m.Msg == Win32Native.WM_LBUTTONUP && IsLeftButtonCurrentlyDown)
            {
                IsLeftButtonCurrentlyDown = false;
                LastX = Win32Native.GetXLParam(m.LParam);
                LastY = Win32Native.GetYLParam(m.LParam);
                RawClicks++;

                if (_simulatedFocus && !Win32Native.IsIconic(Handle))
                {
                    FocusGatedClicks++;
                }
            }

            base.WndProc(ref m);
        }
    }
}

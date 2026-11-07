using System;

namespace RobloxMacro.Core.Models;

public enum ScenarioObservationOutcome
{
    NotTestedYet,
    Win32Delivered_InGameUnverified,
    Win32Delivered_InGameConfirmedWorking,
    Win32Delivered_InGameConfirmedIgnored,
    Win32DeliveryFailed
}

public sealed class ScenarioVerificationResult
{
    public string ScenarioName { get; set; } = string.Empty;
    public int Win32MessagesSent { get; set; }
    public int Win32MessagesSucceeded { get; set; }
    public ScenarioObservationOutcome Outcome { get; set; } = ScenarioObservationOutcome.NotTestedYet;
    public DateTime? LastTestedUtc { get; set; }

    public string SummaryText => Outcome switch
    {
        ScenarioObservationOutcome.NotTestedYet => "Not yet tested against live target",
        ScenarioObservationOutcome.Win32Delivered_InGameUnverified =>
            $"Win32 API Delivered ({Win32MessagesSucceeded}/{Win32MessagesSent} TRUE) — Awaits manual in-game observation",
        ScenarioObservationOutcome.Win32Delivered_InGameConfirmedWorking =>
            $"Verified Working ({Win32MessagesSucceeded}/{Win32MessagesSent} Win32 + In-Game Attack Observed)",
        ScenarioObservationOutcome.Win32Delivered_InGameConfirmedIgnored =>
            $"Background Input Unsupported ({Win32MessagesSucceeded}/{Win32MessagesSent} Win32 Delivered, Ignored by Roblox Engine)",
        ScenarioObservationOutcome.Win32DeliveryFailed =>
            $"Win32 Delivery Failed ({Win32MessagesSucceeded}/{Win32MessagesSent} succeeded)",
        _ => "Unknown"
    };
}

public sealed class BackgroundCompatibilityReport
{
    public ScenarioVerificationResult Scenario1Focused { get; } = new()
    {
        ScenarioName = "Scenario 1: Target Focused & Visible"
    };

    public ScenarioVerificationResult Scenario2UnfocusedVisible { get; } = new()
    {
        ScenarioName = "Scenario 2: Target Visible, Another App Focused"
    };

    public ScenarioVerificationResult Scenario3Minimized { get; } = new()
    {
        ScenarioName = "Scenario 3: Target Minimized to Taskbar"
    };

    public bool IsBackgroundVerifiedWorking =>
        Scenario2UnfocusedVisible.Outcome == ScenarioObservationOutcome.Win32Delivered_InGameConfirmedWorking ||
        Scenario3Minimized.Outcome == ScenarioObservationOutcome.Win32Delivered_InGameConfirmedWorking;

    public string HeadlineStatus
    {
        get
        {
            if (IsBackgroundVerifiedWorking)
            {
                return "Background Input Verified (Manual In-Game Confirmation)";
            }

            if (Scenario2UnfocusedVisible.Outcome == ScenarioObservationOutcome.Win32Delivered_InGameConfirmedIgnored &&
                Scenario3Minimized.Outcome == ScenarioObservationOutcome.Win32Delivered_InGameConfirmedIgnored)
            {
                return "Background Input Unsupported by Target Engine (Verified — Using Safe Foreground-Guarded Mode)";
            }

            return "Background Input Unsupported in Roblox (Unverified — Using Foreground-Guarded Fallback)";
        }
    }

    public string TechnicalExplanation =>
        "Win32 PostMessage/SendMessage returns TRUE when queuing WM_LBUTTONDOWN/UP to RobloxPlayerBeta.exe, " +
        "which is why naive tools falsely claim background clicking works. However, Roblox's internal " +
        "UserInputService gates gameplay input on WindowFocused (WM_ACTIVATE / GetForegroundWindow) and " +
        "hardware Raw Input (WM_INPUT). When unfocused or minimized, Roblox discards posted mouse messages. " +
        "Foreground-Guarded Mode solves this safely by using hardware-queued SendInput only while Roblox has focus " +
        "and pausing within <10ms the instant you switch to another window.";
}

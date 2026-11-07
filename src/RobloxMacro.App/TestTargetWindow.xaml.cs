using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using RobloxMacro.Core.Native;

namespace RobloxMacro.App;

public partial class TestTargetWindow : Window
{
    private HwndSource? _hwndSource;
    private bool _leftDownPending;

    public IntPtr WindowHandle { get; private set; }
    public int RawWin32Clicks { get; private set; }
    public int FocusGatedAttacks { get; private set; }
    public int LastClientX { get; private set; }
    public int LastClientY { get; private set; }

    public TestTargetWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Closed += OnWindowClosed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        WindowHandle = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(WindowHandle);
        _hwndSource?.AddHook(WndProcHook);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _hwndSource?.RemoveHook(WndProcHook);
    }

    public void ResetCounters()
    {
        _leftDownPending = false;
        RawWin32Clicks = 0;
        FocusGatedAttacks = 0;
        RawClickCountText.Text = "0";
        FocusGatedClickCountText.Text = "0";
        LastEventText.Text = "Last Click: Reset";
        HitMarker.Visibility = Visibility.Collapsed;
    }

    private void ResetCounters_Click(object sender, RoutedEventArgs e)
    {
        ResetCounters();
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        uint uMsg = (uint)msg;
        if (uMsg == Win32Native.WM_LBUTTONDOWN)
        {
            _leftDownPending = true;
            LastClientX = Win32Native.GetXLParam(lParam);
            LastClientY = Win32Native.GetYLParam(lParam);
        }
        else if (uMsg == Win32Native.WM_LBUTTONUP && _leftDownPending)
        {
            _leftDownPending = false;
            LastClientX = Win32Native.GetXLParam(lParam);
            LastClientY = Win32Native.GetYLParam(lParam);
            RawWin32Clicks++;

            bool isMinimized = Win32Native.IsIconic(hwnd);
            bool isForeground = (Win32Native.GetForegroundWindow() == hwnd) || IsActive;
            bool acceptedByFocusGate = !isMinimized && isForeground;
            if (acceptedByFocusGate)
            {
                FocusGatedAttacks++;
            }

            Dispatcher.BeginInvoke(() =>
            {
                RawClickCountText.Text = RawWin32Clicks.ToString("N0");
                FocusGatedClickCountText.Text = FocusGatedAttacks.ToString("N0");
                string stateLabel = isMinimized
                    ? "MINIMIZED"
                    : (isForeground ? "FOCUSED" : "UNFOCUSED (Background)");
                WindowStateOverlayText.Text =
                    $"State at Click: {stateLabel} |FocusGate={(acceptedByFocusGate ? "ACCEPTED" : "DROPPED (Unfocused/Minimized)")}";
                LastEventText.Text =
                    $"Last Click @ Client ({LastClientX}, {LastClientY}) [{stateLabel}]";

                double markerX = Math.Clamp(LastClientX - 9, 0, Math.Max(10, HitCanvas.ActualWidth - 18));
                double markerY = Math.Clamp(LastClientY - 9, 0, Math.Max(10, HitCanvas.ActualHeight - 18));
                Canvas.SetLeft(HitMarker, markerX);
                Canvas.SetTop(HitMarker, markerY);
                HitMarker.Visibility = Visibility.Visible;
            });
        }

        return IntPtr.Zero;
    }
}

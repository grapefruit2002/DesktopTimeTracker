# Sends a tiny relative mouse move so GetLastInputInfo reports fresh activity.
# Used only for automated end-to-end verification of the idle/active logic.
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class InputSim {
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    public static void Nudge() {
        mouse_event(0x0001, 2, 0, 0, IntPtr.Zero);
        mouse_event(0x0001, unchecked((uint)-2), 0, 0, IntPtr.Zero);
    }
}
"@
[InputSim]::Nudge()
Write-Output "input nudged; foreground hwnd = $([InputSim]::GetForegroundWindow())"
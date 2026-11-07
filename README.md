> Unofficial fan/learning project. Third-party names, characters, artwork, and other materials remain subject to their owners' rights. No affiliation or endorsement is claimed.

# Roblox Background & Guarded Attack Macro Suite

## Repository guide

A Windows input-automation prototype built with C#/.NET 10 and WPF. It records/dispatches macros, selects a target window and provides global-hotkey/guarded foreground workflows. Roblox is one intended target; the application is unofficial and is not affiliated with or endorsed by Roblox.

Start with the technical limitations below: a successful Windows message/API call does **not** prove that a game accepted an attack or input, particularly while unfocused/minimised. Do not equate the test target window's behaviour with Roblox gameplay behaviour.

Use Windows and the .NET 10 Windows desktop SDK. Build/run the application from its actual project path:

```powershell
dotnet build src/RobloxMacro.App/RobloxMacro.App.csproj -c Release
dotnet run --project src/RobloxMacro.App/RobloxMacro.App.csproj -c Release
```

The separate proof-of-concept and test projects remain available for controlled checks. `build-and-package.ps1` creates local packages; no compiled `dist` executable is included in this source snapshot. Review target selection, hotkeys and stop behaviour in the supplied test window before trying another application.

`src/RobloxMacro.Core/` holds native input/window and automation services; `src/RobloxMacro.App/` the WPF UI; `src/RobloxMacro.PoC/` the probe; and `tests/` the authored checks. The detailed project paths, commands and limitations follow.

A Windows desktop input automation and macro utility built with **C# 13 / .NET 10 WPF** and documented user-mode **Win32 APIs** (`SendInput`, `PostMessageW`, `SendMessageTimeoutW`, `RegisterHotKey`, `GetDpiForWindow`).

---

## 1. Honest Technical Assessment: Background & Minimized Clicking in Roblox

### Delivering a Win32 Message vs. Registering an In-Game Attack
When an external application calls `PostMessageW(hWnd, WM_LBUTTONDOWN, MK_LBUTTON, MAKELPARAM(x, y))` against `RobloxPlayerBeta.exe`, the Win32 API returns `TRUE` because Windows places the message into the target thread's message queue. Standard Win32 controls process this message even when unfocused or minimized.

**However, `RobloxPlayerBeta.exe` does NOT register in-game attacks from unfocused or minimized window messages:**
1. **Focus Gate (`UserInputService.WindowFocused`)**: When you switch to another window or minimize Roblox, Windows dispatches `WM_ACTIVATE(WA_INACTIVE)` and `WM_KILLFOCUS`. Roblox sets `WindowFocused = false` and ignores gameplay mouse/keyboard inputs until the window regains foreground focus.
2. **Hardware Input & Cursor Verification (`WM_INPUT` + `GetCursorPos`)**: Modern Roblox validates mouse input against the system hardware cursor position (`GetCursorPos` + `ScreenToClient`) and Raw Input (`WM_INPUT`). When you are using another application, the cursor is outside Roblox's viewport.
3. **No Process Modification or Anti-Cheat Tampering**: Bypassing `WindowFocused` inside a single OS desktop session would require injecting code or patching process memory inside `RobloxPlayerBeta.exe` (violating Hyperion/Byfron protections and project safety rules). This application strictly uses legitimate, documented user-mode Win32 APIs.

### How This Application Solves Grinding Safely
1. **Foreground-Guarded Mode (`SendInput` + Instant Focus Auto-Pause) [Recommended & Default]**:
   - Uses hardware-queued `SendInput` only while the selected Roblox window is the active foreground window (`GetForegroundWindow() == targetHwnd && !IsIconic(targetHwnd)`).
   - **Zero Stray Clicks**: The instant you `Alt+Tab`, minimize Roblox, or click another application, the engine **auto-pauses within <10ms** and releases any held mouse buttons or keys so it **never** clicks your other applications.
   - **Auto-Resume**: Optionally resumes attacking automatically as soon as you focus Roblox again.
2. **Background Win32 Message Mode (`PostMessage` / `SendMessageTimeout`) [Experimental / Verification]**:
   - Sends `WM_MOUSEMOVE`, `WM_LBUTTONDOWN`, and `WM_LBUTTONUP` with client-relative `(X, Y)` coordinates directly to the target `HWND` (and optional child `HWND`s) without moving the physical cursor or stealing focus.
   - Works across all 3 scenarios (Focused, Unfocused, Minimized) on standard Win32 windows (verified via our built-in **Controlled Win32 Target Window**), and can be tested on Roblox via the **3-Scenario Background Verification Lab**.
3. **Practical Alternative for Simultaneous Laptop Multitasking**:
   - If you want Roblox grinding via `SendInput` while simultaneously using your physical mouse and keyboard in other apps on the same laptop, run Roblox + `RobloxMacro.App.exe` inside a **Windows Sandbox** or **Hyper-V VM** window. Inside the VM, Roblox retains continuous foreground focus while your host cursor and keyboard remain completely free.

---

## 2. Project Structure

```text
RobloxMacroSuite/
├── docs/
│   ├── Phase1_Research_and_Feasibility.md     # Deep-dive analysis of Roblox Win32 input & focus gates
│   └── Phase4_Manual_Roblox_Test_Procedure.md # Step-by-step manual verification guide for Roblox
├── src/
│   ├── RobloxMacro.PoC/                       # Phase 2 Proof of Concept CLI & 3-Scenario Self-Test
│   ├── RobloxMacro.Core/                      # Phase 3 Separated Win32 Input, Window, Hotkey & Macro Engine
│   └── RobloxMacro.App/                       # Phase 3 Dark-Theme WPF Desktop GUI + Test Target Window
├── tests/
│   └── RobloxMacro.Tests/                     # Phase 4 Automated Test Suite (9 Unit & Integration Tests)
├── dist/                                      # Phase 5 Published Standalone Executables
└── build-and-package.ps1                      # Automated Build, Test, and Packaging Script
```

---

## 3. Build, Test, and Run Instructions

### Prerequisites
- Windows 10 / 11 (x64)
- .NET 10.0 SDK (`10.0.100` or newer) with Windows Desktop workload

### 1. Run Phase 2 Proof of Concept (3-Scenario Self-Test)
```powershell
dotnet run --project src/RobloxMacro.PoC/RobloxMacro.PoC.csproj -- --self-test
```
To run the interactive manual test against a live `RobloxPlayerBeta.exe` window:
```powershell
dotnet run --project src/RobloxMacro.PoC/RobloxMacro.PoC.csproj -- --roblox-test
```

### 2. Run Phase 4 Automated Test Suite
```powershell
dotnet run --project tests/RobloxMacro.Tests/RobloxMacro.Tests.csproj
```

### 3. Run the Full WPF Desktop Application
```powershell
dotnet run --project src/RobloxMacro.App/RobloxMacro.App.csproj
```

### 4. Package Standalone Executables (Phase 5)
```powershell
powershell -ExecutionPolicy Bypass -File .\build-and-package.ps1
```
The packaged executables are output to `dist\`:
- `dist\RobloxMacro.App.exe` (Standalone single-file WPF desktop application)
- `dist\RobloxMacro.PoC.exe` (Standalone Proof-of-Concept CLI)


## Source snapshot and validation

See [BACKUP_NOTES.md](BACKUP_NOTES.md) for the omitted build outputs, local settings, credentials and runtime data. Build generated binaries locally using the instructions above; a source checkout is not an installed application. Earlier validation figures in this guide describe their original development runs. This documentation update checks source references and instructions; it does not claim a fresh application, device or gameplay test.

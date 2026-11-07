# Phase 1: Research and Feasibility — Roblox Mouse Input Architecture & Windows Background Automation

## 1. Executive Summary

A common misconception in Windows desktop automation is that if `PostMessage(hWnd, WM_LBUTTONDOWN, ...)` returns `TRUE` (non-zero), the target application "received and processed a mouse click." While this is true for standard Win32 GUI controls (such as Win32 `BUTTON` controls, WinForms, or standard WPF message hooks that do not gate on window activation), **modern 3D game engines—and specifically `RobloxPlayerBeta.exe`—do not process gameplay mouse attacks from unfocused or minimized window messages.**

Without injecting code into the Roblox process, hooking internal functions, or tampering with Hyperion (Byfron) anti-tamper protections—actions that are explicitly out of scope and unsafe—**genuine background or minimized attacking inside a single Windows desktop session is blocked by Roblox's internal input subsystem.**

This document details:
1. How Roblox processes mouse input on Windows.
2. How each documented user-mode Win32 input API behaves across Focused, Unfocused-Visible, and Minimized states.
3. The exact boundary between **programmatically verifiable Win32 message delivery** and **manual observation of in-game attack registration**.
4. How our Proof of Concept (Phase 2) and Full Application (Phase 3) test and handle these constraints honestly and safely.

---

## 2. How Roblox (`RobloxPlayerBeta.exe`) Receives Mouse Input on Windows

### 2.1 Window Architecture
The desktop Windows client (`RobloxPlayerBeta.exe`) creates a single primary top-level Win32 window:
- **Window Class**: `WINDOWSCLIENT`
- **Window Title**: `Roblox`
- **Rendering Subsystem**: Direct3D 11 / Vulkan swapchain bound directly to the `WINDOWSCLIENT` `HWND` (no separate child `HWND` controls represent 3D world geometry or in-game Lua `GuiObject` elements).

### 2.2 The Three Layers of Roblox Input Processing
Inside `RobloxPlayerBeta.exe`, mouse input passes through three distinct gates before a Lua script receives `Tool.Activated`, `UserInputService.InputBegan`, or `Mouse.Button1Down`:

1. **Window Focus Gate (`WM_ACTIVATE` / `WM_SETFOCUS` / `GetForegroundWindow`)**
   - Roblox tracks window focus state via `WM_ACTIVATE` (`WA_ACTIVE` vs `WA_INACTIVE`), `WM_SETFOCUS` / `WM_KILLFOCUS`, and `GetForegroundWindow()`.
   - When the user switches to another application, Windows sends `WM_ACTIVATE(WA_INACTIVE)` and `WM_KILLFOCUS` to the Roblox `HWND`.
   - Roblox immediately fires `UserInputService.WindowFocusReleased` and sets `UserInputService.WindowFocused = false`.
   - While `WindowFocused == false`, Roblox's input dispatcher **explicitly ignores gameplay mouse and keyboard events** (and releases any currently held inputs) so that a user typing or clicking in another window does not accidentally fire weapons or move their character.

2. **Hardware Input Queue vs. Posted Window Messages (`WM_INPUT` + `GetCursorPos` vs. `WM_LBUTTONDOWN`)**
   - Modern Roblox uses **Raw Input** (`RegisterRawInputDevices` with `HID_USAGE_PAGE_GENERIC` / `HID_USAGE_GENERIC_MOUSE`, processing `WM_INPUT`) alongside standard Win32 mouse messages (`WM_LBUTTONDOWN` `0x0201`, `WM_LBUTTONUP` `0x0202`, `WM_MOUSEMOVE` `0x0200`).
   - Crucially, when evaluating mouse clicks, Roblox does **not** rely solely on the client-relative `(X, Y)` coordinates packed into the `lParam` of `WM_LBUTTONDOWN`. It cross-references the system hardware cursor position via `GetCursorPos(&pt)` + `ScreenToClient(hWnd, &pt)` and verifies that the cursor is within the active client area.
   - If another application is being used, the physical cursor is located outside the Roblox window (or over a window with a higher Z-order), and `WindowFromPoint(pt)` does not resolve to the Roblox `HWND`.

3. **Minimized State (`IsIconic(hWnd)` / `SIZE_MINIMIZED`)**
   - When a Win32 window is minimized, Windows moves its window coordinates off-screen (traditionally `(-32000, -32000)`) or collapses its client area, sets the `WS_ICONIC` (`WS_MINIMIZE`) style flag, and suspends/throttles swapchain presentation.
   - Any hit-testing or viewport raycasting inside Roblox's engine during a minimized state has no active viewport focus and is discarded by the engine loop.

---

## 3. Evaluation of Documented User-Mode Win32 Input APIs

| Win32 API Mechanism | Moves Physical Cursor? | Requires Foreground Focus? | Works When Target Unfocused? | Works When Target Minimized? | Accepted by Roblox for In-Game Attacks? |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **`PostMessage(hWnd, WM_LBUTTONDOWN / UP, ...)`** | No | No (at Win32 API level) | Delivers to `WndProc` | Delivers to `WndProc` | **No when unfocused/minimized.** Even when focused, often ignored unless cursor is already inside Roblox window. |
| **`SendMessage` / `SendMessageTimeout`** | No | No (at Win32 API level) | Delivers synchronously to `WndProc` | Delivers synchronously to `WndProc` | **No when unfocused/minimized** (same engine focus gate as `PostMessage`). |
| **`PostMessage(WM_ACTIVATE, WA_ACTIVE)` + `WM_LBUTTONDOWN`** | No | Attempts to spoof activation | Causes flicker/desync; `GetForegroundWindow()` still returns false | Fails (`IsIconic` remains true) | **Unreliable / Rejected.** Modern Roblox checks actual foreground/cursor state and Hyperion monitors anomalous message floods. |
| **`SendInput` (`INPUT_MOUSE` with `MOUSEEVENTF_LEFTDOWN / UP`)** | Optional (clicks at current cursor or moves cursor) | **Yes** (targets active foreground window under cursor) | **No** (would click the user's active app if not guarded!) | **No** | **Yes (100% reliable when Roblox is focused).** Generates hardware-queued input processed identically to physical mouse clicks. |
| **UI Automation (`IUIAutomationInvokePattern`)** | No | Varies | Only works on standard Win32/WPF accessibility trees | Only on standard UI controls | **No.** Roblox renders its 3D world and Lua UI directly via Direct3D/Vulkan without exposing Win32 UI Automation invoke patterns for attacks. |

---

## 4. Delivering a Windows Message vs. Registering an Attack in Roblox

A critical technical requirement of this project is distinguishing between **Win32 API delivery** and **Roblox in-game registration**:

1. **What `PostMessage` / `SendMessage` Returning `TRUE` Actually Means**:
   - `PostMessageW(hWnd, WM_LBUTTONDOWN, MK_LBUTTON, MAKELPARAM(x, y))` returning `true` only proves that:
     - `hWnd` is a valid window handle (`IsWindow(hWnd)` is true).
     - User Interface Privilege Isolation (UIPI) allowed the calling process to post a message to the target window's message queue (i.e., the target process is not running at a higher integrity level than the macro process).
     - The target thread's message queue had space for the message.
   - It says **nothing** about what the target application's `WindowProc` did after dequeuing the message via `GetMessage` / `PeekMessage`.

2. **Why `SendMessage` Returning `0` Also Does Not Prove In-Game Execution**:
   - Under the Win32 specification, a `WindowProc` returns `0` when it processes `WM_LBUTTONDOWN`. A game engine's `WindowProc` will still return `0` after checking `if (!m_bWindowFocused) return 0;`—meaning the message was "handled" by being intentionally ignored!

---

## 5. Analysis of the Three Target Scenarios

### Scenario 1: Roblox is Focused and Visible
- **`SendInput` (Foreground-Guarded Mode)**:
  - **Result**: **Works reliably.** Because `SendInput` injects `MOUSEINPUT` structures at the OS input-stream level, Windows routes both `WM_INPUT` (Raw Input) and `WM_LBUTTONDOWN`/`WM_LBUTTONUP` to the active foreground Roblox window, triggering `Tool.Activated` and `UserInputService.InputBegan`.
- **`PostMessage` / `SendMessage` (Background Message Mode)**:
  - **Result**: **Partial / Inconsistent.** If the Roblox window is focused *and* the physical cursor is already positioned inside the Roblox client area, `PostMessage(WM_LBUTTONDOWN)` / `PostMessage(WM_LBUTTONUP)` may trigger clicks in some contexts, but if `lParam` coordinates differ from `GetCursorPos()`, Roblox uses the physical cursor position or ignores the synthetic message depending on camera mode (Shift-Lock vs Classic vs First-Person).

### Scenario 2: Roblox is Visible, Another Application Has Focus
- **`PostMessage` / `SendMessage` (Background Message Mode)**:
  - **Win32 Delivery**: Succeeds (`PostMessage` returns `TRUE`; our controlled Win32 Test Target receives and counts every click at the exact `(X, Y)` client coordinate).
  - **Roblox In-Game Registration**: **Fails.** Because `UserInputService.WindowFocused` is `false` and `GetForegroundWindow() != robloxHwnd`, Roblox discards the mouse button events before they reach Lua gameplay scripts.
- **`SendInput` (Foreground-Guarded Mode)**:
  - **Safety Behavior**: Our engine detects `GetForegroundWindow() != robloxHwnd` before every click cycle and **immediately auto-pauses** (and releases any held button), ensuring zero stray clicks ever reach the user's active application.

### Scenario 3: Roblox is Minimized
- **`PostMessage` / `SendMessage` (Background Message Mode)**:
  - **Win32 Delivery**: Succeeds (`PostMessage` returns `TRUE`; our controlled Win32 Test Target continues incrementing its background click counter even while minimized to the taskbar).
  - **Roblox In-Game Registration**: **Fails.** Roblox is both unfocused (`WindowFocused == false`) and iconic (`IsIconic(hWnd) == true`), with client area dimensions collapsed (`0x0` or off-screen).
- **`SendInput` (Foreground-Guarded Mode)**:
  - **Safety Behavior**: Detects `IsIconic(robloxHwnd) || GetForegroundWindow() != robloxHwnd` and **auto-pauses**.

---

## 6. Programmatic Verification vs. Manual In-Game Observation

### What Can Be Verified Programmatically
1. **Process & Window Lifecycle**: Whether `RobloxPlayerBeta.exe` (or `Windows10Universal.exe`) is running, its `HWND`, Window Class (`WINDOWSCLIENT`), visibility (`IsWindowVisible`), minimized status (`IsIconic`), and foreground focus (`GetForegroundWindow`).
2. **Coordinate & DPI Geometry**: Client rectangle dimensions (`GetClientRect`), client-to-screen translation (`ClientToScreen`), and per-monitor DPI scaling (`GetDpiForWindow`) across window moves and resizes.
3. **Win32 Message Queue Delivery**: Whether `PostMessage` / `SendMessageTimeout` succeeded without Win32 error codes (`ERROR_ACCESS_DENIED` / UIPI block, `ERROR_INVALID_WINDOW_HANDLE`, or timeout).
4. **End-to-End Message Receipt in a Controlled Target**: Using our dedicated Win32/WPF Test Target window, we can programmatically prove that our `PostMessage` engine delivers accurate `WM_LBUTTONDOWN` and `WM_LBUTTONUP` messages with exact `(X, Y)` coordinates across **all three scenarios** (Focused, Unfocused, Minimized), and contrast its "Standard Win32 Mode" against its "Game Engine Focus-Gated Mode".

### What Requires Manual Observation Inside Roblox
1. **In-Game Tool / Weapon Activation**: Because reading Roblox process memory or injecting hooks is prohibited, only the user observing the Roblox viewport (or in-game hit/damage counter) can confirm whether a delivered `WM_LBUTTONDOWN` caused the character's weapon/tool to swing.
2. **UI Button Clicking vs. 3D World Clicking**: Whether an in-game Lua `TextButton`/`ImageButton` responds differently from a 3D viewport `Tool.Activated` event.

---

## 7. Architectural Conclusion & Practical Alternatives

1. **Honest Status Reporting**:
   - The application must **never** claim `"Background Compatible"` merely because `PostMessage` returned `TRUE`.
   - Instead, the application defaults to flagging Roblox background input as **"Unsupported by Roblox Engine (Unverified)"**, explains the `WindowFocused` / `WM_INPUT` limitation clearly in the UI, and includes an interactive **3-Scenario Verification Wizard** so the user can test Focused, Unfocused, and Minimized behavior first-hand.
2. **Dual-Engine Implementation**:
   - **Mode 1: Foreground-Guarded Mode (`SendInput`) [Recommended & Default for Roblox]**:
     - Delivers authentic hardware-queued mouse input (`SendInput`) when Roblox is the active window.
     - Continuously monitors `GetForegroundWindow()` and `IsIconic()` before every action; if the user `Alt+Tab`s or clicks another window, the macro **instantly pauses within <10ms** and releases any held mouse button so it never clicks another application. When the user clicks back into Roblox, it can optionally auto-resume.
   - **Mode 2: Background Win32 Message Mode (`PostMessage` / `SendMessageTimeout`) [Experimental / For Standard Win32 Apps & Verification]**:
     - Sends `WM_MOUSEMOVE`, `WM_LBUTTONDOWN`, and `WM_LBUTTONUP` with packed client `(X, Y)` `lParam` directly to the target `HWND` (and any child `HWND`s) without moving the physical cursor or stealing focus.
3. **Legitimate Workaround for True Simultaneous Laptop Use + Roblox Grinding**:
   - Because `RobloxPlayerBeta.exe` enforces foreground focus at the OS session level, the only legitimate non-cheat ways to run a foreground-dependent game while using the mouse/keyboard simultaneously on the same laptop are:
     1. Running Roblox + this Macro App inside a **Windows Sandbox** or **Hyper-V Virtual Machine** (where the VM guest OS maintains constant foreground focus on Roblox and `SendInput` works continuously while the host OS mouse/keyboard remain 100% free for the user).
     2. Using **Foreground-Guarded Auto-Resume Mode** during idle/reading/video-watching periods where the macro automatically grinds whenever Roblox is focused and immediately yields the mouse the instant a hotkey is pressed or focus changes.

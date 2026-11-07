# Phase 4: Manual Roblox Test Procedure & Verification Protocol

Because `PostMessage(hWnd, WM_LBUTTONDOWN, ...)` returns `TRUE` whenever Windows places the message into `RobloxPlayerBeta.exe`'s message queue—even when Roblox's internal `UserInputService` subsequently drops the message due to `WindowFocused == false`—**only manual observation inside a live Roblox experience can confirm whether an in-game attack occurred.**

---

## 1. Prerequisites
1. Launch **Roblox** (`RobloxPlayerBeta.exe`) and join the target grinding experience.
2. Equip your weapon, tool, or combat ability so that a normal physical left-click triggers a visible swing animation, sound effect, or hit/damage counter increment.
3. Launch **`RobloxMacro.App.exe`** (from `dist/RobloxMacro.App.exe` or via `dotnet run --project src/RobloxMacro.App`).
4. Confirm that **`[ROBLOX] RobloxPlayerBeta.exe`** is selected in the **Target Window** dropdown at the top of the window.

---

## 2. Executing the 3-Scenario Background Verification Lab

Switch to **Tab 3 (`3. 3-Scenario Background Verification Lab`)** in `RobloxMacro.App`:

### Test A — Scenario 1: Roblox is Focused and Visible
1. Arrange your screen so Roblox is visible, or prepare to `Alt+Tab` into Roblox immediately.
2. Click **"Run Scenario 1 Burst (3s Delay)"** in the Scenario 1 card.
3. Within the 3-second countdown, click inside the Roblox window so it is the **Focused Foreground Window**, and leave your cursor over the 3D viewport (not over the chat box or Esc menu).
4. **Programmatic Check**: Observe the log panel confirming `Win32 PostMessage succeeded for 10/10 clicks`.
5. **Manual Observation**: Watch your character in Roblox:
   - Did your character swing/attack 10 times?
   - Click **"✓ Attack Observed"** or **"✕ Ignored in Game"** on the Scenario 1 card.

### Test B — Scenario 2: Roblox is Visible, Another Application Has Focus
1. Place the Roblox window and the `RobloxMacro.App` window side-by-side so **both are visible on screen**, with `RobloxMacro.App` (or Notepad/Browser) holding foreground focus.
2. Verify the Target Status badge in `RobloxMacro.App` reads **`VISIBLE (UNFOCUSED)`**.
3. Click **"Run Scenario 2 Burst (Immediate)"** in the Scenario 2 card. Keep focus on `RobloxMacro.App` while the 10 background `PostMessage` clicks are sent.
4. **Programmatic Check**: Confirm the log shows `Win32 PostMessage succeeded for 10/10 clicks` and your physical mouse cursor did not move.
5. **Manual Observation**: Watch the visible Roblox window during the burst:
   - In standard `RobloxPlayerBeta.exe`, because `UserInputService.WindowFocused` is `false`, your character will **not** attack despite `PostMessage` returning `TRUE` 10/10 times.
   - Click **"✕ Ignored in Game"** (or **"✓ Attack Observed"** if a custom child control responded).

### Test C — Scenario 3: Roblox is Minimized
1. Minimize the Roblox window to the Windows Taskbar (`IsIconic == true`).
2. Verify the Target Status badge in `RobloxMacro.App` reads **`MINIMIZED (ICONIC)`**.
3. Click **"Run Scenario 3 Burst (Immediate)"** in the Scenario 3 card.
4. Wait 2 seconds for the 10-click burst to complete, then restore the Roblox window.
5. **Programmatic Check**: Confirm the log shows `Win32 PostMessage succeeded for 10/10 clicks`.
6. **Manual Observation**: Check your in-game hit counter, resource/XP bar, or enemy health:
   - Confirm whether any attacks registered while minimized, and record the outcome on the Scenario 3 card.

---

## 3. Verifying Foreground-Guarded Mode (Safe Fallback Mode)

1. Switch to **Tab 1 (`1. Attack Clicker & Input Mode`)**.
2. Select **`Foreground-Guarded Mode (SendInput + Automatic Focus Pause) [Recommended]`**.
3. Set **Click Interval** to `100` ms, **Hold Duration** to `35` ms, and **Target Click Position** to `Center of Target Client Area` (or press `F8` while hovering over your desired click spot inside Roblox to lock `Fixed Client-Relative Coordinates`).
4. Press **`F6`** (or click **START ATTACK AUTOMATION**).
5. Focus the Roblox window:
   - **Expected**: Your character immediately begins attacking repeatedly at the configured interval.
6. Press `Alt+Tab` or click into another window (e.g. a browser or text editor):
   - **Expected**: Within `<10ms`, `ENGINE STATE` switches to **`AUTO-PAUSED (FOCUS)`**, any held mouse button is released, and **zero clicks** are sent to your browser or text editor.
7. Click back into the Roblox window:
   - **Expected**: If `Automatically resume clicking when target window regains focus` is checked, attacking resumes automatically.
8. Press **`F7`** (Emergency Stop) at any time:
   - **Expected**: Automation halts immediately and releases all simulated inputs.

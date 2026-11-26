<!-- https://padforge.org/docs/troubleshooting/ as PadForge's skill carries it, from padforge.org's wiki/troubleshooting.md. -->
# Troubleshooting

*Common problems and how to fix them. Something not working, on Windows on ARM or anywhere else? Tell us right away: start a [Q&A discussion](https://github.com/hifihedgehog/PadForge/discussions/new?category=q-a) with a [problem recording](features/problem-recorder.md) or the file from **Save a Snapshot…** ([Logs and diagnostics](#logs-and-diagnostics)), or ask on [Discord](https://discord.gg/qawTZHVhNH).*

![Settings page where most troubleshooting starts (drivers, polling rate, language)](https://padforge.org/docs/images/settings.png)

Related pages: [Installation](https://padforge.org/docs/start/installation/), [Settings](features/settings.md), [Driver Management](features/driver-management.md), [Devices](features/devices.md).

<figure class="pf-video">
<video controls playsinline preload="none" width="1920" height="1080"
       poster="/assets/video/padforge-tutorial-double-input-and-no-controller-poster.jpg"
       aria-label="Video: fixing double input, or a game that sees no controller, recorded live in PadForge 5.2.0">
<source src="https://padforge.org/assets/video/padforge-tutorial-double-input-and-no-controller.mp4" type="video/mp4">
<track kind="captions" srclang="en" label="English" src="https://padforge.org/assets/video/padforge-tutorial-double-input-and-no-controller.vtt" default>
</video>
<figcaption>Double input, a game that sees no controller, and an empty Devices page, recorded live in PadForge 5.2.0 in 1 minute 20 seconds, before PadForge 6 rebuilt the interface. The narration is a generated voice, with English captions. <a href="https://padforge.org/assets/video/padforge-tutorial-double-input-and-no-controller-transcript.txt">Read the transcript</a>.</figcaption>
</figure>

---

## No Devices Detected

**The [Devices](features/devices.md) page is empty, or the engine card on [Home](features/dashboard.md) shows 0 under "Devices connected"**

1. Try a different USB port. Use one directly on the computer, not a hub.
2. Try a different cable. Some cables are charge-only.
3. Check Windows Device Manager. If the device does not appear under "Human Interface Devices" or "Sound, video and game controllers," the issue is the device or its driver.
4. For Bluetooth controllers, pair and connect in Windows Bluetooth settings before starting PadForge.
5. If HidHide is installed, another tool may have cloaked the device. PadForge adds itself to the HidHide whitelist automatically while **"Hide Devices from Games"** is on in [Settings](features/settings.md), so turn that toggle on if it is off and let PadForge manage hiding. If the message line at the bottom of the window says HidHide blocked PadForge from a device it had hidden, PadForge took that hide back, so the device keeps working in PadForge, and it hides again as soon as HidHide's whitelist changes or its Inverse application cloak goes off.
6. Close other controller tools (DS4Windows, BetterJoy, reWASD). They may intercept access.
7. A DualShock 3 pairs through PadForge itself, with no separate driver tool to install. Connect it by USB, click **Pair a Controller** on the [Devices](features/devices.md) page, and follow the dialog. When it finishes, unplug the pad and press the PS button to connect over Bluetooth. It then streams with rumble and the player-number LED.

---

## Controller Detected but Not Mapping

**The controller appears on [Devices](features/devices.md) but produces no output or wrong output on the virtual controller.**

1. Unknown or unusual controllers may need manual mapping.
2. Assign the device to a controller. Select it on the [Devices](features/devices.md) page and click a controller under **Used By**, or drag the device onto a controller in the sidebar.
3. Map inputs by hand in the controller's [Mappings](features/mappings.md) section: click a control on the picture and press the input, for each button and axis.
4. Enable **"Force Raw Joystick Mode (Bypass Gamepad Remapping)"** on the [Devices](features/devices.md) page if SDL3's remapping produces wrong outputs. Re-record all mappings afterward.
5. Check live input on the Devices page. If axes and buttons do not respond there, the issue is at the driver or HID level.
6. Check the **Source** list on each mapping row. Confirm it points to the correct input device.

---

## Virtual Controller Not Appearing in Games

**A slot is created and a device assigned, but games do not detect the virtual controller.**

1. Xbox, PlayStation, Nintendo, and Extended output need **HIDMaestro**, which installs itself the first time a slot creates a virtual controller. There is nothing to install from [Settings](features/settings.md). If no virtual controller appears, turn the slot off and on, and PadForge runs the setup again.
2. MIDI output runs on **Windows MIDI Services** or, without it, the legacy MIDI API. Its card on [Settings](features/settings.md) says which one runs. Under the legacy API a MIDI slot needs an **Output Port** picked on its page.
3. Confirm the controller is on. The state pill on its card on [Home](features/dashboard.md) reads **Forging** while its virtual controller runs, and **Disabled** when it is off. A click on the pill turns it on or off.
4. Confirm the engine is running. The engine panel at the foot of the sidebar reads **Forging** beside a lit ember flame. **Idle** means the engine runs with no controller active, and **Stopped** means it is off.
5. Restart the game. Some games only scan for controllers at startup.
6. Check the game's input settings. Some games require manual controller selection.
7. For Extended controllers, verify in `joy.cpl` (Win+R > `joy.cpl`). If controllers are not listed, see "Extended Controllers Not Working" below.

---

## Nintendo Slot Not Seen by a Game

**A Nintendo virtual controller is active but the game does not react to it.**

1. The Nintendo output rides **HIDMaestro**, the same driver as Xbox, PlayStation, and Extended. It installs itself the first time a slot creates a virtual controller. If no virtual controller appears, turn the slot off and on, and PadForge runs the setup again.
2. Games see the slot as the picked Nintendo profile: **Nintendo Switch Pro Controller**, **Nintendo Switch Pro Controller (USB)**, **Nintendo Switch 2 Pro Controller** or **Nintendo Switch 2 Pro Controller: Full**. XInput-only games never read any of them. Use an Xbox slot for those.
3. Steam Input detects it as a Pro Controller with Nintendo glyphs. For games without native Switch Pro support, let Steam Input translate it, or switch the slot to Xbox output.
4. Gyro and accelerometer pass through from the assigned pad on every Nintendo profile except the plain **Nintendo Switch 2 Pro Controller**, whose report has no motion field. Pick **Nintendo Switch 2 Pro Controller: Full** or a Switch Pro profile for motion. The USB profile sends input only once a program such as SDL or Steam starts it with the Switch protocol.
5. Face buttons follow Nintendo lettering, so A sits on the right and B on the bottom. Prompts that look swapped against an Xbox pad are the Switch layout working as intended.
6. The Nintendo slot has no Customize surface. It deploys a catalog profile as-is. The profile picker offers the four profiles in step 2, with **Nintendo Switch Pro Controller** the default.

---

## Double Input in Games

**Every press registers twice, menus scroll too fast, or the game shows two controllers.**

The game is reading both the physical controller and PadForge's virtual controller.

1. Install **HidHide** from [Settings](features/settings.md).
2. Enable **"Hide Devices from Games"** on the [Settings](features/settings.md) page.
3. On the [Devices](features/devices.md) page, enable **"Hide from Games (HidHide)"** per device.
4. PadForge always whitelists itself, so it keeps seeing hidden devices. Add other apps (emulators, tools) that need a hidden device under **Whitelisted Applications** on [Settings](features/settings.md).
5. HidHide blocks only programs that open the controller after the hide lands. When PadForge hides a USB controller that was already connected, it reconnects it in Windows, so a game that had it open loses it. A Bluetooth controller, or a USB device that carries more than one controller such as the Xbox 360 wireless receiver, stays with a game that opened it first until it reconnects. Turn it off and on, or restart the game.
6. Check the game's controller settings. Disable the physical controller and keep the virtual one.
7. If using Steam: Steam opens controllers when it starts and holds them, so a pad it opened before PadForge hid it stays visible to Steam under its real name, and Steam Input reads it alongside the virtual one. PadForge reconnects a USB pad when it hides it, which takes the pad away from Steam. A Bluetooth pad stays with Steam until it reconnects, so start PadForge before Steam, or turn the pad off and on once PadForge is running. If Steam usually starts first, turn on **Keep Devices Cloaked Between Launches** in [Settings](features/settings.md). Otherwise disable Steam Input for the game or close Steam entirely.
8. BLE controllers (e.g., Xbox via Bluetooth) are properly hidden by HidHide on current PadForge builds. If peek-through still happens, check that the per-device **"Hide from Games (HidHide)"** toggle is on and that **"Hide Devices from Games"** is enabled in the HidHide section of [Settings](features/settings.md).

---

## Rumble Not Working

**No vibration from the physical controller when games send force feedback.**

1. Set **Overall Gain** above 0% on the [Force Feedback](features/force-feedback.md) card, in the controller's **Rumble & Haptics** section.
2. Set both **left motor** and **right motor** strength above 0%.
3. Click **Test Rumble** to verify hardware support. No vibration means the device lacks rumble motors or driver support.
4. Confirm a device is assigned to the controller and connected. The [Devices](features/devices.md) page lists it under **Connected**.
5. Check rumble support on the Devices page capabilities list. Devices without native rumble attempt haptic fallback, but results vary.
6. Try a different game. Not all games use force feedback.
7. Close other controller software (Steam Input, DS4Windows, reWASD) that may intercept rumble.
8. A DualShock 3 rumbles once it is paired through PadForge's **Pair** flow on the [Devices](features/devices.md) page. Rumble is native over that connection. A DS3 running through some other driver may not pass rumble.
9. For Extended output, the game must send HID PID 1.0 force feedback. See "Force Feedback Not Working" below.

---

## Audio Rumble Not Working

**Audio Rumble is enabled but the controller does not vibrate, or the Level meter stays flat.**

1. Confirm audio plays through the system default render device. The Level meter should show activity.
2. Level meter flat during playback? Toggle **Enable Audio Rumble** off and on to restart WASAPI capture.
3. Set **Overall Gain** above 0%. It applies to audio rumble too.
4. Increase **sensitivity** (default 4, try 8-12, range 1-20).
5. Raise **Bass Cutoff (Hz)** (default 80, range 20-200) to capture more mid-bass.
6. Set both motor scale sliders above 0%.
7. Click **Test Rumble**. No vibration means the device lacks rumble motors.
8. Some apps (DAWs, certain games) open audio in exclusive mode, blocking WASAPI loopback.
9. At least one audio output device must be active in Windows Sound settings.

---

## Bass Shakers Output Silent

**"Route Rumble to an Audio Output" is on but nothing plays on the shaker or subwoofer.**

1. Check the status line on the **Bass Shakers** card, in the controller's **Rumble & Haptics** section. "Audio output is not running." means the routing is off or the engine is stopped. "The selected output device is unavailable. Audio stays off until it returns." means the chosen **Output Device** disappeared. Pick another or reconnect it.
2. The feature works with Xbox, PlayStation, and Nintendo virtual controllers, plus Extended virtual controllers with force feedback, such as racing wheels. It also works with the Steam Deck Controller (Composite), SInput Gamepad and Joypad-OS Steam Input Gamepad profiles. Other slot types have no game feedback to route.
3. Game feedback and **Test Rumble** play through the audio output. Macro rumble stays on the controller by design. Test Rumble on the Force Feedback card is therefore a valid audio-path check, as are the per-voice **Test** buttons and **Frequency Sweep** on the Bass Shakers card.
4. Set **Master Gain** above zero and enable at least one voice with its own gain above zero.
5. Run **Frequency Sweep** (20 to 120 Hz over eight seconds) and note where the shaker responds strongest, then set the voice frequencies there.
6. Bluetooth audio devices add noticeable latency. Use a wired output if the thump lags the game.

---

## Extended Controllers Not Working

**Extended (HIDMaestro) controllers do not appear in games or `joy.cpl`.**

1. Accept the UAC prompt at PadForge startup. PadForge always runs as administrator. Canceling the prompt blocks startup entirely, so the engine never initializes HIDMaestro.
2. The HIDMaestro row of the **Drivers** card on the [Settings](features/settings.md) page reads "Installed" once the driver is in Windows' driver store, or on Windows on ARM once usbip-win2's host controller is installed, which happens with the first virtual controller. It does not show whether a virtual controller exists right now.
3. Check `joy.cpl` (Win+R > `joy.cpl`). An empty slot creates no controller: start the engine, turn the Extended slot on, assign an online device to it, and look again. If the controller is still missing, read the slot's status.
4. A failed HIDMaestro setup leaves "Error: Failed to initialize HIDMaestro." in the message line at the bottom of the window. It runs again when you turn the slot off and on, change its type or profile, or reconnect its controller, and the message clears once it succeeds.
5. If a slot stays stuck on "Initializing," check the **Inactivity Timeout** in [Settings](features/settings.md). After the timeout, a slot whose mapped devices stay offline has its live virtual controller torn down to free its kernel slot. The slot configuration (mappings, profile, position) is preserved. The virtual controller recreates automatically when the devices return online.

---

## Force Feedback Not Working

**Games send force feedback but the physical controller does not vibrate on an Extended slot.**

1. Confirm the game sends **HID PID 1.0** force feedback. Most native DirectInput FFB games do. XInput-only games do not. Use Xbox or PlayStation output for XInput rumble.
2. Set **Overall Gain** above 0% on the [Force Feedback](features/force-feedback.md) card.
3. Set motor strength sliders above 0%.
4. Click **Test Rumble** to confirm the physical controller supports rumble.
5. HIDMaestro Extended profiles publish constant, ramp, periodic (sine, square, triangle, sawtooth), and condition (spring, damper, inertia, friction) effects. Effect types outside that set may not produce rumble.
6. Restart the game. Some games only initialize FFB at startup.

---

## MIDI Not Available

**The MIDI tile in Add a Controller is dimmed and does nothing, with the tooltip "MIDI (not running, see Windows MIDI Services in Settings, under Drivers)".**

1. Open [Settings](features/settings.md) and read the **Windows MIDI Services** row of the **Drivers** card.
2. **Not Running** means no MIDI API started, the legacy one included, and the line under it says why:
    - "The Windows MIDI service did not respond. PadForge restarts the service and checks again." PadForge restarts the service once its check has been pending 15 seconds, and restarts it again every two minutes until MIDI answers. The row changes then.
    - "A MIDI driver did not respond. PadForge checks again once it answers." The check is waiting on a classic MIDI driver, as on Windows 10, Windows 11 before 24H2, in Legacy API mode, or for the legacy API in Hybrid mode. No service is restarted there, and the row changes when the driver answers.
    - "The runtime’s uninstaller is still running. MIDI starts again once it finishes." The runtime is being uninstalled.
3. While the row reads **Not Running**, a MIDI slot that tries to start puts "Virtual Controller *N* could not start because MIDI is not running. See Windows MIDI Services in Settings, under Drivers." in the message line at the bottom of the window. The slot starts by itself once MIDI answers.
4. "Virtual Controller *N* could not start because Windows MIDI Services did not create its port." means the API started but the service refused the slot's session or never finished creating its port. PadForge restarts the service and creates the slot again. It restarts the service at most once every two minutes, and a slot that failed starts again by itself after the next restart. The line clears once the slot runs.

Where Windows MIDI Services does not run (Windows 10, Windows 11 before 24H2, Legacy API mode, or no runtime installed), the row reads **Legacy MIDI API** and the MIDI tile works.

---

## No MIDI Output

**A MIDI slot is active but no messages reach the DAW or synthesizer.**

1. In the DAW, look for **"PadForge MIDI N"** in the MIDI input list. Under the legacy MIDI API there is no such port: the slot sends to the **Output Port** picked on its page, so the receiving app reads that port, such as the other end of a loopback driver.
2. Set the receiving app to read from the PadForge MIDI endpoint, or from the picked port.
3. Confirm the engine is running and the controller is on (its card on Home reads **Forging**).
4. Match the MIDI channel (1-16) between PadForge and the receiving app.
5. Verify CC numbers (axes) and note numbers (buttons) match what the receiving app expects.
6. Use a MIDI monitor (MIDI-OX or Windows MIDI Services console) to confirm PadForge is sending. If messages appear there but not in the DAW, the issue is DAW configuration.
7. Under the legacy MIDI API, a slot that reports its port in use cannot open it while another program or another MIDI slot holds it. When the message names two controllers, pick another port for one of them, and the slot that failed starts by itself. Otherwise close the other program, then turn the slot off and on.

---

## My Macro Doesn't Fire

**A macro trigger is configured but pressing the trigger does nothing.**

1. All trigger components must be active **simultaneously**. Partial presses do not fire.
2. For axis triggers, the input must exceed the configured **threshold percentage**.
3. Check the macro's **Layer**. "Any layer" fires regardless of the engaged layer. Base and named layers fire only while that layer is engaged, exactly like a mapping row. A macro scoped to a shift layer is inert the whole time that layer is off.
4. Check the trigger mode. Several fire by design only on a specific press shape:
   - **On Long Press** fires after the trigger is held for the **Hold Time**. A quick tap does nothing.
   - **On Short Press** fires on release before the Hold Time elapses. Holding past it fires nothing.
   - **On Single Press**, **On Double Press**, and **On Triple Press** wait out the **Press Window** and fire only on the matching press count.
   - **Turbo** repeats only while the trigger is held.
5. Confirm the macro's **Enabled** switch is on.
6. Confirm the engine is running and the slot is enabled.
7. If the **Source** dropdown is set to **Assigned Devices** but no device is assigned, there is nothing to read. Switch to **Virtual Controller** or assign a device.
8. Check for conflicting macros with overlapping triggers on the same slot.

---

## Mouse Actions from Macros Aren't Working

**A macro with Mouse Move, Mouse Button, or Mouse Scroll produces no visible effect.**

1. Match the axis source. **Virtual Controller** reads the slot's combined output, **Assigned Devices** reads the physical device directly.
2. Adjust the **sensitivity slider** if movement is too fast or too slow.
3. Confirm the trigger is firing (see "My Macro Doesn't Fire" above).
4. Anti-cheat software may block simulated mouse input.

---

## Macro Won't Stop Running

**A macro keeps firing after the trigger is released, or runs with no trigger at all.**

1. A macro set to **Always** fires every frame while the slot is active. No trigger required. That is its design.
2. A macro set to **Toggle** latches: the first press turns the actions on, and holds and repeats stay active until the second press. Tap the trigger again to release it.
3. Turn off the macro's **Enabled** switch to stop it.
4. Delete the macro if it is no longer needed.
5. Turn the controller off (the state pill on its Home card) to stop all its macros.

---

## Keyboard+Mouse Not Working

**A Keyboard+Mouse slot is active but key presses or mouse movement do not appear in games.**

1. Verify the controller's type is Keyboard + Mouse. Its type chip on the controller page and on its Home card names it.
2. Assign physical inputs to keyboard keys or mouse buttons in the controller's **Keys & Mouse** section.
3. Adjust mouse sensitivity in the **Sticks & Triggers** section or in the macro action's settings.
4. Anti-cheat software (EAC, BattlEye, Vanguard) may block simulated input.
5. Confirm the engine is running and the slot is enabled.

---

## Settings Not Saving

**Changes revert after restart or the settings file does not update.**

1. Wait for autosave. PadForge saves after changes settle. Unexpected closes may lose recent edits.
2. Click **Save** on the [Settings](features/settings.md) page to force an immediate save.
3. Confirm PadForge has write access to its folder. `PadForge.xml` lives next to the executable. A save that fails puts "Error saving settings: *reason*" in the message line at the bottom of the window, and the reason names the cause.
4. Click **Open Folder** on the **Settings File** card and verify `PadForge.xml` exists and is not read-only.
5. Add a PadForge exception if antivirus software blocks file writes.
6. When PadForge cannot read `PadForge.xml` at startup, the message line says "Error loading settings: *reason*". PadForge copies the unreadable file to `PadForge.xml.unreadable-` plus the date and time, such as `PadForge.xml.unreadable-20261011-213045`, in the same folder, so its contents are kept, and starts with default settings. The copy stays when the next save replaces `PadForge.xml`. To start over, click **Reset to Defaults** on the **Settings File** card of the [Settings](features/settings.md) page. It removes every virtual controller, mapping and saved profile and returns every setting to its default, with no confirmation, and PadForge writes the new file about two seconds later.
7. If the folder is synced by OneDrive, Dropbox, or similar, concurrent access across machines can corrupt the file. Move PadForge to a non-synced folder.

---

## App Won't Start or UAC Prompt Issues

**PadForge does not start, closes immediately, or shows a UAC prompt every time.**

1. UAC is expected while Start at Login is off. PadForge always runs as administrator. HIDMaestro driver registration, HidHide whitelist edits, and the polling loop all run in that one elevated process.
2. Clicking "No" on UAC blocks startup. Windows does not start an administrator-required app when the user declines the prompt. Run PadForge again and accept the prompt.
3. PadForge allows only one instance. Check Task Manager for existing PadForge processes.
4. Check for `crash.log` next to `PadForge.exe`. It appears only after a crash and records the error. The crash message names it and the page where reports go, a new Q&A discussion on PadForge's GitHub page: start one there and drop `crash.log` on it.
5. PadForge ships as a single self-contained `PadForge.exe` with the .NET runtime and SDL3 embedded. Nothing needs installing separately. If startup still fails with no `crash.log`, the download may be truncated or corrupt. Re-download `PadForge.exe` from the [release page](https://github.com/hifihedgehog/PadForge/releases).

---

## High CPU Usage

**PadForge uses significant CPU, causing fan noise or slowdowns.**

1. Lower the **Polling Rate (Unless a Profile Overrides It)** on the **Input Engine** card in [Settings](features/settings.md). It is in passes a second, 60 to 10000, and 1000 by default. Try 250 or 125 for casual use. A profile with its own polling rate overrides this value while it is active.
2. Disable **"Continue Polling When Window Loses Focus"** if PadForge is only needed during gameplay.
3. Delete virtual controllers you do not use.
4. Turn off **Audio Rumble** on controllers that do not need it. It runs WASAPI capture and real-time DSP.

---

## DSU Motion Not Working in Emulator

**The emulator does not receive gyroscope or accelerometer data.**

1. On the **Extras** page, open **Motion Server** and turn it on. Its status should read "Listening on port 26760".
2. Match the port (default 26760) between PadForge and the emulator. "Port 26760 in use" means another app (BetterJoy, DS4Windows) is on that port.
3. Use `127.0.0.1` as the server address. PadForge binds to loopback only.
4. Confirm the controller has motion sensors on the [Devices](features/devices.md) page. Any controller PadForge reads gyroscope or accelerometer data from feeds the DSU server: DualSense, DualShock 4, DualShock 3, Switch Pro, Switch 2 Pro, Joy-Cons, and the Wii Remote all report motion. A controller without motion sensors feeds it through the [Motion Pitch, Yaw and Roll](features/mappings.md#motion-pitch-yaw-and-roll) rows, which only PlayStation, Nintendo and Valve Extended slots carry.
5. DSU protocol supports slots 1-4 only. Assign the motion device to one of those slots.
6. The DSU server binds to loopback only, so no firewall rule is involved. An emulator on another machine cannot reach it at all.
7. In the emulator's motion settings, add a DSU server at `127.0.0.1` with the matching port and select the correct slot.

---

## A Stick on the Motion Gyro Row Does Nothing

**Sticks or buttons on the Motion Gyro or Motion Accelerometer row never move the game's motion.**

1. Those two rows read only a controller's own motion sensor. A note under the row names the problem when it holds anything else.
2. Bind the stick to the [Motion Pitch, Yaw and Roll](features/mappings.md#motion-pitch-yaw-and-roll) rows instead: Motion Pitch from the stick's Y axis, Motion Yaw or Motion Roll from its X axis.
3. Remove the stick and button sources from the Motion Gyro row.
4. The rows need a slot that carries motion: PlayStation, Nintendo, or Extended on a Valve profile. On the DualShock 3 (SIXAXIS), Switch 2 Pro and plain Steam Deck presets only the DSU server receives the motion.

---

## Web Controller Not Connecting

**The browser shows "Disconnected" or cannot reach the web controller URL.**

1. On the Extras page, open **Web Controller** and turn it on. Its status should read "Running on" followed by the address.
2. Both devices must be on the same Wi-Fi or LAN network.
3. PadForge creates a firewall rule automatically.
4. "Port 8080 in use" means another app occupies that port. Change the port under **More** on the Web Controller page in Extras.
5. "Access denied for port 8080 (run as admin)" means Windows reserved it or another service holds it. Change the web controller port to a free one.
6. Rotate the browser device to **landscape** orientation.
7. Try Chrome, Firefox, or Edge if WebSocket issues occur.
8. On the [plain HTTP address](guides/web-controller.md#plain-http-address), a page saying the controller opens only with its access code means the address was opened without the code, or with one that **New Code** replaced. Open the address the Web Controller page shows, or scan its QR code.
9. "Same port as the main address" under the plain address means both addresses asked for one port. Give the plain address its own.

---

## Web Controller drops on iOS Safari After Tab Switch

**On iPhone or iPad Safari, the web controller shows "Disconnected" after locking the screen, switching tabs, or returning from another app.**

iOS Safari kills WebSocket connections when the page loses focus. The web controller page detects the dropped socket, shows "Disconnected. Tap to reconnect.", and retries every three seconds, or the moment the tab comes back to the foreground. If it does not come back, do this:

1. Tap the page to bring it back into focus.
2. Pull down to refresh, or close the tab and reopen the URL.
3. Keep Safari in the foreground while playing. Background-tab WebSockets are not reliable on iOS.
4. Add the page to the Home Screen as a PWA for a more stable foreground session.

---

## Remote Link PC Not Appearing

**A second PC runs Remote Link but does not show on this PC's Remote Link page, which says "No unpaired PCs nearby."**

1. Turn on **Remote Link** on both PCs, from its page in Extras. A PC only announces itself while it is on.
2. Both PCs must be on the same local network. Discovery is a same-subnet broadcast, so a separate guest network, VLAN, or Wi-Fi band hides them from each other.
3. Some networks block broadcast. Under **More**, open **Connect by Address**, enter the other PC's address and port, and click **Pair / Connect**.
4. Allow PadForge through the firewall on each PC. For per-port rules, open UDP 27501 for discovery and TCP 27500 (or your changed listening port) for the connection.
5. For play across the internet, swap connection codes. Copy **This PC’s Code** on the Remote Link page, have the other person paste it into their **Connect by Address** box while you paste theirs, then you both click **Pair / Connect**. That opens a direct path with no VPN and no port forwarding. Broadcast discovery does not cross the internet, so the code is how the two PCs find each other. If PadForge warns that the network cannot make direct connections (a mobile hotspot or carrier-grade NAT), fall back to a VPN like Tailscale and connect by address.

---

## Remote Link Pairing Fails

**Pairing does not complete, or the six-digit codes differ on the two screens.**

1. Check that the two codes match before confirming. A mismatch means the PCs did not reach each other directly. Cancel and retry.
2. Confirm on both PCs. Pairing needs both sides to accept.
3. A failed handshake creates no device and shares nothing. Start pairing again from one side.
4. A PC you revoked cannot reconnect until you pair it again.

---

## Shared Device Not Showing After Pairing

**Two PCs are paired but the owner's controller does not appear in the consumer's device list.**

1. Confirm the pairing is active. Each PC lists the other under **Paired PCs** on its Remote Link page in Extras.
2. Remote Link shares controllers, wheels, and HOTAS hardware, not arbitrary USB devices.
3. On the consumer, assign the shared device to a [slot](features/controller-slots.md) and map it like a local controller.
4. Auto-reconnect is on by default. With it off, the PCs do not relink on their own after a drop. Turn it back on in the Remote Link settings, or re-pair.

---

## Remote Link Feedback or Keyboard Not Working

**A shared device drives the game, but rumble, LEDs, or keyboard output do not reach the owner PC.**

1. Feedback returns only for what the physical device supports: rumble, Xbox trigger rumble, wheel force feedback, adaptive triggers, lightbar, player LEDs, Guide button LED brightness, speaker audio, and HD haptic tones. A device without a given feature shows nothing for it.
2. Keyboard, mouse, and macros from a shared device are blocked when **Gamepad Only** was checked for that paired PC (its row under Paired PCs shows "gamepad only"). To allow them, revoke the PC and pair again with the box cleared.
3. See [Force Feedback](features/force-feedback.md), [Impulse Triggers](https://padforge.org/docs/features/impulse-triggers/), [Lighting](features/lighting.md), and [Controller Audio](https://padforge.org/docs/features/controller-audio/) for the per-feature setup, which also applies over the link.

---

## Profile Not Switching Automatically

**Auto-profile switching is enabled but PadForge does not switch when the game is in the foreground.**

1. Confirm **Switch Profiles by Game** is on, under **Automatic Switching** on the [Profiles](guides/profiles.md) page.
2. Verify the executable path matches (case-insensitive, full path). Use the file browser button to avoid typos.
3. For games launchable from multiple locations, separate paths with the pipe character (`|`).
4. Add the game's own executable, not the launcher's (Steam, Epic, etc.).
5. PadForge always runs elevated, so it can read the foreground path of elevated games. If a protected process still is not detected, add the game executable that actually owns the window.
6. Save after configuring. Profile data must persist to `PadForge.xml`.

---

## Community Configs (Steam Workshop) Issues

**"Steam Is Unreachable" on the Community tab of Get Profiles.**

1. Check your internet connection and click **Retry**.
2. Corporate or school networks sometimes block Steam. The connection uses standard HTTPS (port 443).
3. Already-imported profiles keep working offline. They live on this PC.

**An imported profile double-inputs or fights the original Steam config.**

The imported profile replaces what the Steam config did. Disable Steam Input for the game (Steam > game properties > Controller), or close Steam, and cloak the physical pad on the [Devices](features/devices.md) page.

**An imported profile does nothing in game.**

1. Assign your physical controller to both of the profile's pads. Imports ship with no device assignments.
2. Confirm the profile is active (load it, or add the game's executable via **Edit** for auto-switch).

See [Steam Workshop Config Import](https://padforge.org/docs/guides/steam-workshop-import/) for the full feature guide.

**An imported reWASD or DS4Windows profile does not behave like the original.**

1. Read the preview's **Partial** and **Skipped** rows before importing. Each carries its reason, and [Import from reWASD, DS4Windows and DSX](guides/profile-import.md#what-reads-partial-or-skipped) lists what each one asks of you.
2. Import adds the profile without loading it. Select it on the Profiles page and click **Load**.
3. Assign your controller to the profile's controller slots on the [Devices](features/devices.md) page. An import assigns no controller. Keyboard and mouse inputs read the merged rows, which the profile assigns itself.
4. If keys still reach the game twice, turn on **Consume Mapped Inputs (Hooks)** for the keyboard or mouse, or **Hide from Games (HidHide)** for the controller, as the preview's reason says.
5. Close reWASD or DS4Windows while testing. Both read the same controllers and can fight PadForge for them.

See [Import from reWASD, DS4Windows and DSX](guides/profile-import.md) for the full feature guide.

---

## Sensitivity Curves Not Responding

**A sensitivity curve is set but stick or trigger output seems unaffected.**

1. Check the correct axis. X (horizontal) and Y (vertical) have independent curves.
2. If the preset is "Linear," no modification is applied. Select a different preset or add custom points.
3. Click **Reset** to restore Linear, then apply a new preset to verify functionality.
4. A large deadzone leaves little range for the curve to affect. Reduce the deadzone to test.
5. Custom control points placed nearly linearly produce no visible change. Spread them further apart.
6. Per-direction max range settings may cause asymmetric behavior. Review them on the **Sticks** card, in the controller's **Sticks & Triggers** section.
7. Non-default deadzone shapes (Radial, Axial, Hybrid, Sloped Axial, Sloped Scaled Axial) interact differently with curves. Try Scaled Radial, the default, to isolate the issue.

---

## Steam Controller Conflicts

**Controller behaves unexpectedly, is not detected, or rumble stops. Especially Switch Pro or Switch 2 Pro.**

1. Disable **Steam Input** for the specific controller in Steam > Settings > Controller.
2. Close Steam entirely if not in use.
3. Switch 2 Pro Controller. Steam may lock the WinUSB interface. Close Steam before connecting, or disable Switch controller support in Steam.

---

## Buttons Map to Wrong Outputs

**Buttons do not match expected positions, trigger two inputs, or produce no input.**

1. Enable **"Force Raw Joystick Mode (Bypass Gamepad Remapping)"** on the [Devices](features/devices.md) page to bypass SDL3's remapping.
2. Re-record all mappings by hand in the controller's [Mappings](features/mappings.md) section, or run **Map All**.
3. Verify in `joy.cpl` that buttons work correctly at the Windows level.
4. Known affected devices: certain third-party controllers with non-standard HID layouts.

---

## Opposite Buttons Cancel Each Other

**Two opposite directions held together and one, or both, vanish from the game.**

1. That is SOCD cleaning doing its job. The **SOCD** card, under the picture in the controller's **Mappings** section, resolves paired buttons held at once.
2. The **Mode** decides the outcome. **Last Wins (Snap Tap)**: the most recent press wins, and releasing it re-presses the still-held partner. **Neutral**: holding both releases both until one is let go. **First Wins**: the earlier press keeps winning until it is released.
3. The rule applies to the slot's final combined output, so physical, mapped, and macro presses are all cleaned.
4. To turn it off, set the Mode to **Off** or remove the pair under **Button Pairs**.
5. Keyboard + Mouse controllers have their own SOCD card with **Key Pairs**, applied to the virtual keyboard output.

---

## Force Raw Joystick Mode

**Buttons map wrong, trigger double inputs, or shoulder/trigger/stick-click inputs are missing.**

SDL3's gamepad mapping does not match the device's HID report layout (common with some third-party controllers).

1. Go to the **Devices** page.
2. Select the problematic device.
3. Enable **Force Raw Joystick Mode (Bypass Gamepad Remapping)** in the device's **Details** card.
4. Open the controller's **Mappings** section and record each button by hand.
5. Auto-mapping is unavailable in raw mode, but raw indices will be correct.

---

## Mouse or Keyboard Input Issues

**Mouse buttons don't register, mouse movement is barely visible, or mouse axes can't be recorded.**

1. Mouse buttons map like any other input. In the controller's Mappings section, click the control on the picture, or **Record** on an open row of the list, and press the mouse button.
2. Mouse axes show relative deltas, not absolute positions. Fast movements produce larger values.
3. When recording a mouse axis mapping, move the mouse in the desired direction. Detection is instant.
4. PadForge does not consume mouse or keyboard inputs on its own. Turn on **Consume Mapped Inputs (Hooks)** if mapped keys or buttons should not also reach other programs (a warning appears first). **Hide from Games (HidHide)** does not stop Windows from reading a keyboard or mouse.
5. To take a USB keyboard or mouse away from Windows entirely, use **Use Only in PadForge** in the device's **Settings** card on the [Devices](features/devices.md) page. See the next entry.

---

## Keyboard, Mouse or Touchpad Dead in Windows After Use Only in PadForge

**A keyboard, mouse or touchpad no longer types or points in Windows after Use Only in PadForge, or its row shows no such button.**

1. A device on **Use Only in PadForge** is PadForge's alone. Windows stops typing with the keyboard or moving the pointer with the mouse in every program, even while PadForge is closed. Select the device on the [Devices](features/devices.md) page and click **Restore the Windows Driver**. A touchpad goes back to Windows when the engine stops or PadForge closes, and its button reads **Give Back to Windows**.
2. The **Keep This Keyboard in PadForge?**, **Keep This Mouse in PadForge?** and **Keep This Touchpad in PadForge?** prompts give the device back after 15 seconds unless you click **Keep**. If you left it, the message line says the device is back on its Windows driver because the switch was not kept.
3. The button shows only for a USB keyboard or mouse and for a precision touchpad. A keyboard or mouse on Bluetooth, PS/2 or I2C has none.
4. "Unplug the other keyboards of this model first" (or mice) means Windows moves every plugged-in device with the same ID at once. Unplug the others, switch, and plug them back.
5. A touchpad needs HidHide installed, **Hide Devices from Games** on in [Settings](features/settings.md), and the engine running. The message line names the one that is missing.
6. A switched device shows neither **Hide from Games (HidHide)** nor **Consume Mapped Inputs (Hooks)**. That is expected.

[Driver Management](features/driver-management.md#keyboards-mice-and-touchpads-use-only-in-padforge) has the details.

---

## Stick Drift After Calibration

**The stick drifts even after setting deadzones.**

1. Open the **Sticks** card, in the controller's **Sticks & Triggers** section.
2. Leave the stick at rest. Do not touch it.
3. Click **Calibrate Center**.
4. PadForge measures the actual rest position and applies an offset so the deadzone centers correctly.

---

## Flick Stick Doesn't Turn the Camera

**A Flick Stick source is selected but sweeping the stick does not rotate the view.**

1. Map **Flick Stick (Right Stick)** to **Mouse X** from the **Source** list in the controller's Keys & Mouse section, on any layer. Flick Stick produces mouse movement, so it needs a Mouse X row to land on.
2. Point the stick and the camera turns to match. Sweep the rim to fine-turn.
3. The game's camera must follow the mouse. Flick Stick drives the camera through mouse input, not a stick axis.
4. Tune it on the **Flick Stick** card, in the **Sticks & Triggers** section of a Keyboard + Mouse controller. "Flick Stick (Left Stick)" and per-touchpad variants are available from the same dropdown.

---

## Volume Macro Issues

**Volume changes go the wrong way, or the volume indicator appears when it should not.**

1. Wrong direction. Enable **Invert Axis** on the volume action settings.
2. Unwanted indicator. Turn off **Show Volume Indicator** on the volume action.
3. Volume not changing. Confirm the trigger fires (see "My Macro Doesn't Fire" above).

---

## Shift Layer Won't Engage or Won't Turn Off

**The activator input is set but the layer never turns on, a long press never registers, or a toggled layer stays stuck on.**

1. Check the **Mode**. Hold keeps the layer on only while the activator is held. Toggle and Latch flip the layer on a press and keep it on until you press again. Sticky (one-shot) turns the layer on with a tap, then drops it the moment you use a mapped input on the layer and release. Cycle steps through an ordered list of layers, one per press.
2. Check the **Delay**. In Hold mode it is a debounce: the activator must stay held that long before the layer changes. In Toggle, Latch, or Sticky mode a non-zero Delay turns the activator into a long press, so the layer flips only when the hold crosses that time and a quick tap does nothing. Lower the Delay to 0 if a tap should register.
3. Check **Fire on Release**. With it on, the activator waits until the button is let go before firing, so it looks dead the whole time the button is held. If a hold delay is set, the press must last that long for the release to count.
4. For an **Axis** activator, the input must cross the threshold. Default is 0.5 of full deflection.
5. For a **Chord** activator, both inputs must hold at the same time.
6. The activator can live on a different physical device than the slot it shifts. Confirm that device is online.
7. Last engaged wins on conflicts. If a different slot's activator fired more recently, that layer is active instead.
8. Look at the engaged-layer flyout at the bottom of the screen. If it never appears, the activator never registered the input.

**A toggled layer won't turn off.**

1. Tap the activator again to release a Toggle layer.
2. Set **Auto-Cancel After Inactivity** (Toggle mode) so the layer drops itself after an idle stretch. It is 0 (off) by default. Enter a time in milliseconds. The layer turns back off after that long with none of its own mapped inputs active, and the timer restarts on every layer input.

---

## Impulse Triggers Card Not Visible

**The Impulse Triggers card is missing from a controller you expected to have it.**

1. The card, in the **Rumble & Haptics** section, only appears when the selected physical pad has trigger motors. Xbox One, Xbox Elite (Series 1 / Series 2), and Xbox Series X|S pads have them. Xbox 360 and most third-party pads do not.
2. The card follows the device selected under **Driven by** at the top of the controller page. Select the right physical device there.
3. A DualSense does not get this card. Its trigger motors are driven from the **Adaptive Triggers** card instead. Xbox impulse data a game sends routes there automatically as trigger vibration, gentler than Xbox impulse-trigger motors. If the buzz feels weak, raise the Vibration effect strength on the Adaptive Triggers card.

---

## Xbox Elite Paddles Do Nothing

**The four paddles of an Xbox Elite controller never light a mapping row, while the rest of the pad works.**

1. Check how the controller is connected. Over USB and the Xbox Wireless Adapter, PadForge reads the paddles from the Windows GameInput service, which takes Windows 11 24H2 or 25H2 at build 26100.8973 or 26200.8973 (July 28, 2026) or later. Run `winver` to see your build. The GameInput redistributable is not required.
2. On Windows 10 and older Windows 11 that route stays off. An Elite Series 2 can pair over Bluetooth, where the paddles come from the controller's own Bluetooth LE service. The first Elite has no Bluetooth.
3. The paddles are sources named **Right Paddle 1**, **Left Paddle 1**, **Right Paddle 2** and **Left Paddle 2**. Record a mapping by pressing one, or pick it from the **Source** list.

---

## Custom Expression Macro Won't Fire

**A Custom Expression macro is set up but the formula never crosses 0.5.**

1. Read the status line under the formula box. A ⚠ line names each letter the formula uses with no variable row behind it. Those letters read as 0.
2. Each variable needs a recorded input. Click **Record** on a variable row and press the input. A variable with nothing recorded reads as 0.
3. The macro fires on the **rising edge** (0 → over 0.5). It does not refire until the value drops below 0.5 and rises past it again.
4. Buttons and POV directions read as 0 or 1. Triggers read 0 at rest up to 1 fully pressed. A stick axis reads 0.5 at rest and moves toward 0 or 1 as you push it, so a resting stick showing 0.5 is normal, not a binding failure.
5. Verify the formula syntax. The status line shows **✓ valid** for a formula that parses, and **✗** followed by the parse error for one that does not.

---

## Touchpad Overlay Doesn't Drive the DS4 / DualSense Touchpad

**The Touchpad Overlay window is enabled but touch input doesn't reach the game.**

1. The overlay drives the touchpad on the assigned **PlayStation** slot only. Confirm the slot type is PlayStation, not Xbox or Extended.
2. The slot needs at least one mapped device. The overlay forwards finger contacts. The slot needs to exist for them to land somewhere.
3. If the slot's source is a real DualSense or DS4, that pad passes its own touchpad through to the game, and that takes priority over the overlay. Remove or hide the physical pad from the slot to let the overlay drive the touchpad instead.

---

## Touchpad Pressure Reads in Steps

**A touchpad's Pressure source jumps between 0%, one fixed level, and 100% instead of reading smoothly.**

1. That is **Synthetic Pressure**. On pads that report a touch as full pressure (DualShock 4, DualSense, Steam Controller 2015), it shapes the Pressure sources into three steps: no touch reads 0%, a resting touch reads the Touch Pressure Level, and a pad click reads 100%.
2. Turn off **Enable Synthetic Pressure** on the **Touchpad** card, in the controller's **Motion & Touch** section, to return to raw readings.
3. A pad without a true pressure sensor reports touch as full pressure by hardware. Synthetic Pressure is the only way to get graded values out of it. Smooth analog pressure needs a pad that actually senses it.

---

## NFC Reader Not Detected

**A USB NFC reader is plugged in but does not appear as a device, or the NFC Tags dialog says no reader is detected.**

1. PadForge reads through the Windows **Smart Card** service (`SCardSvr`) over the PC/SC stack. Open `services.msc` and confirm "Smart Card" is running. Windows stops it automatically when the last reader is unplugged.
2. Use a **PC/SC contactless reader (CCID)**, such as the ACR122U. Readers without a PC/SC driver do not enumerate.
3. A reader plugged in after launch is picked up automatically. PadForge retries roughly every 5 seconds, so wait a moment.
4. Register a tag before mapping it: open the **NFC Tags** dialog via **Register / Manage NFC Tags**, tap the tag on the reader, name it, click **Register**. Each registered tag becomes a bindable button.
5. An unregistered tag only pulses the "Any NFC Tag" button, not a per-tag button. Register the specific tag to bind it on its own.
6. PadForge reads only the tag **UID** (NTAG21x, MIFARE, amiibo all work). It does not read tag memory, authenticate sectors, or write tags.

---

## Media Keys (Consumer Control) Not Mapping

**Play/Pause, volume, or other media keys on a keyboard or remote do not show up as a mappable source.**

1. The keyboard or remote must expose a media-key device. Windows presents a keyboard's media-key row as a separate Consumer Control device. A basic keyboard without a media row exposes none and is not enumerated.
2. Media keys arrive on a separate path from normal keys. Pick the media-key device itself as the mapping source, not the keyboard.
3. Consumer Control is an **input source only**. PadForge cannot suppress media keys, so they still reach the OS even when mapped. There is no "consume" option for them.
4. Standard media keys have stable mappings. An uncommon usage gets a session-only "Consumer 0xNNNN" slot whose index is not stable across restarts, so re-record those after an update.

---

## Wii Remote IR Pointer Not Working

**A Wii Remote is connected but the IR Pointer source stays centered or does not aim.**

1. The IR pointer needs a **sensor bar or any IR light source** in view of the remote's camera. It reads the two sensor-bar dots. Until the remote first sees both, it reports centered.
2. A powered USB sensor bar or a pair of IR emitters both drive it. The bar only needs to emit IR, it carries no data.
3. Aim the remote at the bar. When the camera loses the dots the source holds its last aim and moves again when both dots return. 4.5.3 and earlier relaxed to center instead.
4. Map **IR Pointer X** and **IR Pointer Y** to stick axes. Tune sensor-bar position, vertical offset, and smoothing on the **IR Pointer** card, in the **Motion & Touch** section. Sensitivity is per source, on the mapping row itself.
5. A right Joy-Con's **IR Brightness** source is different. It reports a single cover/proximity value, not an X/Y pointer.
6. Each time a remote connects, its camera is started until it sends IR data. SDL's Wii driver starts a failed camera again after 1, 2 and 4 seconds and then every 8, and PadForge restarts it up to three times if no IR data comes. The [diagnostics log](features/settings.md#keep-a-diagnostics-log) records PadForge's tries as `WIIIR` lines.

---

## Joy-Con 2 Mouse Not Working

**A Joy-Con 2 is connected but its optical mouse sensor produces no Mouse Motion output.**

1. The sensor rides the custom SDL fork's BLE Switch 2 driver, which is embedded inside `PadForge.exe`. Release builds always carry it. Only a source build running against a stock `SDL3.dll` lacks the driver, and there the source stays silent with no error.
2. Connect the Joy-Con 2 over Bluetooth. The mouse data comes through the wireless Switch 2 path.
3. Map **Mouse Motion X** and **Mouse Motion Y** to stick axes or scroll. Adjust the per-source Sensitivity if the cursor moves too fast or slow.

---

## Switch 2 Controller Drops over Bluetooth

**A Switch 2 Pro Controller or a Joy-Con 2 on Bluetooth stops for a moment, or leaves the Devices page.**

1. A pad whose link drops keeps its place, with its sticks and buttons at rest. Windows connects the link again by itself. Within 12 seconds input resumes, or the pad leaves the list. A pad PadForge turns off itself (Idle Disconnect or a power-off) leaves at once, unless it keeps reporting for more than 2 seconds after the shutdown.
2. A Joy-Con 2 clone such as the Mobapad M12-S drops its link about every 30 seconds. PadForge 6.0.0 is expected to show that as a short gap where earlier builds lost the pad for good. No clone has run this build, so that remains unconfirmed.
3. To see what happened, turn on **Keep a Diagnostics Log** in [Settings](features/settings.md), or click **Save a Snapshot…** right after the drop. The lines come from SDL's input category and read `SDL [7/3]` before the text:
    - `BLE GATT session: MaintainConnection hr=0x...` when the pad connects. A value other than `0x00000000` is an error code from Windows, and the link is then not held.
    - `BLE Switch2 Windows pairing: paired` or `not paired`.
    - `BLE Switch2 link lost N ms after it came up (console path): waiting up to 12 s for it`, with `unified path` for a pad that streams its standard reports.
    - `BLE Switch2 link restored after N ms (console path)` when the pad comes back.
    - `BLE Switch2 link not restored after 12 s (unified path): removing the joystick` when it does not.
    - `BLE Switch2 link lost N ms after a shutdown the app sent: removing the joystick` after PadForge turned the pad off.
    - `BLE Switch2 no reply to the calibration reads on 649d4ac9: starting the console session` for a clone that answers only the way a Switch 2 console talks to it.

---

## Battery Level Not Showing

**A controller's battery glyph or percentage is missing on the Devices page.**

1. Battery shows only when the device reports a level. Many wired controllers and some Bluetooth pads report nothing, so no indicator appears.
2. The reading refreshes about every 5 seconds, not every frame. Give it a moment after connecting.
3. An offline device shows no battery. Confirm it is listed under **Connected** first.
4. If SDL cannot read the battery for the device, PadForge cannot either. There is no manual override.

---

## Controller Won't Auto-Disconnect When Idle

**The idle auto-disconnect timeout is set but the controller never disconnects, or it disconnects too soon.**

1. Idle Disconnect is **off by default** (0). Set a timeout in minutes in the device's **Settings** card on the Devices page to enable it.
2. It only affects **Bluetooth** controllers. A wired (USB) controller has no radio link to drop, so it stays connected even with a timeout set.
3. Xbox, Valve (Steam Controller), and Switch 2 pads receive a direct power-off. Sony pads, the Wii Remote, and other Bluetooth controllers drop their Bluetooth link instead, which needs the controller to report a valid MAC address (Bluetooth pads do). A classic Bluetooth pad that reports none stays connected.
4. Aiming with the Wii IR pointer or Joy-Con 2 mouse counts as activity, so you are not disconnected mid-use.
5. Charging does not pause the countdown. An idle pad disconnects even while charging, and dropping Bluetooth does not interrupt the charge.

---

## HD Haptic Tones Sound Wrong or Garbled

**Audio routed to a controller's haptic actuator plays beeps fine but speech and music come through as buzz.**

1. This is a hardware limit, not a bug. These controllers (Nintendo Joy-Con and Switch Pro, Steam Controller 2015, Steam Deck, Steam Controller 2026) reproduce sound through haptic actuators, not a speaker.
2. A haptic actuator plays one frequency at a time. PadForge reduces the audio to that one dominant tone plus a volume envelope each tick. Simple alert tones and melodic cues survive. Speech and full music do not.
3. Joy-Con and Pro tones fold into roughly 41-626 Hz. A note outside that band is shifted by whole octaves until it fits, so it keeps its pitch class but not its register. The 2015 Steam Controller and Steam Deck play pitch only, with no working volume, so they feel flatter.
4. DualSense and DualShock 4 do not use this path. They play through their actual speaker. A Wii Remote likewise uses its speaker.
5. Switch 2 (Joy-Con 2, Pro Controller 2) does not play haptic tones. Its actuator is driven for rumble feel only.

---

## Wii Remote Motion Drifts or Turns the Wrong Way in an Emulator

**A Wii Remote feeds an emulator through a Nintendo or PlayStation slot or the DSU server. Steering drifts after a tilt, the orientation is off, or a sharp turn goes the wrong way.**

1. On the **Gyro** card, in the controller's **Motion & Touch** section, set **Motion Passthrough** to **Raw**. Raw is the default. With **Tuned**, the card's defaults run smoothing and a 3°/s deadzone on the rate the emulator integrates, and the integrated orientation walks away from the real one. The card's live readouts show the calibrated rate, not the tuned one, so they look fine while this happens.
2. Set **Held As** on the Grip card to the hold you are using: **Pointing** for aiming at the screen, **Sideways, Face Up** for the NES-style hold, **Wii Wheel, Face Toward You** for the wheel shell, **Upright** for the pointer at the ceiling. The two sideways holds use different rotations, so a wheel played with Sideways, Face Up selected steers on the wrong axis.
3. Hold the remote still for a moment after changing the grip. The change recenters the gravity estimate and the tilt neutrals.
4. If the remote still drifts at rest, set it down and click **Calibrate Gyro**. The zero is calibrated on connect. The driver applies a fixed Motion Plus scale and reads no calibration block from the remote, so a fast turn can read a little short. That is a known limitation, not a setting.
5. In both sideways holds the D-pad turns with the grip, so Up on the mapping table is up in the hold. Re-record a D-pad row if it was recorded in a different hold.

---

## Hide from Games Leaves a Handheld's XInput Pad Visible or Hides Its Touchpad

**On a handheld PC such as the Legion Go, Hide from Games (HidHide) on the built-in controller hides its touchpad but games still see the XInput pad, or the pad is hidden and the touchpad went with it.**

1. Update to 4.4.0. The built-in controller is a USB composite device whose XInput node is an interface under the composite parent, and earlier builds never blacklisted the nodes between the HID child and the parent. 4.4.0 hides every interface of the device that HidHide can filter, the XInput node included. The Xbox 360 wireless receiver has the same shape.
2. An interface that shows on the [Devices](features/devices.md) page as its own row, such as the touchpad, follows its own **Hide from Games (HidHide)** switch. Leave it off and the touchpad stays visible while the pad is hidden. Turn it on to hide the touchpad too.
3. A program that opened the pad before the entry landed keeps it until the pad reconnects. A built-in pad never reconnects by itself, so PadForge reconnects it in Windows when it hides it, and a game or launcher started first loses it. A USB device that carries a second controller is left alone. To have the pad hidden from the moment Windows starts, turn on **Keep Devices Cloaked Between Launches** in [Settings](features/settings.md).
4. **Hide Devices from Games** in [Settings](features/settings.md) is the master switch. With it off, no per-device switch does anything.
5. Add any emulator or tool that must still see the physical pad under **Whitelisted Applications** in [Settings](features/settings.md). PadForge whitelists itself.

---

## Dual-Connected DualSense Goes Silent over USB

**A DualSense paired over Bluetooth is plugged in by USB. Input moves to the cable, but the pad's speaker, headphone jack, and microphone go silent while Windows shows the audio endpoint as active.**

1. This is the pad's firmware. While a DualSense holds a Bluetooth link and a USB link at once, it mutes its USB audio interface in both directions. Windows reports the endpoint as active and playback runs without error, so no mixer shows the mute.
2. 4.4.0 finishes the transport switch. When PadForge opens the pad's USB audio (controller audio passthrough, or a persona that plays through the pad), it drops the stale Bluetooth link once for that cable session. The drop lands within a few seconds. With no PadForge audio feature in use on the pad, nothing drops the link: connect the pad by one transport instead.
3. A pad whose radio was dropped while the cable is in does not re-link on a PS press. To go back to Bluetooth, unplug the cable, then press PS.
4. Unplugging, using the pad over Bluetooth, and plugging in again earns a fresh drop.
5. This entry covers the DualSense. A wired DualShock 4 has a USB audio path of its own, which PadForge routes with **Output Path** on the **Audio** card, in the **Lights & Sound** section ([Controller Audio](https://padforge.org/docs/features/controller-audio/)).
6. When the engine stops, or PadForge crashes, PadForge puts every Sony pad it had moved off the headphone route back on it, so Windows audio to the pad keeps playing.

---

## GameMaker Game Drops to 5 FPS (Sonic UltraSaturn)

**A GameMaker game such as Sonic UltraSaturn falls to about 5 fps within minutes while PadForge runs, and its handle count in Task Manager climbs by the thousand every second.**

1. The game's runtime polls `joyGetPosEx` for unplugged joystick ids every frame, and Windows answers each call with a full HID rescan. When two HID devices carry the same vendor and product id, that rescan leaks a DirectInput calibration registry key handle per device per call, about 1,200 handles a second, until the game crawls. The game does this with or without PadForge. PadForge's part is the second identity.
2. The Xbox slot's default profile, Xbox Series X|S Controller (Bluetooth), carries the same id (045E:0B13) as a real Xbox Series pad connected over Bluetooth. With such a pad present, pick another Xbox profile for the controller. The Xbox 360 wired and Xbox Elite Series 2 profiles ran flat on the bench.
3. A DualSense connected over Bluetooth and USB at once is also two devices with one id. Connect it by one transport, or let 4.4.0's dual-link drop finish the switch (see the entry above).
4. Two real pads of the same model trigger the same path. Nothing in PadForge changes that.

---

## Removing PadForge and HIDMaestro

**You want PadForge and what it installed off the PC.**

1. Deleting `PadForge.exe` leaves HIDMaestro, the driver behind PadForge's virtual controllers, on the PC. Before you delete PadForge, find the **HIDMaestro** card in **Settings** and select **Uninstall**. It shows what HIDMaestro left, then stops the engine and removes HIDMaestro's virtual controllers, driver, certificate, registry keys and files ([Driver Management](features/driver-management.md#uninstall)).
2. Uninstall HidHide, SteamVR and the Windows MIDI Services runtime from their cards on the same page if you installed them, and turn off **Start at Login**, which removes the `PadForge` task from Task Scheduler.
3. If you used Automation, open it from **Extras**. Select **Remove** on each row of the **Assistants** card that says **Added**, which takes PadForge's entry and its skill out of that program. Under the **Scripts** card's **More**, select **Remove All**, which takes the script libraries out of Documents and out of each Python's user site. Under the **Assistants** card's **More**, turn off **Reach PadForge From This Network**, which removes the firewall rule **PadForge Assistants on This Network** and its address reservation ([Assistants](features/assistants.md), [Scripts](features/scripts.md)).
4. Close PadForge and delete its folder ([Installation](https://padforge.org/docs/start/installation/#uninstalling)). Then delete `C:\ProgramData\PadForge`, which holds the files PadForge unpacks and downloads. Deleting it needs administrator rights.
5. Already deleted PadForge? Download `HIDMaestro-v2.4.0.zip` (`HIDMaestro-v2.4.0-arm64.zip` on Windows on ARM), or the same files of a later version, from the [HIDMaestro releases](https://github.com/hifihedgehog/HIDMaestro/releases/latest) and extract it. In a terminal opened as administrator, change to its `HIDMaestroUninstall` folder and run `.\HIDMaestroUninstall.exe`. Add `--include-usbip` to also remove usbip-win2 that an older PadForge set up with usbip-win2's own installer, its USB root hub filter included. Taking the filter off restarts every USB 3 root hub, so USB devices disconnect for a moment. Then uninstall HidHide (on an x64 PC) and the Windows MIDI Services runtime in Windows under **Settings → Apps → Installed apps**, delete the SteamVR folder PadForge installed (`C:\SteamVR` unless you picked another), and delete the `PadForge` task in Task Scheduler and `C:\ProgramData\PadForge`. PadForge's entry and skill in an assistant program, the script libraries and the firewall rule **PadForge Assistants on This Network** stay until you remove them by hand.
6. Each item the removal keeps is listed with the reason. A USB persona's device record waits for the next restart, and a removal run after it takes the record. A program that still uses HIDMaestro blocks the removal until you close it.

---

## Update Won't Check or Install

**The Updates card in Settings reports an error, or an update did not install.**

1. *GitHub is limiting requests from this network* means everyone behind the network's shared address used up GitHub's allowance of 60 requests an hour. Wait an hour, or check from another network.
2. *Could not check for updates* followed by a reason means GitHub could not be reached or answered with an error. Check the connection, then click **Check Now**.
3. *The download did not match the checksum GitHub published for it* means the file was damaged on the way, and PadForge did not install it. Click **Install and Restart** to download it again. With **Install Updates Automatically** on, **Check Now** downloads it again too.
4. An update set to install at the next start that does not finish is reported once, and its staged copy is dropped. Install it from the card with **Install and Restart**.
5. When the new version does not start, the message names the folder under `%TEMP%\PadForge_Update` that holds a copy of the old `PadForge.exe`. Copy that file over the installed one to go back.
6. Versions 4.5.2 and older have no updater. Download 4.5.3 or later once from the [latest release](https://github.com/hifihedgehog/PadForge/releases/latest).

[Updates](https://padforge.org/docs/features/updates/) lists every message the card can show.

---

## Logs and diagnostics

PadForge writes no log files on its own. A healthy session leaves nothing on disk unless you ask for it. Next to `PadForge.exe`:

- `PadForge.xml` holds your settings.
- `crash.log` appears only after a crash. It records the error plus a tail of recent internal diagnostics. This is the file to attach to a bug report, and the crash message says where reports go.

If PadForge has not crashed there is no `crash.log`, and that is normal.

**Capturing a trace.** For a problem that is hard to reproduce, open **Settings** and find the **Diagnostics** card.

- **Record a Problem** records everything PadForge sees while you make the problem happen, then opens the recording for you to read, part by part, before you save it. See [Problem Recorder](features/problem-recorder.md), which also covers memory dumps and sending a file privately.
- **Save a Snapshot…** takes PadForge, Windows, your devices and settings as they are now, with the engine's most recent events, and opens them through the same review. It works even with logging off, because those events are always held in memory, so it is the right button right after something goes wrong. A snapshot also holds HidHide's flags and list, every present XInput interface and each virtual controller's HID node, which is what to attach for a controller that vanishes while HidHide runs. **Save…** in the review writes it where you pick, to Downloads the first time.
- **Keep a Diagnostics Log** records continuously to `diagnostics.log` in the folder PadForge runs from. It stays on across restarts, so use it for something that only appears minutes into a session or on a PadForge that starts with Windows. Turn it back off when you are done.

`PADFORGE_DIAG` still works for benches. PadForge runs with administrator rights, and a variable set in an ordinary console does not reach it. With PadForge closed, open PowerShell as administrator, set the variable to the full path of `diag.log` in the folder PadForge runs from, and start PadForge.exe from that window: `$env:PADFORGE_DIAG = 'C:\PadForge\diag.log'`, then `C:\PadForge\PadForge.exe`. The same trace goes there for that session only. PadForge takes no other file.

---

## Quick Reference

| Symptom | Fix |
|---|---|
| No devices | Check USB, cable, Device Manager |
| Detected but no mapping | Assign to a controller, check the Source list |
| No virtual controller in game | HIDMaestro installs itself. Turn the controller off and on to run a failed setup again. A MIDI slot on the legacy MIDI API needs an Output Port picked |
| Nintendo slot not in game | XInput-only games need an Xbox slot |
| Keyboard, mouse or touchpad dead in Windows | **Restore the Windows Driver** or **Give Back to Windows** on its Devices row. A touchpad returns when the engine stops |
| Imported reWASD or DS4Windows profile off | Read the preview's Partial and Skipped reasons, Load it, assign your controller |
| Double input | Install HidHide, enable "Hide Devices from Games" |
| BLE controller not hidden | Enable per-device "Hide from Games" and "Hide Devices from Games" in Settings |
| Wrong button mapping | Force Raw Joystick Mode, re-record |
| Elite paddles do nothing over USB | Windows 11 24H2 or 25H2 at build 26100.8973 or later, or pair an Elite Series 2 over Bluetooth |
| Opposite buttons vanish | SOCD cleaning. Set Mode to Off or remove the pair |
| No rumble | Overall Gain > 0%, motor strength > 0%, Test Rumble |
| Audio bass rumble flat | Check audio playing, raise sensitivity |
| Bass shakers silent | Check the Bass Shakers status line. Game feedback and Test Rumble route to audio, macro rumble does not |
| Extended controller not in joy.cpl | Start the engine, turn the Extended controller on and assign an online device to it. A controller with no device creates nothing. Turn it off and on to run a failed HIDMaestro setup again |
| HIDMaestro left after removing PadForge | **Uninstall** on the HIDMaestro row in Settings, under Drivers, first, or `HIDMaestroUninstall.exe` from the HIDMaestro release |
| Extended FFB silent | Game must send HID PID 1.0 effects, check gain |
| MIDI tile dimmed | No MIDI API started. Read the Windows MIDI Services row in Settings, under Drivers |
| No MIDI output | Select "PadForge MIDI N" in the DAW, or under the legacy MIDI API the port picked on the controller's page |
| No motion in emulator | Turn on Motion Server in Extras, match port, use 127.0.0.1 |
| DSU port in use | Close the other program, and the server starts within 3 seconds, or change the port |
| Web controller disconnected | Same network, check firewall, change the port on the Web Controller page |
| iOS Safari drops the web controller | Keep Safari in the foreground or install the page as a PWA |
| Remote Link PC not appearing | Same network, enable on both, allow firewall, or connect by address |
| Remote Link pairing fails | Codes must match on both screens, then retry the handshake |
| Remote Link device missing after pairing | Check the pairing is active, assign the shared device to a controller |
| Remote Link feedback or keys missing | Device must support the feature, clear **Gamepad Only** for keys |
| No NFC reader detected | Start the Smart Card service, use a PC/SC (CCID) reader |
| Media keys not mapping | Keyboard needs a media-key device, input only, no suppress |
| Wii IR pointer centered | Aim at a sensor bar or IR source, map IR Pointer X/Y |
| Joy-Con 2 mouse silent | Connect over Bluetooth, map Mouse Motion X/Y |
| Switch 2 pad drops over Bluetooth | It has 12 s to come back. Keep a Diagnostics Log shows the BLE Switch2 lines |
| Battery not showing | Wired/unsupported devices report none, refreshes every 5 s |
| Won't auto-disconnect when idle | Bluetooth only, off by default, set timeout in minutes |
| Haptic tone garbled on speech/music | Single actuator plays one tone, only beeps and cues survive |
| Profile not switching | Verify exe path, enable auto-switching |
| Sensitivity curve no effect | Check axis (X vs Y), preset not Linear |
| Settings lost | Wait for autosave, check file permissions |
| UAC prompt every launch | Expected while Start at Login is off. PadForge always runs as administrator. |
| App crashes on start | Check `crash.log` next to PadForge.exe, re-download the exe |
| High CPU | Lower the Polling Rate in Settings |
| Steam conflict | Disable Steam Input or close Steam |
| Macro doesn't fire | All triggers simultaneous, check threshold, mode, and Layer |
| Mouse macro not working | Check axis source, adjust sensitivity |
| Macro won't stop | A Toggle macro releases on the second press. Otherwise disable the macro or turn the controller off |
| Keyboard+Mouse not working | Check the controller's type, add mappings |
| Mouse buttons not registering | Record the button on a mapping row |
| Stick drift after deadzone | Calibrate Center on the Sticks card |
| Flick Stick not turning camera | Map Flick Stick (Right Stick) to Mouse X |
| Volume macro wrong direction | Enable Invert Axis |
| Volume indicator unwanted | Turn off Show Volume Indicator |
| DualShock 3 not connecting | Pair through PadForge, unplug, press PS over Bluetooth |
| Shift layer stuck or won't engage | Check mode, Delay, Fire on Release, axis threshold, activator online. A Toggle layer releases on a second tap or via Auto-Cancel |
| Impulse Triggers card missing | Select an Xbox One/Elite/Series pad under Driven by. DualSense uses the Adaptive Triggers card |
| Custom Expression macro silent | Record every variable, read the status line. Rising-edge means it must cross 0.5 |
| Touchpad Overlay no input in game | The controller must be PlayStation type. A real DualSense or DS4 in the slot overrides the overlay |
| Touchpad pressure in steps | Synthetic Pressure is on. Turn it off for raw readings |
| Wii Remote drifts or turns wrong in an emulator | Set Motion Passthrough to Raw, set Held As to the hold |
| Handheld XInput pad still visible after hiding | 4.4.0 hides composite interfaces. PadForge reconnects the pad when it hides it, so launchers lose it |
| Dual-connected DualSense silent over USB | Firmware mute. 4.4.0 drops the stale Bluetooth link, or connect by one transport |
| GameMaker game at 5 fps | Two devices share one id. Pick another Xbox profile, one transport per pad |
| Update won't check or install | Read the card's message. See [Update Won't Check or Install](#update-wont-check-or-install) |

---

## Related Pages

- [Installation](https://padforge.org/docs/start/installation/): First-time setup guide
- [Settings](features/settings.md): Engine and driver configuration
- [Driver Management](features/driver-management.md): Driver installation
- [Home](features/dashboard.md): Engine status and the controllers
- [Controller Slots](features/controller-slots.md): Virtual controller creation and management
- [Devices](features/devices.md): Device detection and input state
- [Button and Axis Mappings](features/mappings.md): Input mapping configuration
- [Stick Deadzones](features/stick-deadzones.md): Thumbstick deadzone and calibration
- [Trigger Deadzones](features/trigger-deadzones.md): Trigger range and deadzone
- [Force Feedback](features/force-feedback.md): Rumble configuration
- [Macros](guides/macros.md): Triggers and actions
- [Profiles](guides/profiles.md): Per-application profiles
- [DSU Motion Server](https://padforge.org/docs/reference/dsu-motion-server/): Motion data for emulators
- [Web Controller](guides/web-controller.md): Browser-based virtual controller
- [Remote Link](guides/remote-link.md): Share a controller between PCs, on your network or across the internet
- [Import from reWASD, DS4Windows and DSX](guides/profile-import.md): What each import row means and what to do about it
- [Wii Controllers](https://padforge.org/docs/devices/wii-controllers/): Holds, emulator setup, and the Motion Plus limitation

---

*Last updated for PadForge 6.0.0.*

<!-- https://padforge.org/docs/features/scripts/ as PadForge's skill carries it, from padforge.org's wiki/features/scripts.md. -->
# Scripts

*Drive PadForge from AutoHotkey v2, Python or PowerShell with one object model: read any device, hold its controls, take a button from the game with a hotkey, and add a controller of the script's own.*

A script reaches PadForge through [Automation](automation.md), at the level set there. Each language has PadForge's own library, one file, with the same model in all three: a device is an object, and its properties are its controls, in plain units.

!!! warning "Anti-cheat"
    Some online games' anti-cheat flags a player while AutoHotkey runs, or while a script drives the game. Check a game's rules before a script drives it.

<!-- SCREENSHOT: extras-automation-scripts (the Scripts card with one library current, one older and one not installed) -->

---

## Install a library

The **Scripts** card on the Automation page has a row for each language. Each row checks, as you, whether its library is installed:

| Line | Button |
|---|---|
| **Not installed.** | **Install** |
| **Installed and current. A script starts with …** | **Open Folder** |
| **Installed. This PadForge has a newer copy.** | **Update** |

**Install** writes the library with your own rights, even while PadForge runs as administrator, where the language finds it:

| Language | Where | A script starts with |
|---|---|---|
| AutoHotkey v2 | `Documents\AutoHotkey\Lib\PadForge.ahk` | `#Include <PadForge>` |
| PowerShell | Both module folders, `Documents\PowerShell\Modules` and `Documents\WindowsPowerShell\Modules` | `Import-Module PadForge` |
| Python | The user site of each Python found | `import padforge` |

Documents moved to OneDrive counts. With several Pythons, the row names which ones the library is installed for. Under the card's **More**, **Save a Library…** writes one library where you pick, and **Remove All** takes out every installed copy. `PadForge.exe --install autohotkey`, `powershell` or `python` does the same from the [command line](../reference/command-line.md#-install), with `--remove` or `--check`.

Turn on Automation at the level the script needs: **Read** to follow devices, **Send Inputs** to hold controls, take them in hotkeys and add controllers. A script that asks for more than Commands asks once, on PadForge's question card, which names its interpreter and its script file.

---

## Devices and their controls

A device is found by its script ID, its type (`gamepad`, `keyboard`, `mouse`, `wheel`, `joystick`, `program` and so on) or a part of its name:

=== "AutoHotkey"

    ```autohotkey
    pad := PadForge.Device("gamepad")
    ```

=== "Python"

    ```python
    pad = padforge.device("gamepad")
    ```

=== "PowerShell"

    ```powershell
    $pad = Get-PadForgeDevice gamepad
    ```

Its properties are its controls by the names scripts use: `A`, `B`, `LeftX`, `RightTrigger`, `DPad` for a pad, `MouseX` and `MouseLeft` for a mouse, `KeyA` for a keyboard, and `Axis0`, `Slider0`, `Hat0` and `Button0` for any device. They also go by number: `pad.Button[0]`, `pad.Axis[0]`, `pad.Slider[0]`, `pad.Hat[0]`. Python and PowerShell read names without regard to case. A name that is also a method's reads by index, as `pad["Set"]`.

To find a device's script ID and its control names, select it on the **Devices** page and open **More** under **Test Inputs**. **Script ID** shows the ID with **Copy**, and a line lists the device's controls by their script names. A keyboard's keys are `KeyA`, `KeySpace`, `KeyLeftShift` and the like, and every key by its code, `Vk0` to `Vk255`. The ID is the same in every run and names nothing of the device's serial number or path.

Values are plain units:

| Control | Value |
|---|---|
| Button | true or false |
| Stick | -100 to 100, 0 at rest |
| Trigger, slider | 0 to 100 |
| Hat | Degrees clockwise from up, or -1 at rest. `HatUp`, `HatRight`, `HatDown` and `HatLeft` (`DPadUp` and so on, on a pad) read as buttons |
| Half an axis | `LeftXPlus` and `LeftXMinus`, 0 to 100 each |

A read costs nothing: the library keeps the device's whole state, which PadForge sends in full when the script starts following it. A device that is not connected reads nothing (`""` in AutoHotkey, `None` in Python, `$null` in PowerShell), and its `Connected` property is false.

Every device PadForge knows can be read, keyboards and mice included.

---

## Holds and timing

Setting a property holds the control at that value for the game. Setting it to nothing gives it back:

| | AutoHotkey | Python | PowerShell |
|---|---|---|---|
| Hold | `pad.A := true` | `pad.A = True` | `$pad.A = $true` |
| Give back | `pad.A := ""` or `pad.Release("A")` | `pad.A = None` or `pad.release("A")` | `$pad.A = $null` or `$pad.Release('A')` |
| Put a value once | `pad.Set("LeftX", 40)` | `pad.set("LeftX", 40)` | `$pad.Set('LeftX', 40)` |
| Tap | `pad.Tap("A", 50)` | `pad.tap("A", 50)` | `$pad.Tap('A', 50)` |
| Taps in a row | `pad.Sequence("A", 50, "B", 50)` | `pad.sequence("A", 50, "B", 50)` | `$pad.Sequence('A', 50, 'B', 50)` |

In Python, `False` and `0` are values like any other, so `pad.A = False` holds A up.

**Set** puts a value once, until the device's own input moves that control. **Tap** and **Sequence** are timed on PadForge's own clock, not the script's. PadForge applies one change per control per pass, so a tap sent between two of its passes still lands, the press on one pass and the release on the next.

### A script's life

A script and PadForge start in either order. While PadForge is not running, the library says why it waits and keeps trying. When PadForge starts again, the library puts back what the script held, claimed and followed, its controllers and its values.

The library beats on a timer of its own, every 31 ms, so a script busy in its own code keeps its holds. A script that stops beating for 250 ms lets go of what it held and claimed, and gets it back when it beats again. Its controllers go offline after 5 seconds of silence, and a second and a half after its pipe closes unless it comes back.

---

## Hotkeys

Hotkeys take AutoHotkey's own forms, in all three languages:

| Hotkey | Fires |
|---|---|
| `"A"` | When A is pressed. A is taken from the game while the hotkey exists. |
| `"~A"` | When A is pressed, and A stays the game's too. |
| `"A up"` | When A is let go. |
| `"HatUp"` | When the hat or D-pad goes up. |
| `"LeftX > 80%"`, `"LeftX < -80%"` | When the axis crosses the line, either side of center. |
| `"LeftX > 80% up"` | When it comes back over the line. |

PadForge decides a claim inside its own pass, so the game never sees a press the hotkey took. The callback gets the hotkey's name. `pad.Hotkey("A", "Off")` removes one. A hotkey that takes a control from the game needs **Send Inputs**.

A script also hears what happens in PadForge: a device that comes or goes (`OnDevice`), the profile (`OnProfile`), the engine (`OnEngine`) and the level (`OnLevel`), each control's changes (`pad.OnChange`) and the device's connection (`pad.OnConnect`). Python spells these `on_device`, `on_change` and so on, and PowerShell has `Register-PadForgeDeviceChange`, `Register-PadForgeProfile`, `Register-PadForgeEngine` and `Register-PadForgeLevel` beside the device's own `OnChange` and `OnConnect`.

In Python, callbacks run one at a time, in order, on a thread of the library's own, and a script that only waits for them ends with `padforge.wait()`. In PowerShell, script blocks run on the script's own thread while `Wait-PadForge` waits, or whenever the script calls the module. A script with a loop of its own calls `Wait-PadForge -Seconds 0.01` in it.

### Values, commands and tools

- `SetValue("pull", 40)` hands a named value to Custom Expression macro triggers. It is set again after PadForge restarts.
- The commands of the [Commands level](automation.md#the-level): switch and release a profile, start and stop the engine, turn controllers on and off, show and hide the window, take a snapshot, and read the state.
- `Outputs(1)` reads what virtual controller 1 sends the game now.
- `Devices()` lists every device with its controls.
- `Tool("press", '{"id":"..."}')` runs any [assistant tool](assistants.md), at the tool's own level.

---

## A controller of the script's own

At **Send Inputs**, a script can add a controller of its own and drive it. It shows on the Devices page like any device and takes mappings. Its key keeps its identity, and its mappings, across runs.

A declaration can name buttons, axes (`rest` 0 for a pedal, 50 for a stick), sliders and hats, and add a gyro and an accelerometer, touchpads with their finger counts, a battery, and the feedback it wants: `rumble`, `trigrumble`, `trigfx`, `light` and `player`. The script hears the game's rumble, trigger rumble, trigger effects, light and player number (`OnRumble`, `OnTriggerRumble`, `OnTriggerEffects`, `OnLight`, `OnPlayer`). The example [A controller of its own](#a-controller-of-its-own) adds a wheel.

---

## Examples

Each example is one of the files PadForge's tests run against a test pipe, in each language. The comment at the top says what it does and the level it needs.

### Tap and turbo

=== "AutoHotkey"

    ```autohotkey
    ; Tap and turbo: A taps B for the game, and while X is held the game sees
    ; Y pressed and let go ten times a second. Needs Send Inputs.
    #Requires AutoHotkey v2.0
    #Include <PadForge>

    pad := PadForge.Device("gamepad")

    ; A belongs to the script: the game never sees it, and sees B tapped.
    pad.Hotkey("A", (*) => pad.Tap("B", 60))

    ; X stays the game's too ("~"), and drives Y while it is held.
    pad.Hotkey("~X", (*) => SetTimer(Turbo, 100))
    pad.Hotkey("~X up", (*) => SetTimer(Turbo, 0))
    Turbo() => pad.Tap("Y", 40)

    Persistent
    ```

=== "Python"

    ```python
    # Tap and turbo: A taps B for the game, and while X is held the game sees
    # Y pressed and let go ten times a second. Needs Send Inputs.
    import threading
    import time

    import padforge

    pad = padforge.device("gamepad")

    # A belongs to the script: the game never sees it, and sees B tapped.
    pad.hotkey("A", lambda: pad.tap("B", 60))

    # X stays the game's too ("~"), and drives Y while it is held.
    turbo = threading.Event()
    pad.hotkey("~X", turbo.set)
    pad.hotkey("~X up", turbo.clear)


    def run_turbo():
        while True:
            turbo.wait()
            pad.tap("Y", 40)
            time.sleep(0.1)


    threading.Thread(target=run_turbo, daemon=True).start()
    padforge.wait()
    ```

=== "PowerShell"

    ```powershell
    # Tap and turbo: A taps B for the game, and while X is held the game sees
    # Y pressed and let go ten times a second. Needs Send Inputs.
    Import-Module PadForge

    $pad = Get-PadForgeDevice gamepad

    # A belongs to the script: the game never sees it, and sees B tapped.
    $pad.Hotkey('A', { $pad.Tap('B', 60) })

    # X stays the game's too ("~"), and drives Y while it is held.
    $turbo = $false
    $pad.Hotkey('~X', { $script:turbo = $true })
    $pad.Hotkey('~X up', { $script:turbo = $false })
    while ($true) {
        if ($turbo) { $pad.Tap('Y', 40) }
        Wait-PadForge -Seconds 0.1
    }
    ```

### A combo on one button

=== "AutoHotkey"

    ```autohotkey
    ; A combo on one button: Back plays A, B, then X, each held 50 ms, timed on
    ; PadForge's own clock. Needs Send Inputs.
    #Requires AutoHotkey v2.0
    #Include <PadForge>

    pad := PadForge.Device("gamepad")
    pad.Hotkey("Back", (*) => pad.Sequence("A", 50, "B", 50, "X", 50))

    ; The left trigger, pulled past 90%, holds the right one full until it
    ; comes back.
    pad.Hotkey("~LeftTrigger > 90%", (*) => pad.RightTrigger := 100)
    pad.Hotkey("~LeftTrigger > 90% up", (*) => pad.RightTrigger := "")

    Persistent
    ```

=== "Python"

    ```python
    # A combo on one button: Back plays A, B, then X, each held 50 ms, timed on
    # PadForge's own clock. Needs Send Inputs.
    import padforge

    pad = padforge.device("gamepad")
    pad.hotkey("Back", lambda: pad.sequence("A", 50, "B", 50, "X", 50))

    # The left trigger, pulled past 90%, holds the right one full until it
    # comes back.
    pad.hotkey("~LeftTrigger > 90%", lambda: pad.hold("RightTrigger", 100))
    pad.hotkey("~LeftTrigger > 90% up", lambda: pad.release("RightTrigger"))

    padforge.wait()
    ```

=== "PowerShell"

    ```powershell
    # A combo on one button: Back plays A, B, then X, each held 50 ms, timed on
    # PadForge's own clock. Needs Send Inputs.
    Import-Module PadForge

    $pad = Get-PadForgeDevice gamepad
    $pad.Hotkey('Back', { $pad.Sequence('A', 50, 'B', 50, 'X', 50) })

    # The left trigger, pulled past 90%, holds the right one full until it
    # comes back.
    $pad.Hotkey('~LeftTrigger > 90%', { $pad.RightTrigger = 100 })
    $pad.Hotkey('~LeftTrigger > 90% up', { $pad.RightTrigger = $null })

    Wait-PadForge
    ```

### A stick as keys

=== "AutoHotkey"

    ```autohotkey
    ; The left stick as W, A, S and D, for a game that takes only keys. Each
    ; direction is held down while the stick is past 50% that way. Needs Read.
    #Requires AutoHotkey v2.0
    #Include <PadForge>

    pad := PadForge.Device("gamepad")

    Bind(line, key) {
        ; "~" leaves the stick to the game as well.
        pad.Hotkey("~" line, (*) => Send("{" key " down}"))
        pad.Hotkey("~" line " up", (*) => Send("{" key " up}"))
    }
    Bind("LeftY < -50%", "w")
    Bind("LeftY > 50%", "s")
    Bind("LeftX < -50%", "a")
    Bind("LeftX > 50%", "d")

    Persistent
    ```

=== "Python"

    ```python
    # The left stick as the D-pad, for a game that reads only the D-pad. Each
    # direction is held while the stick is past 50% that way. The AutoHotkey
    # example presses W, A, S and D. Python has no built-in for keys, so this
    # one holds the pad's own D-pad. Needs Send Inputs.
    import padforge

    pad = padforge.device("gamepad")
    held = set()


    def show():
        # A diagonal holds two directions, as in "Up+Left". None gives the
        # D-pad back to the pad.
        pad.DPad = "+".join(d for d in ("Up", "Right", "Down", "Left") if d in held) or None


    def bind(line, direction):
        def press():
            held.add(direction)
            show()

        def let_go():
            held.discard(direction)
            show()

        # "~" leaves the stick to the game as well.
        pad.hotkey("~" + line, press)
        pad.hotkey("~" + line + " up", let_go)


    bind("LeftY < -50%", "Up")
    bind("LeftY > 50%", "Down")
    bind("LeftX < -50%", "Left")
    bind("LeftX > 50%", "Right")

    padforge.wait()
    ```

=== "PowerShell"

    ```powershell
    # The left stick as W, A, S and D, for a game that takes only keys. Each
    # direction holds a button of a controller of the script's own while the
    # stick is past 50% that way: PowerShell has no command of its own that
    # presses a key, so map the four buttons to keys on PadForge's Devices
    # page. Needs Send Inputs.
    Import-Module PadForge

    $keys = Add-PadForgeController stickkeys 'Stick Keys' -Buttons W, A, S, D
    $pad = Get-PadForgeDevice gamepad

    # "~" leaves the stick to the game as well.
    $pad.Hotkey('~LeftY < -50%', { $keys.W = $true })
    $pad.Hotkey('~LeftY < -50% up', { $keys.W = $false })
    $pad.Hotkey('~LeftY > 50%', { $keys.S = $true })
    $pad.Hotkey('~LeftY > 50% up', { $keys.S = $false })
    $pad.Hotkey('~LeftX < -50%', { $keys.A = $true })
    $pad.Hotkey('~LeftX < -50% up', { $keys.A = $false })
    $pad.Hotkey('~LeftX > 50%', { $keys.D = $true })
    $pad.Hotkey('~LeftX > 50% up', { $keys.D = $false })

    Wait-PadForge
    ```

### A stick as the mouse

=== "AutoHotkey"

    ```autohotkey
    ; The right stick moves the mouse, faster the further it goes, and the
    ; right shoulder clicks. A read of the stick costs nothing: the library
    ; keeps the pad's whole state. Needs Send Inputs, since the shoulder is
    ; taken from the game.
    #Requires AutoHotkey v2.0
    #Include <PadForge>

    pad := PadForge.Device("gamepad")
    pad.Hotkey("RightShoulder", (*) => Click())

    SetTimer(Aim, 10)
    Aim() {
        x := pad.RightX, y := pad.RightY
        if (x = "")                 ; not connected
            return
        if (Abs(x) > 10 || Abs(y) > 10)
            MouseMove(x * Abs(x) / 500, y * Abs(y) / 500, 0, "R")
    }

    Persistent
    ```

=== "Python"

    ```python
    # The right stick aims, faster the further it goes, and the right shoulder
    # fires. The AutoHotkey example moves the mouse and clicks. Python has no
    # built-in for that, so this one drives a controller of the script's own:
    # map its Aim X and Aim Y to a game's camera on PadForge's Devices page. A
    # read of the stick costs nothing: the library keeps the pad's whole state.
    # Needs Send Inputs, since the shoulder is taken from the game.
    import time

    import padforge

    aim = padforge.add_controller("pyaim", "Aim Pad", buttons=["Fire"], axes=[{"name": "Aim X"}, {"name": "Aim Y"}])
    pad = padforge.device("gamepad")
    pad.hotkey("RightShoulder", lambda: aim.tap("Fire", 50))


    def curve(v):
        # Nothing near the center, then faster the further the stick goes.
        return 0 if abs(v) <= 10 else v * abs(v) / 100


    last = None
    while True:
        x, y = pad.RightX, pad.RightY
        if x is not None:               # None while the pad is not connected
            now = (curve(x), curve(y))
            if now != last:
                aim.hold("AimX", now[0], "AimY", now[1])
                last = now
        time.sleep(0.01)
    ```

=== "PowerShell"

    ```powershell
    # The right stick aims, faster the further it goes, and the right shoulder
    # fires. PowerShell has no command of its own that moves the mouse, so this
    # script drives a controller of its own instead: map its Aim X and Aim Y to
    # the game's aim on PadForge's Devices page. A read of the stick costs
    # nothing: the library keeps the pad's whole state. Needs Send Inputs,
    # since the shoulder is taken from the game.
    Import-Module PadForge

    $aim = Add-PadForgeController aimpad 'Aim Pad' -Buttons Fire -Axes @{ name = 'Aim X' }, @{ name = 'Aim Y' }
    $pad = Get-PadForgeDevice gamepad
    $pad.Hotkey('RightShoulder', { $aim.Tap('Fire', 50) })

    while ($true) {
        $x = $pad.RightX
        $y = $pad.RightY
        if ($null -ne $x) {
            # Past a tenth of the way, the further the stick goes the faster.
            if ([Math]::Abs($x) -le 10) { $x = 0 }
            if ([Math]::Abs($y) -le 10) { $y = 0 }
            $aim.Hold('AimX', $x * [Math]::Abs($x) / 100, 'AimY', $y * [Math]::Abs($y) / 100)
        }
        Wait-PadForge -Seconds 0.01
    }
    ```

### Events and a value for a macro

=== "AutoHotkey"

    ```autohotkey
    ; What happens in PadForge, and a value for a macro: a device that comes
    ; or goes and a profile that changes show in a tooltip, and the left
    ; trigger's pull is handed to Custom Expression triggers as "pull".
    ; Needs Read, and Send Inputs for the value.
    #Requires AutoHotkey v2.0
    #Include <PadForge>

    PadForge.OnDevice((id, online) => ToolTip(id (online ? " connected" : " disconnected")))
    PadForge.OnProfile((name) => ToolTip("Profile: " name))

    pad := PadForge.Device("gamepad")
    pad.OnChange("LeftTrigger", (value, *) => PadForge.SetValue("pull", value))
    pad.OnConnect((connected) => ToolTip(pad.Name (connected ? " is here" : " went")))

    Persistent
    ```

=== "Python"

    ```python
    # What happens in PadForge, and a value for a macro: a device that comes
    # or goes and a profile that changes are printed, and the left trigger's
    # pull is handed to Custom Expression triggers as "pull". Needs Read, and
    # Send Inputs for the value.
    import padforge


    def device_changed(device_id, online):
        print(device_id, "connected" if online else "disconnected", flush=True)


    padforge.on_device(device_changed)
    padforge.on_profile(lambda name: print("Profile:", name, flush=True))

    pad = padforge.device("gamepad")
    pad.on_change("LeftTrigger", lambda value: padforge.set_value("pull", value))
    pad.on_connect(lambda connected: print(pad.name, "is here" if connected else "went", flush=True))

    padforge.wait()
    ```

=== "PowerShell"

    ```powershell
    # What happens in PadForge, and a value for a macro: a device that comes
    # or goes and a profile that changes show on the console, and the left
    # trigger's pull is handed to Custom Expression triggers as "pull".
    # Needs Read, and Send Inputs for the value.
    Import-Module PadForge

    Register-PadForgeDeviceChange { param($id, $online) Write-Host ($id + $(if ($online) { ' connected' } else { ' disconnected' })) }
    Register-PadForgeProfile { param($name) Write-Host "Profile: $name" }

    $pad = Get-PadForgeDevice gamepad
    $pad.OnChange('LeftTrigger', { param($value) Set-PadForgeValue pull $value })
    $pad.OnConnect({ param($connected) Write-Host ($pad.Name + $(if ($connected) { ' is here' } else { ' went' })) })

    Wait-PadForge
    ```

### A controller of its own

=== "AutoHotkey"

    ```autohotkey
    ; A controller of the script's own: a wheel that a pad's shoulders turn and
    ; its triggers drive, with a battery, motion and a touchpad, which hears the
    ; game's rumble. Map it on PadForge's Devices page like any other. Needs
    ; Send Inputs.
    #Requires AutoHotkey v2.0
    #Include <PadForge>

    wheel := PadForge.AddController("padwheel", "Pad Wheel", {
        Buttons: ["Shift Up", "Shift Down"],
        Axes: [{name: "Steer"}, {name: "Gas", rest: 0}, {name: "Brake", rest: 0}],
        Gyro: true, Touchpads: [2], Battery: true,
        Feedback: ["rumble", "trigfx"]})
    wheel.Battery := 100
    wheel.OnRumble((low, high) => ToolTip("rumble " low " " high))

    pad := PadForge.Device("gamepad")
    pad.Hotkey("LeftShoulder", (*) => wheel.Steer := Max(-100, wheel.Steer - 10))
    pad.Hotkey("RightShoulder", (*) => wheel.Steer := Min(100, wheel.Steer + 10))
    pad.Hotkey("Y", (*) => wheel.Tap("ShiftUp", 80))
    pad.Hotkey("A", (*) => wheel.Tap("ShiftDown", 80))
    pad.OnChange("RightTrigger", (value, *) => wheel.Gas := value)
    pad.OnChange("LeftTrigger", (value, *) => wheel.Brake := value)

    Persistent
    ```

=== "Python"

    ```python
    # A controller of the script's own: a wheel that a pad's shoulders turn and
    # its triggers drive, with a battery, motion and a touchpad, which hears the
    # game's rumble. Map it on PadForge's Devices page like any other. Needs
    # Send Inputs.
    import padforge

    wheel = padforge.add_controller(
        "padwheel", "Pad Wheel",
        buttons=["Shift Up", "Shift Down"],
        axes=[{"name": "Steer"}, {"name": "Gas", "rest": 0}, {"name": "Brake", "rest": 0}],
        gyro=True, touchpads=[2], battery=True,
        feedback=["rumble", "trigfx"])
    wheel.battery = 100
    wheel.on_rumble(lambda low, high: print("rumble", low, high, flush=True))

    pad = padforge.device("gamepad")


    def steer(by):
        wheel.Steer = max(-100, min(100, wheel.Steer + by))


    def gas(value):
        wheel.Gas = value


    def brake(value):
        wheel.Brake = value


    pad.hotkey("LeftShoulder", lambda: steer(-10))
    pad.hotkey("RightShoulder", lambda: steer(10))
    pad.hotkey("Y", lambda: wheel.tap("ShiftUp", 80))
    pad.hotkey("A", lambda: wheel.tap("ShiftDown", 80))
    pad.on_change("RightTrigger", gas)
    pad.on_change("LeftTrigger", brake)

    padforge.wait()
    ```

=== "PowerShell"

    ```powershell
    # A controller of the script's own: a wheel that a pad's shoulders turn and
    # its triggers drive, with a battery, motion and a touchpad, which hears the
    # game's rumble. Map it on PadForge's Devices page like any other. Needs
    # Send Inputs.
    Import-Module PadForge

    $wheel = Add-PadForgeController padwheel 'Pad Wheel' -Buttons 'Shift Up', 'Shift Down' `
        -Axes @{ name = 'Steer' }, @{ name = 'Gas'; rest = 0 }, @{ name = 'Brake'; rest = 0 } `
        -Gyro -Touchpads 2 -Battery -Feedback rumble, trigfx
    $wheel.Battery = 100
    $wheel.OnRumble({ param($low, $high) Write-Host "rumble $low $high" })

    $pad = Get-PadForgeDevice gamepad
    $pad.Hotkey('LeftShoulder', { $wheel.Steer = [Math]::Max(-100, $wheel.Steer - 10) })
    $pad.Hotkey('RightShoulder', { $wheel.Steer = [Math]::Min(100, $wheel.Steer + 10) })
    $pad.Hotkey('Y', { $wheel.Tap('ShiftUp', 80) })
    $pad.Hotkey('A', { $wheel.Tap('ShiftDown', 80) })
    $pad.OnChange('RightTrigger', { param($value) $wheel.Gas = $value })
    $pad.OnChange('LeftTrigger', { param($value) $wheel.Brake = $value })

    Wait-PadForge
    ```

See also: [Automation](automation.md), [Plugins](plugins.md), [Command Line](../reference/command-line.md).

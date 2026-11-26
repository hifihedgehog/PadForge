; PadForge Automation for AutoHotkey v2. Speaks protocol 1 of PadForge's
; Automation pipe, PadForge.Automation.
;
; Install it from PadForge with Install on the AutoHotkey v2 row of the
; Scripts card (Extras, Automation), then a script anywhere starts with
; #Include <PadForge>.
; Turn on Automation at the level the script needs: Read to follow devices,
; Send Inputs to hold controls, take them in hotkeys and add controllers.
;
; Example:
;
;   #Requires AutoHotkey v2.0
;   #Include <PadForge>
;   pad := PadForge.Device("gamepad")
;   pad.Hotkey("A", (*) => pad.Tap("B", 50))      ; A presses B for the game
;   pad.Hotkey("LeftX > 80%", (*) => ToolTip("right"))
;   pad.Hotkey("Back", (*) => pad.RightTrigger := 100)    ; holds it
;   pad.Hotkey("Back up", (*) => pad.RightTrigger := "")  ; gives it back
;   Persistent
;
; A device is an object found by its script ID, its type (gamepad, keyboard,
; mouse, wheel, joystick, program...) or a part of its name. Its properties
; are its controls by the names scripts use, the names its Script ID row on
; PadForge's Devices page lists: A, B, LeftX, RightTrigger, DPad for a pad,
; MouseX and MouseLeft for a mouse, KeyA for a keyboard, Axis0, Slider0,
; Hat0 and Button0 for any device. By number: pad.Button[0], pad.Axis[0],
; pad.Slider[0], pad.Hat[0]. A name that is also a method's, such as Set,
; reads as pad["Set"].
;
; Values are plain units: a button is true or false, a stick runs from -100
; to 100 with 0 at rest, a trigger or slider from 0 to 100, a hat is degrees
; clockwise from up or -1 at rest, and its directions read as HatUp,
; HatRight, HatDown and HatLeft (DPadUp on a pad). LeftXPlus and LeftXMinus
; are the two halves of an axis, 0 to 100 each. A read costs nothing: the
; library keeps the device's whole state, which PadForge sends in full when
; the script starts following it. A device that is not connected reads ""
; and its Connected property is false.
;
; Setting a property holds the control at that value for the game:
; pad.A := true. "" gives it back: pad.A := "", or pad.Release("A").
; pad.Set("LeftX", 40) puts a value once, until the device's own input moves
; the control. pad.Tap("A", 50) presses A for 50 ms and pad.Sequence("A", 50,
; "B", 50) taps one after the other, timed on PadForge's own clock, and a tap
; sent between two of PadForge's passes is never lost.
;
; Before the device is found, and while PadForge is not running, a property
; set, Hold and Set wait under the control's name and apply when the device
; is found, a Set once. A Tap or a Sequence throws then, since a tap sent
; later is not the one the script asked for: "PadForge is not connected"
; while PadForge does not run, and "the device is not connected" while it
; runs and has no device that matches. PadForge refuses a Tap or a Sequence
; on a device it has that is not connected, in the same words, and the
; library throws its refusal. A Tap or a Sequence on the script's own
; controller throws the same way until PadForge has added it.
;
; A hat's direction is set as a button is: pad.DPadUp := true presses Up
; and false holds it released, beside the hat's other directions. The hat
; itself, held or the device's own, is what the held directions go over, so
; pad.DPad := "Down" with DPadRight held reads Down and Right. Up and Down
; together cancel, as Left and Right do.
;
; Hotkeys take AutoHotkey's own forms: pad.Hotkey("A", fn) takes A from the
; game while the hotkey exists, "~A" leaves it to the game too, and "A up",
; "HatUp", "LeftX > 80%", "LeftX < -80%" and "LeftX > 80% up" work. fn gets
; the hotkey's name. pad.Hotkey("A", "Off") removes it. PadForge decides the
; claim inside its own pass, so the game never sees a press the hotkey took.
;
; The script and PadForge start in either order. While PadForge is not
; running, PadForge.Why says so and the library keeps trying, and when
; PadForge starts again it puts back what the script held, claimed and
; followed. The library beats every 31 ms. A script that stops beating for
; 250 ms lets go of what it held and claimed until it beats again, and its
; controllers go offline after 5 seconds of silence.
;
; PadForge posts the message PadForge.Wake to this script's window after it
; writes, and the library then reads everything that waits in the pipe. The
; message only wakes the script, and PadForge posts it again while the
; script says nothing, so one AutoHotkey drops loses nothing. Hotkeys work
; whichever window is active.
;
; A plugin is a program PadForge starts itself (Extras, Plugins). It connects
; with PadForge.Plugin(name), which reads the one-time key and the pipe's
; name PadForge put in its environment. A plugin declares, drives and removes
; its own controllers whatever Automation's level, and writes to its log on
; the Plugins page with Log.
;
; Some online games' anti-cheat flags a player while AutoHotkey runs. Check
; a game's rules before a script drives it.

#Requires AutoHotkey v2.0

class PadForge {
    static Protocol := 1
    static DefaultPipe := "PadForge.Automation"
    static BeatMs := 31
    static RetryMs := 1000

    ; ── The connection every device uses ──

    ; The script's connection, made on first use with the script's name.
    static Default {
        get {
            if !PadForge.HasOwnProp("_default")
                PadForge._default := PadForge(RegExReplace(A_ScriptName, "\.[^.]*$"), PadForge._Pipe())
            return PadForge._default
        }
    }

    ; Makes the script's connection with a name and the level it asks for:
    ; "commands", "read", "send-inputs" (the default) or "change-settings". With the
    ; reach "own" the script asks to drive and change only the devices it
    ; adds and the ones the user picks for it, and it still reads every
    ; device. Call it before anything else to choose them.
    static Connect(name := "", level := "send-inputs", pipe := "", reach := "") {
        if PadForge.HasOwnProp("_default")
            PadForge._default.Close()
        PadForge._default := PadForge(name != "" ? name : RegExReplace(A_ScriptName, "\.[^.]*$"), pipe != "" ? pipe : PadForge._Pipe(), level, "", 0, reach)
        return PadForge._default
    }

    ; The pipe the script's connection opens: PadForge's own, or the one
    ; PADFORGE_PIPE names, as a test's host serves.
    static _Pipe() {
        pipe := EnvGet("PADFORGE_PIPE")
        return pipe != "" ? pipe : PadForge.DefaultPipe
    }

    static Device(what) => PadForge.Default.Device(what)
    static Devices() => PadForge.Default.Devices()
    static AddController(key, name, spec := "", axes := "", sliders := "", hats := "", feedback := "")
        => PadForge.Default.AddController(key, name, spec, axes, sliders, hats, feedback)
    static OnDevice(callback) => PadForge.Default.OnDevice(callback)
    static OnProfile(callback) => PadForge.Default.OnProfile(callback)
    static OnEngine(callback) => PadForge.Default.OnEngine(callback)
    static OnLevel(callback) => PadForge.Default.OnLevel(callback)
    static SetValue(name, value) => PadForge.Default.SetValue(name, value)
    static SwitchProfile(name) => PadForge.Default.SwitchProfile(name)
    static ReleaseProfile() => PadForge.Default.ReleaseProfile()
    static Engine(on) => PadForge.Default.Engine(on)
    static Controllers(on) => PadForge.Default.Controllers(on)
    static Controller(number, on) => PadForge.Default.Controller(number, on)
    static Show() => PadForge.Default.Show()
    static Hide() => PadForge.Default.Hide()
    static Snapshot() => PadForge.Default.Snapshot()
    static State() => PadForge.Default.State()
    static Outputs(number) => PadForge.Default.Outputs(number)
    static Tool(name, json := "{}") => PadForge.Default.Tool(name, json)
    static Connected => PadForge.Default.Connected
    static Why => PadForge.Default.Why
    static Level => PadForge.Default.Level

    ; Opens a plugin's connection: PadForge started this script, and its
    ; environment holds the one-time key and the pipe's name. The key is
    ; taken once, so a plugin connects once for each start and does not
    ; try again.
    static Plugin(name, lease := 2000) {
        token := EnvGet("PADFORGE_PLUGIN_TOKEN")
        if (token = "")
            throw Error("PadForge did not start this program as a plugin. Add it on the Plugins page.")
        pipe := EnvGet("PADFORGE_PIPE")
        pf := PadForge(name, pipe != "" ? pipe : PadForge.DefaultPipe, "", token, lease)
        if !pf.Connected
            throw Error(pf.Why)
        return pf
    }

    ; A connection to the PadForge that runs on this PC. It does not wait:
    ; while PadForge is not running, Connected is false, Why says why, and
    ; the library tries again every second.
    __New(name, pipe := PadForge.DefaultPipe, level := "send-inputs", token := "", lease := 0, reach := "") {
        this.Name := name
        this.Pipe := pipe
        this.Wanted := level
        this.WantedReach := reach
        this.Token := token
        this.Lease := lease
        this.Answers := Map()
        this.Callbacks := Map()
        this.Events := []
        this.Pending := ""
        ; The bytes after the last whole line, decoded once their line ends.
        this.Carry := Buffer(0)
        this.NextTag := 0
        this.Level := ""
        this.Version := ""
        this.Session := 0
        this.Errors := []
        this.Why := "Not connected yet."
        this.Wanting := Map()
        this.ById := Map()
        this.Mine := Map()
        this.Values := Map()
        this.Follows := Map()
        this.Dispatching := false
        this.Restoring := false
        this.NextTry := 0
        this.Waiting := false
        ; The hello said PadForge asks the user: the level the answer gives
        ; is heard as one e level, and what the script asked for meanwhile
        ; was held, not dropped.
        this.ConsentPending := false
        this.WakeMessage := DllCall("RegisterWindowMessage", "Str", "PadForge.Wake", "UInt")
        ; Bound once, so a timer or a message can be turned off by the same
        ; function. AutoHotkey's names ignore case, so these must not share
        ; a name with a method.
        this.WakeFn := ObjBindMethod(this, "_OnWake")
        this.BeatFn := ObjBindMethod(this, "_OnBeat")
        this.Handle := 0
        this.Closed := true
        this.Ended := false
        OnMessage(this.WakeMessage, this.WakeFn)
        this._TryOpen()
        if (token = "")
            SetTimer(this.BeatFn, PadForge.BeatMs)
        else if this.Connected
            SetTimer(this.BeatFn, Max(31, lease // 3))
    }

    Connected => !this.Closed

    ; ── Opening, and opening again ──

    _TryOpen() {
        this.NextTry := A_TickCount + PadForge.RetryMs
        try {
            h := this._Open(this.Pipe)
        } catch as e {
            this.Why := e.Message
            return false
        }
        this.Handle := h
        this.Closed := false
        this.Pending := ""
        this.Carry := Buffer(0)
        kind := this.Token != "" ? "kind=plugin token=" PadForge.Quote(this.Token) : "kind=script"
        hello := "hello name=" PadForge.Quote(this.Name) " " kind " protocol=" PadForge.Protocol " units=plain hwnd=" A_ScriptHwnd
        if (this.Wanted != "")
            hello .= " level=" this.Wanted
        if (this.WantedReach != "")
            hello .= " reach=" this.WantedReach
        if (this.Lease > 0)
            hello .= " lease=" this.Lease
        try {
            answer := this.Request(hello)
        } catch as e {
            this._Closed()
            this.Why := e.Message
            return false
        }
        this.Version := answer.Get("padforge", "")
        this.Level := answer.Get("level", "")
        this.Session := Integer(answer.Get("session", "0"))
        this.Waiting := answer.Get("consent", "") = "waiting"
        this.ConsentPending := this.Waiting
        this.Why := this.Waiting ? "PadForge asks you whether this script may work with it." : ""
        if (this.Token = "")
            this._Restore()
        this.Waiting := false
        ; A pipe that closed during the restore keeps its line.
        if !this.Closed
            this.Why := ""
        return this.Connected
    }

    ; Everything the script holds, claims and follows, put back on a new
    ; connection: its devices found again, its controllers declared again
    ; under the same keys, its values set again.
    ; A step PadForge refuses goes to Errors, and the next one still runs.
    _Restore() {
        this.Restoring := true
        try {
            for kind in this.Follows
                this._Step(ObjBindMethod(this, "Request", "sub " kind))
            this._Step(ObjBindMethod(this, "Request", "sub devices"))
            this._Step(ObjBindMethod(this, "_Relist"))
            for key, ctl in this.Mine
                this._Step(ObjBindMethod(ctl, "_Declare"))
            for name, value in this.Values
                this._Send("var " name "=" value)
            for _, pad in this.Wanting
                this._Step(ObjBindMethod(pad, "_Find"))
        } finally {
            this.Restoring := false
        }
    }

    _Step(fn) {
        try fn()
        catch as e
            this._Note(e.Message)
    }

    ; The level rose from below Read or Send Inputs, where PadForge let go
    ; of what the script followed, held and claimed and took its controllers
    ; offline: all of it is put back as a new connection puts it back, from
    ; a clean slate.
    _Rise() {
        if this.Closed
            return
        this._Step(ObjBindMethod(this, "Request", "unsub all"))
        for id, pad in this.ById
            if !(pad is PadForgeController)
                pad._followed := false
        this._Restore()
    }

    ; Where a level stands among the four: Commands (and off) 0, Read 1, Send
    ; Inputs 2, Change Settings 3.
    static _Rank(word) => word = "read" ? 1 : word = "send-inputs" ? 2 : word = "change-settings" ? 3 : 0

    ; Every device the script listed takes its connection from PadForge's
    ; list again, on a new connection.
    _Relist() {
        if (this.ById.Count = 0)
            return
        for row in this.Request("devices").Rows {
            if (row.Words.Length = 0 || row.Words[1] != "device")
                continue
            p := row.Pairs
            if (this.ById.Has(p["id"]) && !(this.ById[p["id"]] is PadForgeController))
                this.ById[p["id"]]._online := p.Get("online", "0") = "1"
        }
    }

    ; The pipe is opened at the identification level (SECURITY_SQOS_PRESENT
    ; with SECURITY_IDENTIFICATION), so a server that is not PadForge cannot
    ; act as this script's user, and nothing is sent until its owner is
    ; PadForge's: Administrators or the system, which a program without
    ; administrator rights cannot give a pipe it made under PadForge's name.
    ; A pipe of another name, which a test serves, is taken from this
    ; script's own user too.
    _Open(pipe) {
        path := "\\.\pipe\" pipe
        start := A_TickCount
        loop {
            h := DllCall("CreateFile", "Str", path, "UInt", 0xC0000000, "UInt", 0, "Ptr", 0, "UInt", 3, "UInt", 0x00110000, "Ptr", 0, "Ptr")
            if (h != -1) {
                if !PadForge._ServedByPadForge(h, pipe) {
                    DllCall("CloseHandle", "Ptr", h)
                    throw Error("Another program holds the pipe " pipe ". It is not PadForge, so nothing was sent to it.")
                }
                return h
            }
            err := A_LastError
            if (err = 231 && A_TickCount - start < 2000) {   ; ERROR_PIPE_BUSY
                DllCall("WaitNamedPipe", "Str", path, "UInt", 500)
                continue
            }
            if (err = 2) {                                  ; ERROR_FILE_NOT_FOUND
                if (pipe != PadForge.DefaultPipe)
                    throw Error("Nothing serves the pipe " pipe ".")
                if PadForge.Runs()
                    throw Error("PadForge is running, but Automation is off. Turn it on in Extras, Automation.")
                throw Error("PadForge is not running.")
            }
            throw OSError(err)
        }
    }

    ; Whether PadForge runs: its window PadForge.Instance stands, made by a
    ; process with administrator rights, which a program that made one
    ; without them does not have. Every window of the name is looked at, so
    ; windows another program makes cannot hide PadForge's, and each
    ; process's rights are read once.
    static Runs() {
        after := 0
        seen := Map()
        while (after := DllCall("FindWindowEx", "Ptr", -3, "Ptr", after, "Ptr", 0, "Str", "PadForge.Instance", "Ptr")) {
            pid := 0
            DllCall("GetWindowThreadProcessId", "Ptr", after, "UInt*", &pid)
            if !seen.Has(pid)
                seen[pid] := PadForge.Integrity(pid) >= 0x3000
            if seen[pid]
                return true
        }
        return false
    }

    ; A process's integrity level's RID, or -1.
    static Integrity(pid) {
        process := DllCall("OpenProcess", "UInt", 0x1000, "Int", 0, "UInt", pid, "Ptr")
        if !process
            return -1
        level := -1
        token := 0
        if DllCall("advapi32\OpenProcessToken", "Ptr", process, "UInt", 8, "Ptr*", &token) {
            size := 0
            DllCall("advapi32\GetTokenInformation", "Ptr", token, "Int", 25, "Ptr", 0, "UInt", 0, "UInt*", &size)
            if size {
                buf := Buffer(size, 0)
                if DllCall("advapi32\GetTokenInformation", "Ptr", token, "Int", 25, "Ptr", buf, "UInt", size, "UInt*", &size) {
                    sid := NumGet(buf, 0, "Ptr")
                    count := NumGet(DllCall("advapi32\GetSidSubAuthorityCount", "Ptr", sid, "Ptr"), 0, "UChar")
                    level := NumGet(DllCall("advapi32\GetSidSubAuthority", "Ptr", sid, "UInt", count - 1, "Ptr"), 0, "UInt")
                }
            }
            DllCall("CloseHandle", "Ptr", token)
        }
        DllCall("CloseHandle", "Ptr", process)
        return level
    }

    ; Whether PadForge serves the pipe: its owner is Administrators or the
    ; system. A pipe of another name than PadForge's own (the Automation
    ; pipe and the control pipe), which only a test serves, is also taken
    ; from this script's own user. Windows reads a pipe's name without
    ; regard to case, and so does != here.
    static _ServedByPadForge(h, pipe) {
        owner := PadForge.PipeOwner(h)
        if (owner = "S-1-5-32-544" || owner = "S-1-5-18")
            return true
        return pipe != PadForge.DefaultPipe && pipe != "PadForge.Control" && owner != "" && owner = PadForge.OwnUser()
    }

    ; The SID of a pipe's owner as text, or "".
    static PipeOwner(h) {
        owner := 0, sd := 0
        if DllCall("advapi32\GetSecurityInfo", "Ptr", h, "Int", 6, "UInt", 1, "Ptr*", &owner, "Ptr", 0, "Ptr", 0, "Ptr", 0, "Ptr*", &sd, "UInt")
            return ""
        text := PadForge.SidText(owner)
        DllCall("LocalFree", "Ptr", sd)
        return text
    }

    static SidText(sid) {
        p := 0
        if !sid || !DllCall("advapi32\ConvertSidToStringSidW", "Ptr", sid, "Ptr*", &p)
            return ""
        s := StrGet(p, "UTF-16")
        DllCall("LocalFree", "Ptr", p)
        return s
    }

    ; The SID of the user this script runs as, or "".
    static OwnUser() {
        token := 0
        if !DllCall("advapi32\OpenProcessToken", "Ptr", DllCall("GetCurrentProcess", "Ptr"), "UInt", 8, "Ptr*", &token)
            return ""
        size := 0
        DllCall("advapi32\GetTokenInformation", "Ptr", token, "Int", 1, "Ptr", 0, "UInt", 0, "UInt*", &size)
        if !size {
            DllCall("CloseHandle", "Ptr", token)
            return ""
        }
        buf := Buffer(size, 0)
        ok := DllCall("advapi32\GetTokenInformation", "Ptr", token, "Int", 1, "Ptr", buf, "UInt", size, "UInt*", &size)
        DllCall("CloseHandle", "Ptr", token)
        ; TOKEN_USER opens with the pointer to the user's SID.
        return ok ? PadForge.SidText(NumGet(buf, 0, "Ptr")) : ""
    }

    ; ── Words on the line ──

    static Quote(text) {
        text := StrReplace(String(text), "\", "\\")
        text := StrReplace(text, '"', '\"')
        text := StrReplace(StrReplace(text, "`r", " "), "`n", " ")
        return '"' text '"'
    }

    ; A word for the line: bare when it can be, quoted otherwise, so a key
    ; or a name with a space reaches PadForge whole.
    static Word(text) {
        text := String(text)
        return (text = "" || RegExMatch(text, '[ "\={`r`n]')) ? PadForge.Quote(text) : text
    }

    ; The words of a line: .Words holds the bare ones, .Pairs the key=value
    ; ones, .Json the JSON the line ends with.
    static Split(line) {
        words := [], pairs := Map(), json := ""
        pairs.CaseSense := false
        i := 1, n := StrLen(line)
        while (i <= n) {
            while (i <= n && SubStr(line, i, 1) = " ")
                i++
            if (i > n)
                break
            if (SubStr(line, i, 1) = "{") {
                json := SubStr(line, i)
                break
            }
            key := "", hasKey := false, value := "", quoted := false
            while (i <= n && (quoted || SubStr(line, i, 1) != " ")) {
                c := SubStr(line, i, 1)
                if quoted {
                    nextChar := SubStr(line, i + 1, 1)
                    if (c = "\" && (nextChar = '"' || nextChar = "\")) {
                        value .= nextChar, i += 2
                        continue
                    }
                    if (c = '"')
                        quoted := false
                    else
                        value .= c
                } else if (c = '"') {
                    quoted := true
                } else if (c = "=" && !hasKey) {
                    key := value, value := "", hasKey := true
                } else {
                    value .= c
                }
                i++
            }
            if hasKey
                pairs[key] := value
            else
                words.Push(value)
        }
        return {Words: words, Pairs: pairs, Json: json}
    }

    ; The directions a hat in degrees holds: the nearest of the eight
    ; points, a diagonal holding two, "" at rest.
    static Directions(degrees) {
        if (degrees = "" || degrees < 0)
            return ""
        point := Mod(Round(degrees / 45), 8)
        static names := ["Up", "Up+Right", "Right", "Right+Down", "Down", "Down+Left", "Left", "Up+Left"]
        return names[point + 1]
    }

    ; The directions a hat value as the pipe takes it holds: "45deg",
    ; "Up+Right", or "-1" at rest.
    static HatWords(wire) {
        if (wire = "" || wire = "-1" || wire = "rest")
            return ""
        if InStr(wire, "deg")
            return PadForge.Directions(Number(StrReplace(wire, "deg")))
        return wire
    }

    ; The degrees a hat that holds these directions reads, -1 for none. Up
    ; and Down together cancel, as Left and Right do.
    static DirectionDegrees(words) {
        words := "+" words "+"
        up := InStr(words, "+Up+"), down := InStr(words, "+Down+")
        right := InStr(words, "+Right+"), left := InStr(words, "+Left+")
        if (up && down)
            up := down := 0
        if (right && left)
            right := left := 0
        if up
            return right ? 45 : left ? 315 : 0
        if down
            return right ? 135 : left ? 225 : 180
        return right ? 90 : left ? 270 : -1
    }

    ; words with one direction pressed or released.
    static Turn(words, bit, pressed) {
        words := StrReplace("+" words "+", "+" bit "+", "+")
        return Trim(pressed ? words bit : words, "+")
    }

    ; A value as the pipe takes it for a control of a kind (b, a or h, or d
    ; for a hat's direction, which takes a button's), in plain units.
    static Wire(kind, value) {
        ; A button is pressed by any number but 0, and by the text 1 or true.
        if (kind = "b" || kind = "d") {
            if (value is String)
                return (Trim(value) = "1" || Trim(value) = "true") ? "1" : "0"
            return (value is Number && value != 0) ? "1" : "0"
        }
        ; An axis is a number, or text: rest, or a percent such as "50%".
        if (kind = "a") {
            if (value = "rest")
                return "rest"
            if (value is String && SubStr(Trim(value), -1) = "%")
                return Trim(value)
            return value "%"
        }
        ; A hat's rest is -1 on the pipe, and a number, or text that reads as
        ; one, is degrees.
        if (value = "rest")
            return "-1"
        if IsNumber(value) {
            degrees := Number(value)
            return degrees < 0 ? "-1" : degrees "deg"
        }
        return value = "" ? "-1" : StrReplace(value, " ", "")
    }

    ; ── The pipe ──

    _Write(line) {
        if this.Closed
            throw Error("PadForge is not connected: " this.Why)
        size := StrPut(line "`n", "UTF-8") - 1
        buf := Buffer(size + 1)
        StrPut(line "`n", buf, "UTF-8")
        if !DllCall("WriteFile", "Ptr", this.Handle, "Ptr", buf, "UInt", size, "UInt*", &written := 0, "Ptr", 0) {
            this._Closed()
            throw Error("PadForge is not connected: the pipe closed.")
        }
    }

    ; A line that needs no answer. While PadForge is not connected it is
    ; dropped: what it changed is put back from what the library keeps.
    _Send(line) {
        if this.Closed
            return false
        try this._Write(line)
        catch
            return false
        return true
    }

    ; Reads everything that waits in the pipe, without blocking, and sorts
    ; each line: answers to their request, events to the devices' copies
    ; and the queue of callbacks.
    _Drain() {
        prev := Critical()
        try {
            loop {
                if this.Closed
                    break
                if !DllCall("PeekNamedPipe", "Ptr", this.Handle, "Ptr", 0, "UInt", 0, "Ptr", 0, "UInt*", &avail := 0, "Ptr", 0) {
                    this._Closed()
                    break
                }
                if (avail = 0)
                    break
                carried := this.Carry.Size
                buf := Buffer(carried + avail)
                if carried
                    DllCall("RtlMoveMemory", "Ptr", buf, "Ptr", this.Carry, "UPtr", carried)
                if !DllCall("ReadFile", "Ptr", this.Handle, "Ptr", buf.Ptr + carried, "UInt", avail, "UInt*", &got := 0, "Ptr", 0) {
                    this._Closed()
                    break
                }
                ; Text is decoded up to the last line's end, so a character
                ; that one read ends inside comes whole with the next.
                total := carried + got, last := total
                while (last > 0 && NumGet(buf, last - 1, "UChar") != 10)
                    last--
                this.Carry := Buffer(total - last)
                if (total > last)
                    DllCall("RtlMoveMemory", "Ptr", this.Carry, "Ptr", buf.Ptr + last, "UPtr", total - last)
                if (last > 0)
                    this.Pending .= StrGet(buf, last, "UTF-8")
                while (at := InStr(this.Pending, "`n")) {
                    line := RTrim(SubStr(this.Pending, 1, at - 1), "`r")
                    this.Pending := SubStr(this.Pending, at + 1)
                    this._Line(line)
                }
            }
        } finally {
            Critical(prev)
        }
    }

    _Line(line) {
        ; The user answered PadForge's question card: a line no longer waits
        ; for it, the restore's own next line included.
        if (SubStr(line, 1, 8) = "e level ")
            this.Waiting := false
        if (SubStr(line, 1, 2) = "e ") {
            ; The copy takes the event at once, and a change the copy holds
            ; already (a second subscription's copy of it) goes no further.
            if this._Mirror(line)
                this.Events.Push(line)
            return
        }
        tag := ""
        if (SubStr(line, 1, 1) = "@") {
            space := InStr(line, " ")
            tag := space ? SubStr(line, 1, space - 1) : line
            line := space ? SubStr(line, space + 1) : ""
        }
        if (tag = "" || !this.Answers.Has(tag)) {
            if (SubStr(line, 1, 4) = "err ")
                this._Note(line)
            return
        }
        answer := this.Answers[tag]
        if (line = "ok" || SubStr(line, 1, 3) = "ok " || SubStr(line, 1, 4) = "err ")
            answer.Final := line
        else
            answer.Rows.Push(line)
    }

    ; An event into the copy of its device. False for a change the copy
    ; holds already.
    _Mirror(line) {
        w := StrSplit(line, " ")
        if (w.Length < 3)
            return true
        id := w[3]
        if !this.ById.Has(id)
            return true
        return this.ById[id]._Take(w, line)
    }

    _OnWake(wParam, lParam, msg, hwnd) {
        this._Drain()
        this._Dispatch()
        return 0
    }

    _OnBeat() {
        if this.Closed {
            if (this.Token != "" || this.Ended)
                return
            if (A_TickCount >= this.NextTry && !this.Restoring)
                this._TryOpen()
            return
        }
        if !this._Send("beat")
            return
        this._Drain()
        this._Dispatch()
    }

    ; Runs the callbacks of the events that wait, in their order. One run
    ; at a time: an event that comes while a callback runs joins the queue
    ; this run empties.
    _Dispatch() {
        if this.Dispatching
            return
        this.Dispatching := true
        try {
            while (this.Events.Length > 0) {
                line := this.Events.RemoveAt(1)
                try this._Event(line)
                catch as e
                    PadForge._Later(e)
            }
        } finally {
            this.Dispatching := false
        }
    }

    ; A callback's error shows as AutoHotkey shows any, after the queue is
    ; done.
    static _Later(e) {
        SetTimer(() => PadForge._Throw(e), -1)
    }

    static _Throw(e) {
        throw e
    }

    _Event(line) {
        e := PadForge.Split(line)
        w := e.Words
        if (w.Length < 2)
            return
        switch w[2] {
            case "b", "h", "a", "x", "state":
                if this.ById.Has(w[3])
                    this.ById[w[3]]._Fire(w)
            case "dev":
                online := w[4] = "online"
                ; A device the script listed keeps its connection current.
                if this.ById.Has(w[3])
                    this.ById[w[3]]._online := online
                this._Call("dev", w[3], online)
                ; A wanted device not found, or found and not connected, is
                ; looked for again: an unplugged pad follows a matching one
                ; that connects.
                if online
                    for _, pad in this.Wanting
                        if (pad.Id = "" || !pad._followed || !pad.Connected)
                            pad._Find()
            case "profile": this._Call("profile", w.Length > 2 ? w[3] : "default")
            case "engine": this._Call("engine", w[3])
            case "level":
                was := this.Level
                this.Level := w[3]
                if this.ConsentPending
                    this.ConsentPending := false
                else if (PadForge._Rank(this.Level) > PadForge._Rank(was) && PadForge._Rank(was) < 2)
                    this._Rise()
                this._Call("level", w[3])
            case "rumble", "trigrumble", "trigfx", "led", "player":
                if this.ById.Has(w[3])
                    this.ById[w[3]]._Feedback(w)
            case "lease":
                ; The lease ran out while the script did not beat: its own
                ; controllers rest. What it set goes back at once.
                if (w.Length > 2 && w[3] = "lapsed")
                    for key, ctl in this.Mine
                        ctl._Resend()
            default: this._Call("event|" w[2], e.Pairs)
        }
    }

    ; callback(pairs) for every event of this kind, with the line's
    ; key=value words as a Map: setting, press and choices for a plugin's
    ; declared settings.
    OnEvent(kind, callback) => this._Add("event|" kind, callback)

    _Call(key, args*) {
        if !this.Callbacks.Has(key)
            return
        for callback in this.Callbacks[key]
            callback(args*)
    }

    ; A line for Errors: the last 100 are kept, as the PowerShell library
    ; keeps them.
    _Note(text) {
        this.Errors.Push(text)
        if (this.Errors.Length > 100)
            this.Errors.RemoveAt(1)
    }

    _Add(key, callback) {
        if !this.Callbacks.Has(key)
            this.Callbacks[key] := []
        this.Callbacks[key].Push(callback)
    }

    _Closed() {
        if this.Closed
            return
        this.Closed := true
        if this.Handle {
            DllCall("CloseHandle", "Ptr", this.Handle)
            this.Handle := 0
        }
        this.Why := this.Ended ? "The script closed the connection."
            : this.Token != "" ? "PadForge closed the connection."
            : "PadForge closed the connection. The library tries again."
        this.NextTry := A_TickCount + PadForge.RetryMs
        for _, pad in this.ById
            pad._Lost()
        if (this.Token != "")
            SetTimer(this.BeatFn, 0)
    }

    ; Sends a line and waits for its answer. Returns the ok line's key=value
    ; pairs as a Map, with .Rows holding the rows before it as objects from
    ; Split. Throws PadForge's refusal in words. A line that waits for the
    ; user's answer to this script's first connection waits as long as
    ; PadForge's question card does.
    Request(line, timeoutMs := 0) {
        if (timeoutMs = 0)
            timeoutMs := this.Waiting ? 150000 : 10000
        tag := "@" (++this.NextTag)
        answer := {Rows: [], Final: ""}
        this.Answers[tag] := answer
        try {
            this._Write(tag " " line)
            start := A_TickCount
            loop {
                this._Drain()
                if (answer.Final != "")
                    break
                if this.Closed
                    throw Error("PadForge is not connected: the pipe closed.")
                if (A_TickCount - start > timeoutMs)
                    throw Error("PadForge did not answer.")
                Sleep(1)
            }
        } finally {
            this.Answers.Delete(tag)
        }
        final := answer.Final
        if (SubStr(final, 1, 4) = "err ")
            throw this._Refused(StrSplit(line, " ")[1], final, -1)
        result := PadForge.Split(SubStr(final, 3))
        pairs := result.Pairs
        pairs.Rows := []
        for row in answer.Rows
            pairs.Rows.Push(PadForge.Split(row))
        pairs.Json := result.Json
        pairs.Words := result.Words
        return pairs
    }

    ; PadForge's refusal of a request, as an error in words, the request
    ; named by verb. frames is the stack frame the error names, counted
    ; from the caller: -1 for the caller itself. A line past the program's
    ; level carries PadForge's default words, and is told as the level the
    ; program works at: the user's cap, or what the user allowed it on the
    ; question card.
    _Refused(verb, final, frames) {
        parts := StrSplit(final, " ", , 3)
        code := parts.Length > 1 ? parts[2] : "internal"
        words := parts.Length > 2 ? parts[3] : ""
        level := PadForge.LevelName(this.Level)
        if (code == "not-allowed" && (words = "" || words == PadForge.LevelRefusal) && level != "")
            return Error("PadForge refused " verb ": this program works at the " level " level, which does not allow it.", frames - 1)
        return Error("PadForge refused " verb ": " words " (" code ").", frames - 1)
    }

    ; The words PadForge's refusal carries when a line is past the
    ; program's level (AutomationLine.WordsFor).
    static LevelRefusal := "the Automation level does not allow this"

    ; A level's word on the pipe by its name, or "".
    static LevelName(word) {
        static names := Map("commands", "Commands", "read", "Read", "send-inputs", "Send Inputs", "change-settings", "Change Settings")
        return names.Get(word, "")
    }

    ; A Tap's or a Sequence's lines, written one after the other with no
    ; wait between them, so their times on PadForge's clock stay as sent,
    ; and then each one's answer. PadForge refuses a timed step on a device
    ; that is not connected: the first refusal is thrown in words, named by
    ; verb, the call's name.
    _Timed(verb, lines*) {
        timeoutMs := this.Waiting ? 150000 : 10000
        tags := [], answers := []
        try {
            for line in lines {
                tag := "@" (++this.NextTag)
                answer := {Rows: [], Final: ""}
                this.Answers[tag] := answer
                tags.Push(tag)
                answers.Push(answer)
                this._Write(tag " " line)
            }
            start := A_TickCount
            loop {
                this._Drain()
                done := true
                for answer in answers
                    if (answer.Final = "") {
                        done := false
                        break
                    }
                if done
                    break
                if this.Closed
                    throw Error("PadForge is not connected: the pipe closed.")
                if (A_TickCount - start > timeoutMs)
                    throw Error("PadForge did not answer.")
                Sleep(1)
            }
        } finally {
            for tag in tags
                this.Answers.Delete(tag)
        }
        for answer in answers
            if (SubStr(answer.Final, 1, 4) = "err ")
                throw this._Refused(verb, answer.Final, -2)
    }

    ; ── Devices ──

    ; A device by its script ID, its type or a part of its name. The same
    ; words give the same object. It is found again whenever PadForge
    ; connects and whenever a device connects while it is missing.
    Device(what) {
        key := StrLower(String(what))
        if this.Wanting.Has(key)
            return this.Wanting[key]
        ; A device Devices() listed is the same object Device() gives for
        ; its script ID.
        if (this.ById.Has(key) && !(this.ById[key] is PadForgeController) && StrLower(this.ById[key]._what) = key) {
            pad := this.ById[key]
            this.Wanting[key] := pad
            pad._wanted := true
            if (this.Connected && !pad._followed)
                pad._Find()
            return pad
        }
        pad := PadForgeDevice(this, what)
        this.Wanting[key] := pad
        pad._wanted := true
        if this.Connected
            pad._Find()
        return pad
    }

    ; Every device PadForge knows, connected or not, as device objects. A
    ; device listed here is the same object Device() gives for its script
    ; ID, and it follows itself on its first use of any kind: a read, a
    ; hotkey, a change callback, a hold, Set, Tap or Sequence.
    Devices() {
        list := []
        for row in this.Request("devices").Rows {
            if (row.Words.Length = 0 || row.Words[1] != "device")
                continue
            p := row.Pairs
            pad := this.ById.Has(p["id"]) ? this.ById[p["id"]] : PadForgeDevice(this, p["id"])
            pad._Row(p)
            list.Push(pad)
        }
        return list
    }

    ; ── Events ──

    ; callback(id, online) when a device connects or disconnects.
    OnDevice(callback) => this._Follow("devices", "dev", callback)

    ; callback(name) when the profile changes, "default" for the default.
    OnProfile(callback) => this._Follow("profile", "profile", callback)

    ; callback(state): running, idle, stopping or stopped.
    OnEngine(callback) => this._Follow("engine", "engine", callback)

    ; callback(level) when the user changes Automation's level.
    OnLevel(callback) => this._Add("level", callback)

    _Follow(kind, key, callback) {
        this._Add(key, callback)
        if !this.Follows.Has(kind) {
            this.Follows[kind] := true
            if this.Connected
                this.Request("sub " kind)
        }
    }

    ; ── Send Inputs ──

    ; Adds a controller of the script's own. key keeps its identity, and its
    ; mappings, across runs. spec is an object with any of: Buttons, Axes,
    ; Sliders and Hats (arrays of names, or of objects with index, name and,
    ; for an axis or slider, rest as a share of its travel, 0 to 100: 50
    ; makes a stick), Gyro and Accel (true), Touchpads (an array of finger
    ; counts), Battery (true) and Feedback (any of "rumble", "trigrumble",
    ; "trigfx", "light" and "player"). The four arrays may also be given in
    ; that order after name.
    AddController(key, name, spec := "", axes := "", sliders := "", hats := "", feedback := "") {
        if (spec is Array)
            spec := {Buttons: spec, Axes: axes = "" ? [] : axes, Sliders: sliders = "" ? [] : sliders,
                Hats: hats = "" ? [] : hats, Feedback: feedback = "" ? [] : feedback}
        if (spec = "")
            spec := {}
        if this.Mine.Has(key)
            return this.Mine[key]
        ctl := PadForgeController(this, key, name, spec)
        this.Mine[key] := ctl
        if this.Connected
            ctl._Declare()
        return ctl
    }

    ; A named value Custom Expression macro triggers read. Set again after
    ; PadForge starts again.
    SetValue(name, value) {
        this.Values[name] := value
        if this.Connected
            this.Request("var " name "=" value)
    }

    ; A line for this plugin's log on the Plugins page. Only a plugin has a
    ; log.
    Log(text) => this._Send("log " PadForge.Quote(text))

    ; ── Commands ──

    SwitchProfile(name) => this.Request("activate " PadForge.Quote(name))
    ReleaseProfile() => this.Request("deactivate")
    Engine(on) => this.Request("engine " (on ? "on" : "off"))
    Controllers(on) => this.Request("controllers " (on ? "on" : "off"))
    Controller(number, on) => this.Request("slot " number " " (on ? "on" : "off"))
    Show() => this.Request("show")
    Hide() => this.Request("hide")
    Snapshot() => this.Request("snapshot")

    ; What runs now, as JSON text.
    State() => this.Request("state").Json

    ; What virtual controller number sends to the game now, as a Map.
    Outputs(number) => this.Request("outputs " number)

    ; Runs an assistant tool, at the tool's own level: Tool("press",
    ; '{"id":"..."}') presses what the user could. Returns JSON text.
    ; A tool that asks the user waits for the card's answer, up to 45
    ; seconds in PadForge, so its line waits 60. JSON written over several
    ; lines goes as one: a line break is a space between JSON's tokens.
    Tool(name, json := "{}") => this.Request("call " PadForge.Word(name) " " StrReplace(StrReplace(json, "`r", " "), "`n", " "), this.Waiting ? 150000 : 60000).Json

    ; Closes the connection for good.
    Close() {
        this.Ended := true
        SetTimer(this.BeatFn, 0)
        if !this.Closed {
            this._Send("bye")
            this._Closed()
        }
        this.Why := "The script closed the connection."
        OnMessage(this.WakeMessage, this.WakeFn, 0)
    }

    __Delete() => this.Close()
}

; A device PadForge reads, found by PadForge.Device. Its properties are its
; controls (see the top of this file).
class PadForgeDevice {
    __New(pf, what) {
        this.DefineProp("_pf", {Value: pf})
        this.DefineProp("_what", {Value: String(what)})
        this.DefineProp("_id", {Value: ""})
        this.DefineProp("_info", {Value: Map("name", "", "type", "")})
        this.DefineProp("_controls", {Value: Map()})
        this.DefineProp("_byIndex", {Value: Map()})
        this.DefineProp("_values", {Value: Map()})
        this.DefineProp("_connected", {Value: false})
        ; Whether PadForge.Devices() listed it online, and whether the
        ; script follows it: a device it follows says it is connected from
        ; its own state, one it only listed from the list.
        this.DefineProp("_online", {Value: false})
        this.DefineProp("_followed", {Value: false})
        this.DefineProp("_wanted", {Value: false})
        this.DefineProp("_holds", {Value: Map()})
        ; Sets made before the device was found, put once when it is.
        this.DefineProp("_sets", {Value: Map()})
        this.DefineProp("_keys", {Value: Map()})
        this.DefineProp("_lines", {Value: Map()})
        this.DefineProp("_changes", {Value: Map()})
        this.DefineProp("_onConnect", {Value: []})
        this.DefineProp("_hats", {Value: Map()})
        this._controls.CaseSense := false
        this._holds.CaseSense := false
        this._sets.CaseSense := false
        this._keys.CaseSense := false
        this._changes.CaseSense := false
    }

    ; Its script ID, "" until it is found.
    Id => this._id
    Name => this._info["name"]
    Type => this._info["type"]
    ; A device the script asked for says it is connected from its own state,
    ; while it is found and after PadForge comes back too. One the script
    ; only listed says what the list said.
    Connected => this._followed || this._wanted ? this._connected : this._online

    ; Its controls: a Map of each name to an object with Name, Kind
    ; ("button", "axis", "slider" or "hat") and Index. A device that
    ; PadForge.Devices() listed reads them on first use.
    Controls {
        get {
            if (this._controls.Count = 0 && this._id != "" && this._pf.Connected && !(this is PadForgeController)) {
                try this._Caps()
                catch as e
                    this._pf._Note(e.Message)
            }
            out := Map()
            for name, c in this._controls
                out[c.Name] := {Name: c.Name, Kind: c.Kind = "b" ? "button" : c.Kind = "h" ? "hat" : c.Index >= 24 ? "slider" : "axis", Index: c.Index}
            return out
        }
    }

    __Get(name, params) => params.Length ? this._Read(name)[params*] : this._Read(name)
    __Set(name, params, value) => this._Hold(name, value)
    __Item[name] {
        get => this._Read(name)
        set => this._Hold(name, value)
    }
    Button[index] {
        get => this._Read("Button" index)
        set => this._Hold("Button" index, value)
    }
    Axis[index] {
        get => this._Read("Axis" index)
        set => this._Hold("Axis" index, value)
    }
    Slider[index] {
        get => this._Read("Slider" index)
        set => this._Hold("Slider" index, value)
    }
    Hat[index] {
        get => this._Read("Hat" index)
        set => this._Hold("Hat" index, value)
    }

    ; ── Finding it ──

    _Find() {
        pf := this._pf
        try rows := pf.Request("devices").Rows
        catch
            return
        want := StrLower(this._what)
        pick := "", spare := ""
        for row in rows {
            if (row.Words.Length = 0 || row.Words[1] != "device")
                continue
            p := row.Pairs
            hit := p["id"] = this._what || StrLower(p["type"]) = want || InStr(StrLower(p["name"]), want)
            if !hit
                continue
            if (p["online"] = "1") {
                pick := p
                break
            }
            if (spare = "")
                spare := p
        }
        if (pick = "")
            pick := spare
        if (pick = "")
            return
        ; The device it follows on this connection already, connected or
        ; not: its state subscription says when it comes back.
        if (this._followed && pick["id"] = this._id)
            return
        old := this._followed ? this._id : ""
        this._Row(pick)
        if (old != "" && old != this._id) {
            ; It followed another device, which is not connected: what it
            ; followed and held there goes, and comes to this one.
            pf._Send("rel " old " all")
            try pf.Request("unsub " old)
            catch as e
                pf._Note(e.Message)
        }
        this._Attach()
    }

    _Row(p) {
        if (this._id != "" && this._id != p["id"] && this._pf.ById.Has(this._id) && this._pf.ById[this._id] = this)
            this._pf.ById.Delete(this._id)
        this._id := p["id"]
        this._info["name"] := p["name"]
        this._info["type"] := p["type"]
        this._online := p.Get("online", "0") = "1"
        this._pf.ById[this._id] := this
    }

    ; Reads its controls, follows its whole state, and puts back what the
    ; script holds and claims on it.
    _Attach() {
        pf := this._pf
        this._Caps()
        pf.Request("sub " this._id " state")
        this._followed := true
        ; Hotkeys and holds made before the controls were known are read
        ; again by the device's own names.
        items := []
        for id, key in this._keys
            for item in key.List
                items.Push(item)
        this._keys.Clear()
        this._lines.Clear()
        for item in items {
            try this.Hotkey(item.Name, item.Fn)
            catch as e
                pf._Note(e.Message)
        }
        ; Change callbacks made before the controls were known are filed
        ; again under each control's own name.
        changes := []
        for name, list in this._changes
            changes.Push([name, list])
        this._changes.Clear()
        for entry in changes {
            c := this._Control(entry[1])
            key := c = "" ? entry[1] : c.Name
            if !this._changes.Has(key)
                this._changes[key] := []
            for callback in entry[2]
                this._changes[key].Push(callback)
        }
        ; A hold whose name or value the device does not take goes to
        ; Errors, and the rest are held.
        line := "", held := Map()
        held.CaseSense := false
        for name, value in this._holds {
            c := this._Target(name)
            if (c = "") {
                pf._Note("No such control: " name)
                continue
            }
            try wire := PadForge.Wire(c.Kind, value)
            catch as e {
                pf._Note(e.Message)
                continue
            }
            held[c.Name] := value
            line .= " " c.Name "=" wire
        }
        this._holds.Clear()
        for name, value in held
            this._holds[name] := value
        if (line != "")
            pf._Send("own " this._id line)
        ; Sets made before the device was found are put once now.
        line := ""
        for name, value in this._sets {
            c := this._Target(name)
            if (c = "") {
                pf._Note("No such control: " name)
                continue
            }
            line .= " " c.Name "=" PadForge.Wire(c.Kind, value)
        }
        this._sets.Clear()
        if (line != "")
            pf._Send("set " this._id line)
    }

    ; A device PadForge.Devices() listed follows itself on its first use
    ; of any kind, a read, a hotkey, a change callback, a hold, Set, Tap or
    ; Sequence, as one PadForge.Device() found does: its controls are
    ; read, its state and connection stay current, and PadForge finds it
    ; again after a restart. A device already wanted is found again by
    ; PadForge's restore and its device events.
    _FollowSelf() {
        if (this._followed || this._id = "" || this is PadForgeController)
            return
        pf := this._pf
        key := StrLower(this._what)
        if pf.Wanting.Has(key)
            return
        pf.Wanting[key] := this
        this._wanted := true
        if !pf.Connected
            return
        try this._Attach()
        catch as e
            pf._Note(e.Message)
    }

    _Caps() {
        this._controls.Clear()
        this._byIndex.Clear()
        for row in this._pf.Request("caps " this._id).Rows {
            if (row.Words.Length = 0)
                continue
            kind := row.Words[1]
            if !(kind = "button" || kind = "axis" || kind = "hat")
                continue
            p := row.Pairs
            k := SubStr(kind, 1, 1)
            index := Integer(p["index"])
            name := p.Has("script") ? p["script"] : PadForgeDevice._Numbered(k, index)
            rest := p.Has("rest") ? Integer(p["rest"]) : (index < 24 ? 32768 : 0)
            c := {Name: name, Kind: k, Index: index, Stick: rest = 32768 || rest = 32767}
            this._controls[name] := c
            this._byIndex[k index] := c
        }
    }

    _Lost() {
        ; PadForge closed the pipe: the device is followed again, and what
        ; the script holds put back, when PadForge has it again, at once or
        ; when it connects after.
        this._followed := false
        this._online := false
        if this._connected {
            this._connected := false
            this._Changed("", false)
        }
    }

    ; ── The copy ──

    ; An event into the copy. False for a change the copy holds already.
    _Take(w, line) {
        switch w[2] {
            case "b":
                key := Integer(w[4]), down := w[5] = "1"
                if (this._values.Has("b" key) && this._values["b" key] = down)
                    return false
                this._values["b" key] := down
            case "h":
                key := Integer(w[4]), deg := Number(w[5])
                if (this._values.Has("h" key) && this._values["h" key] = deg)
                    return false
                this._values["h" key] := deg
            case "a":
                key := Integer(w[4]), v := Number(w[5])
                if (this._values.Has("a" key) && this._values["a" key] = v)
                    return false
                this._values["a" key] := v
            case "state":
                if (w[4] = "offline") {
                    was := this._connected
                    this._connected := false
                    return was
                }
                was := this._connected
                this._State(PadForge.Split(SubStr(line, InStr(line, w[4]))).Pairs)
                this._connected := true
                return !was
        }
        return true
    }

    _State(p) {
        hex := p.Has("b") ? p["b"] : ""
        loop StrLen(hex) // 2 {
            byte := Integer("0x" SubStr(hex, A_Index * 2 - 1, 2))
            base := (A_Index - 1) * 8
            loop 8
                this._values["b" (base + A_Index - 1)] := ((byte >> (A_Index - 1)) & 1) = 1
        }
        for i, v in StrSplit(p.Has("a") ? p["a"] : "", ",")
            this._values["a" (i - 1)] := Number(v)
        for i, v in StrSplit(p.Has("h") ? p["h"] : "", ",") {
            this._values["h" (i - 1)] := Number(v)
            this._hats["h" (i - 1)] := Number(v)
        }
    }

    ; The control a name names: {Name, Kind, Index, Stick}, or "" for none.
    ; A control answers to its name, its numbered name and the pipe's short
    ; form (b3, a0, h0).
    _Control(name) {
        if this._controls.Has(name)
            return this._controls[name]
        if RegExMatch(name, "i)^(Button|Axis|Slider|Hat)(\d+)$", &m) {
            k := m[1] = "Button" ? "b" : m[1] = "Hat" ? "h" : "a"
            index := Integer(m[2]) + (m[1] = "Slider" ? 24 : 0)
            if this._byIndex.Has(k index)
                return this._byIndex[k index]
            return {Name: m[1] m[2], Kind: k, Index: index, Stick: m[1] = "Axis"}
        }
        if RegExMatch(name, "i)^([bah])(\d+)$", &m) {
            k := StrLower(m[1]), index := Integer(m[2])
            if this._byIndex.Has(k index)
                return this._byIndex[k index]
            return {Name: PadForgeDevice._Numbered(k, index), Kind: k, Index: index, Stick: k = "a" && index < 24}
        }
        return ""
    }

    ; A control's numbered name by its kind and its index on the pipe:
    ; sliders from 24.
    static _Numbered(k, index) => k = "b" ? "Button" index : k = "h" ? "Hat" index : index >= 24 ? "Slider" (index - 24) : "Axis" index

    ; What a setting names: a control as _Control gives it, or one direction
    ; of a hat as {Name, Kind: "d", Hat, Bit}, named by its hat's name, or
    ; "". A direction is set as a button is.
    _Target(name) {
        c := this._Control(name)
        if (c != "")
            return c
        d := this._Direction(name)
        if (d = "")
            return ""
        hat := this._byIndex.Get("h" d.Hat, "")
        return {Name: (hat != "" ? hat.Name : "Hat" d.Hat) d.Bit, Kind: "d", Hat: d.Hat, Bit: d.Bit, Stick: false}
    }

    ; A hat direction a name names: {Hat, Bit, Name}, or "".
    _Direction(name) {
        static ends := ["Up", "Right", "Down", "Left"]
        for i, d in ends {
            if (StrLen(name) <= StrLen(d) || SubStr(name, -StrLen(d)) != d)
                continue
            head := SubStr(name, 1, StrLen(name) - StrLen(d))
            if (head = "Hat")
                return {Hat: 0, Bit: d, Name: name}
            c := this._Control(head)
            if (c != "" && c.Kind = "h")
                return {Hat: c.Index, Bit: d, Name: name}
        }
        return ""
    }

    _Read(name) {
        this._FollowSelf()
        if (SubStr(name, -4) = "Plus" || SubStr(name, -5) = "Minus") {
            minus := SubStr(name, -5) = "Minus"
            c := this._Control(SubStr(name, 1, StrLen(name) - (minus ? 5 : 4)))
            if (c != "" && c.Kind = "a") {
                v := this._Value(c)
                if (v = "")
                    return ""
                half := c.Stick ? (minus ? -v : v) : (minus ? (50 - v) * 2 : (v - 50) * 2)
                return Max(0, half)
            }
        }
        c := this._Control(name)
        if (c != "")
            return this._Value(c)
        d := this._Direction(name)
        if (d != "") {
            if !this._connected
                return ""
            return InStr("+" PadForge.Directions(this._values.Get("h" d.Hat, -1)) "+", "+" d.Bit "+") > 0
        }
        ; Not found yet: its controls are not known, and it reads nothing.
        if (this._controls.Count = 0)
            return ""
        throw PropertyError("No such control: " name, -2)
    }

    _Value(c) {
        if !this._connected
            return ""
        return this._values.Get(c.Kind c.Index, c.Kind = "b" ? false : c.Kind = "h" ? -1 : 0)
    }

    ; ── Holding ──

    ; Sets a property: holds the control at the value, or gives it back
    ; for "". A hat's direction is held as a button is, over the hat's
    ; other directions: the hat itself, held or the device's own, with each
    ; held direction pressed or released over it. Before the device is found
    ; its controls are not known yet: the hold waits under the name given.
    _Hold(name, value) {
        this._FollowSelf()
        c := this._Target(name)
        if (c = "" && this._controls.Count > 0)
            throw PropertyError("No such control: " name, -2)
        key := c = "" ? name : c.Name
        ; A hold or a give-back takes the place of a Set that waits.
        if this._sets.Has(key)
            this._sets.Delete(key)
        if (value = "") {
            ; A control the script does not hold gives nothing back.
            if this._holds.Has(key)
                this._holds.Delete(key)
            if (this._id != "" && c != "")
                this._pf._Send("rel " this._id " " key)
            return
        }
        this._holds[key] := value
        if (this._id != "" && c != "")
            this._pf._Send("own " this._id " " key "=" PadForge.Wire(c.Kind, value))
    }

    ; Holds several controls in one change: Hold("LeftX", 50, "LeftY", -20).
    ; "" gives a control back, as setting it to "" does, and before the
    ; device is found each waits under the name given, as Set does.
    Hold(pairs*) {
        if Mod(pairs.Length, 2)
            throw ValueError("hold takes names and values in pairs.", -1)
        this._FollowSelf()
        line := "", back := ""
        loop pairs.Length // 2 {
            name := pairs[A_Index * 2 - 1]
            c := this._Target(name)
            if (c = "" && this._controls.Count > 0)
                throw ValueError("No such control: " name, -1)
            key := c = "" ? name : c.Name
            if this._sets.Has(key)
                this._sets.Delete(key)
            value := pairs[A_Index * 2]
            if (value = "") {
                if this._holds.Has(key)
                    this._holds.Delete(key)
                if (c != "")
                    back .= " " key
                continue
            }
            this._holds[key] := value
            if (c != "")
                line .= " " key "=" PadForge.Wire(c.Kind, value)
        }
        if (this._id != "" && line != "")
            this._pf._Send("own " this._id line)
        if (this._id != "" && back != "")
            this._pf._Send("rel " this._id back)
    }

    ; Gives controls back: Release("A", "LeftX"), or all of them.
    Release(names*) {
        this._FollowSelf()
        if (names.Length = 0) {
            this._holds.Clear()
            this._sets.Clear()
            if (this._id != "")
                this._pf._Send("rel " this._id " all")
            return
        }
        line := ""
        for name in names {
            ; Before the device is found its controls are not known: a hold
            ; or a Set that waits under the name given goes.
            c := this._Target(name)
            if (c = "" && this._controls.Count > 0)
                throw ValueError("No such control: " name, -1)
            key := c = "" ? name : c.Name
            if this._holds.Has(key)
                this._holds.Delete(key)
            if this._sets.Has(key)
                this._sets.Delete(key)
            if (c != "")
                line .= " " key
        }
        if (this._id != "" && line != "")
            this._pf._Send("rel " this._id line)
    }

    ; Puts a value once, until the device's own input moves the control.
    ; Before the device is found its controls are not known yet: the value
    ; waits under the name given, and is put when the device is found.
    ; While PadForge is not connected it waits the same way, as a hold
    ; does, and is put when the device is found again.
    Set(name, value) {
        this._FollowSelf()
        c := this._Target(name)
        if (c = "") {
            if (this._controls.Count > 0)
                throw ValueError("No such control: " name, -1)
            this._sets[name] := value
            return
        }
        if (this._id = "" || !this._pf._Send("set " this._id " " c.Name "=" PadForge.Wire(c.Kind, value)))
            this._sets[c.Name] := value
    }

    ; The refusal of a timed action before the device is found: a tap sent
    ; later, at a moment the script did not choose, is not the tap it asked
    ; for. PadForge's own error while PadForge is not connected, else a
    ; read's of a device that is not connected.
    _Absent(verb) {
        if !this._pf.Connected
            return Error("PadForge is not connected: " this._pf.Why, -2)
        return Error("PadForge refused " verb ": the device is not connected (offline).", -2)
    }

    ; Presses a control for ms milliseconds on PadForge's clock: a button
    ; or a hat's direction, or an axis to value (100 by default) and back.
    ; Refused before the device is found, and by PadForge while the device
    ; is not connected.
    Tap(name, ms := 50, value := "") {
        if (this._id = "")
            throw this._Absent("tap")
        this._FollowSelf()
        c := this._Target(name)
        if (c = "") {
            ; Its controls are not read yet: PadForge is not connected, or
            ; has not given them.
            if (this._controls.Count = 0)
                throw this._Absent("tap")
            throw ValueError("No such control: " name, -1)
        }
        if (value = "")
            value := c.Kind = "b" || c.Kind = "d" ? true : c.Kind = "h" ? 0 : 100
        this._pf._Timed("tap", "own " this._id " " c.Name "=" PadForge.Wire(c.Kind, value) " in=0"
            , "rel " this._id " " c.Name " in=" Round(ms))
    }

    ; Taps controls one after the other, each held its ms: Sequence("A",
    ; 50, "B", 50). Each starts as the one before lets go, every step timed
    ; from the call on PadForge's clock. Refused before the device is found,
    ; and by PadForge while the device is not connected.
    Sequence(steps*) {
        if Mod(steps.Length, 2)
            throw ValueError("sequence takes names and times in pairs.", -1)
        if (this._id = "")
            throw this._Absent("sequence")
        this._FollowSelf()
        lines := [], at := 0
        loop steps.Length // 2 {
            c := this._Target(steps[A_Index * 2 - 1])
            if (c = "") {
                if (this._controls.Count = 0)
                    throw this._Absent("sequence")
                throw ValueError("No such control: " steps[A_Index * 2 - 1], -1)
            }
            on := c.Kind = "b" || c.Kind = "d" ? "1" : c.Kind = "h" ? "0deg" : "100%"
            ; Each step is timed from now (in=), so a Tap or Sequence before
            ; it, which moves the end of the session's timed steps, does not
            ; stretch a step.
            lines.Push("own " this._id " " c.Name "=" on " in=" at)
            at += Round(steps[A_Index * 2])
            lines.Push("rel " this._id " " c.Name " in=" at)
        }
        if (lines.Length > 0)
            this._pf._Timed("sequence", lines*)
    }

    ; ── Hotkeys ──

    ; Hotkey(keyName, callback): see the top of this file. callback gets
    ; the hotkey's name. Hotkey(keyName, "Off") removes it.
    Hotkey(keyName, callback := "") {
        this._FollowSelf()
        h := this._Parse(keyName)
        id := h.Key
        if (callback = "Off") {
            if this._keys.Has(id) {
                key := this._keys[id]
                for i, item in key.List
                    if (item.Name = h.Name) {
                        key.List.RemoveAt(i)
                        break
                    }
                if (key.List.Length = 0) {
                    this._keys.Delete(id)
                    this._Unclaim(key)
                } else
                    this._Claim(key)
            }
            return
        }
        if !HasMethod(callback)
            throw ValueError("A hotkey needs a function or `"Off`": " keyName, -1)
        h.Fn := callback
        fresh := !this._keys.Has(id)
        if fresh
            this._keys[id] := {Id: id, What: h.What, Kind: h.Kind, Line: h.Line, Wire: "", Number: 0, Claimed: -1, List: []}
        key := this._keys[id]
        before := key.List.Clone()
        for i, item in key.List
            if (item.Name = h.Name) {
                key.List.RemoveAt(i)
                break
            }
        key.List.Push(h)
        try this._Claim(key)
        catch as e {
            ; PadForge refused the claim: the hotkeys on the control, and
            ; what it follows, are as they were.
            if fresh
                this._keys.Delete(id)
            else {
                key.List := before
                try this._Claim(key)
                catch as again
                    this._pf._Note(again.Message)
            }
            throw e
        }
    }

    ; A hotkey's words as an object: its name, whether it leaves the
    ; control to the game, on release, and what it follows.
    _Parse(keyName) {
        text := Trim(keyName)
        pass := SubStr(text, 1, 1) = "~"
        if pass
            text := LTrim(SubStr(text, 2))
        up := false
        if RegExMatch(text, "i)\s+(up|down)$", &m) {
            up := m[1] = "up"
            text := RTrim(SubStr(text, 1, m.Pos - 1))
        }
        if !RegExMatch(text, "^(\w+)\s*(?:([<>])\s*(-?\d+(?:\.\d+)?)\s*%?)?$", &m)
            throw ValueError("Not a control: " keyName, -2)
        name := m[1], op := m[2], limit := m[3]
        if (op != "") {
            half := "", base := name
            if (SubStr(name, -4) = "Plus")
                half := "Plus", base := SubStr(name, 1, -4)
            else if (SubStr(name, -5) = "Minus")
                half := "Minus", base := SubStr(name, 1, -5)
            c := this._Control(half = "" ? name : base)
            if (c = "" && half != "")
                c := this._Control(name), half := ""
            if (c = "" && this._controls.Count > 0)
                throw ValueError((this._Direction(name) != "" ? "Only an axis or a slider takes a limit: " : "No such control: ") keyName, -2)
            if (c != "" && c.Kind != "a")
                throw ValueError("Only an axis or a slider takes a limit: " keyName, -2)
            limit := Number(limit)
            low := op = "<"
            if (half != "") {
                stick := c = "" ? true : c.Stick
                if (half = "Minus")
                    limit := stick ? -limit : 50 - limit / 2, low := !low
                else
                    limit := stick ? limit : 50 + limit / 2
            }
            what := (c = "" ? name : c.Name)
            line := (low ? "below=" : "above=") limit "%"
            return {Name: keyName, Pass: pass, Up: up, Kind: "l", What: what, Line: line, Key: what " " line}
        }
        d := this._Direction(name)
        if (d != "")
            return {Name: keyName, Pass: pass, Up: up, Kind: "d", What: d.Name, Hat: d.Hat, Bit: d.Bit, Line: "", Key: d.Name}
        c := this._Control(name)
        if (c = "" && this._controls.Count > 0)
            throw ValueError("No such control: " keyName, -2)
        if (c != "" && c.Kind = "a")
            throw ValueError("An axis or slider hotkey needs a limit, such as LeftX > 80%: " keyName, -2)
        if (c != "" && c.Kind = "h")
            throw ValueError("A hat's directions are its hotkeys, such as " c.Name "Up: " keyName, -2)
        return {Name: keyName, Pass: pass, Up: up, Kind: "b", What: c = "" ? name : c.Name, Line: "", Key: c = "" ? name : c.Name}
    }

    ; Subscribes what a hotkey follows, claimed unless every hotkey on it
    ; leaves it to the game. A line's number comes back in the answer.
    _Claim(key) {
        claim := false
        for item in key.List
            if !item.Pass
                claim := true
        if (this._id = "" || !this._pf.Connected) {
            key.Claimed := -1
            return
        }
        if (key.Kind != "l" && !claim) {
            ; A button or direction the game keeps: the whole state's
            ; subscription carries its events.
            if (key.Claimed = 1)
                this._pf.Request("unsub " this._id " " key.What)
            key.Claimed := 0
            return
        }
        if (key.Kind = "l" && key.Claimed = (claim ? 1 : 0) && key.Number)
            return
        if (key.Kind = "l" && key.Number) {
            this._lines.Delete(key.Number)
            this._pf.Request("unsub " this._id " line=" key.Number)
            ; The line is gone: a refused claim after it leaves no number
            ; that names a line.
            key.Number := 0
        }
        answer := this._pf.Request("sub " this._id " " key.What (key.Line != "" ? " " key.Line : "") (claim ? " claim" : ""))
        key.Claimed := claim ? 1 : 0
        if (key.Kind = "l") {
            key.Number := Integer(answer.Get("line", "0"))
            this._lines[key.Number] := key
        }
    }

    _Unclaim(key) {
        if (this._id = "" || !this._pf.Connected)
            return
        if (key.Kind = "l" && key.Number) {
            this._lines.Delete(key.Number)
            this._pf.Request("unsub " this._id " line=" key.Number)
        } else if (key.Claimed = 1)
            this._pf.Request("unsub " this._id " " key.What)
    }

    ; ── Events ──

    ; callback(value, name) each time a control changes: a button, a hat,
    ; or an axis as it moves, up to 100 times a second.
    OnChange(name, callback) {
        this._FollowSelf()
        c := this._Control(name)
        key := c = "" ? name : c.Name
        if !this._changes.Has(key)
            this._changes[key] := []
        this._changes[key].Push(callback)
    }

    ; callback(connected) when the device connects or disconnects.
    OnConnect(callback) {
        this._FollowSelf()
        this._onConnect.Push(callback)
    }

    _Fire(w) {
        switch w[2] {
            case "b":
                c := this._byIndex.Get("b" w[4], "")
                name := c = "" ? "Button" w[4] : c.Name
                down := w[5] = "1"
                this._Changed(name, down)
                if this._keys.Has(name)
                    for item in this._keys[name].List.Clone()
                        if (item.Up != down)
                            PadForgeDevice._Run(item)
            case "h":
                index := Integer(w[4]), deg := Number(w[5])
                c := this._byIndex.Get("h" index, "")
                this._Changed(c = "" ? "Hat" index : c.Name, deg)
                before := "+" PadForge.Directions(this._hats.Get("h" index, -1)) "+"
                after := "+" PadForge.Directions(deg) "+"
                this._hats["h" index] := deg
                for id, key in this._keys {
                    if (key.Kind != "d" || key.List[1].Hat != index)
                        continue
                    bit := "+" key.List[1].Bit "+"
                    was := InStr(before, bit) > 0, now := InStr(after, bit) > 0
                    if (was = now)
                        continue
                    for item in key.List.Clone()
                        if (item.Up != now)
                            PadForgeDevice._Run(item)
                }
            case "a":
                c := this._byIndex.Get("a" w[4], "")
                this._Changed(c = "" ? PadForgeDevice._Numbered("a", Integer(w[4])) : c.Name, Number(w[5]))
            case "x":
                lineNo := w.Length > 5 ? Integer(w[6]) : 0
                if !this._lines.Has(lineNo)
                    return
                past := w[5] = "1"
                for item in this._lines[lineNo].List.Clone()
                    if (item.Up != past)
                        PadForgeDevice._Run(item)
            case "state":
                this._Changed("", this._connected)
        }
    }

    static _Run(item) {
        fn := item.Fn
        if (fn.MaxParams >= 1 || fn.IsVariadic)
            fn(item.Name)
        else
            fn()
    }

    _Changed(name, value) {
        if (name = "") {
            for callback in this._onConnect
                callback(value)
            return
        }
        if this._changes.Has(name)
            for callback in this._changes[name]
                callback(value, name)
    }

    _Feedback(w) {
    }
}

; A controller of the script's own on PadForge's device list. Map and assign
; it like any device. Setting a property sets that control: ctl.A := true,
; ctl.LeftX := -50, ctl.DPad := "Up". "" puts it at rest.
class PadForgeController extends PadForgeDevice {
    __New(pf, key, name, spec) {
        super.__New(pf, key)
        this.DefineProp("_key", {Value: key})
        this.DefineProp("_label", {Value: name})
        this.DefineProp("_spec", {Value: spec})
        this.DefineProp("_set", {Value: Map()})
        this.DefineProp("_fbs", {Value: Map()})
        ; Battery, Charging, Gyro and Accel as the script set them.
        this.DefineProp("_extras", {Value: Map()})
        this._extras.CaseSense := false
        ; Each declared axis's rest as a share of its travel, 0 to 100.
        this.DefineProp("_rests", {Value: Map()})
        this._set.CaseSense := false
        this._connected := true
        this._Shape()
    }

    Key => this._key
    Name => this._label
    Type => "program"
    Connected => this._id != "" && this._pf.Connected

    ; The words of the pipe a declared name may not take, as PadForge keeps
    ; them.
    static Reserved := PadForgeController._Words("gyro accel bat charging rest after in claim above below hyst line all state buttons hats axes axis hz devices profile engine")

    static _Words(text) {
        words := Map()
        words.CaseSense := false
        for w in StrSplit(text, " ")
            words[w] := true
        return words
    }

    ; The controls named by the declaration, as PadForge names them
    ; (ScriptNames.Declared): buttons, then axes and sliders, then every hat
    ; up to the last one declared, each in the order of its index. The hats
    ; are named first, and each hat's name with Up, Right, Down or Left is
    ; taken with it, so a button or an axis declared so keeps its numbered
    ; name: that word is the hat's direction. A name that is no word, a word
    ; of the pipe, reads as another control or is taken keeps the control's
    ; numbered name.
    _Shape() {
        spec := this._spec
        taken := Map()
        taken.CaseSense := false
        Pick(label, numbered) {
            word := PadForgeController._Word(label)
            if (word = "" || taken.Has(word) || PadForgeController.Reserved.Has(word) || PadForgeController._LooksNumbered(word))
                word := numbered
            taken[word] := true
            return word
        }
        Add(name, k, index, stick) {
            c := {Name: name, Kind: k, Index: index, Stick: stick}
            this._controls[name] := c
            this._byIndex[k index] := c
        }
        hats := Map(), last := -1
        for d in PadForgeController._Declared(PadForgeController._Get(spec, "Hats"), 0, 0)
            hats[d.Index] := d.Label, last := Max(last, d.Index)
        hatNames := []
        loop last + 1 {
            name := Pick(hats.Get(A_Index - 1, ""), "Hat" (A_Index - 1))
            for end in ["Up", "Right", "Down", "Left"]
                taken[name end] := true
            hatNames.Push(name)
        }
        for d in PadForgeController._Declared(PadForgeController._Get(spec, "Buttons"), 0, 0)
            Add(Pick(d.Label, "Button" d.Index), "b", d.Index, false)
        axes := PadForgeController._Declared(PadForgeController._Get(spec, "Axes"), 0, 50)
        for d in PadForgeController._Declared(PadForgeController._Get(spec, "Sliders"), 24, 0)
            axes.Push(d)
        for d in PadForgeController._Sorted(axes) {
            Add(Pick(d.Label, PadForgeDevice._Numbered("a", d.Index)), "a", d.Index, d.Rest = 50)
            this._rests[d.Index] := d.Rest
        }
        for i, name in hatNames
            Add(name, "h", i - 1, false)
    }

    ; A declaration's list as PadForge reads it: each item's index (its own,
    ; or its place), label and rest, in the order of the index.
    static _Declared(list, base, defaultRest) {
        out := []
        for i, item in list {
            if IsObject(item) && !(item is Array) {
                index := item.HasOwnProp("index") ? item.index : i - 1
                label := item.HasOwnProp("name") ? item.name : ""
                rest := item.HasOwnProp("rest") ? PadForgeController._Share(item.rest) : defaultRest
            } else
                index := i - 1, label := item, rest := defaultRest
            out.Push({Index: base + index, Label: label, Rest: rest})
        }
        return PadForgeController._Sorted(out)
    }

    static _Sorted(list) {
        out := []
        for d in list {
            at := out.Length + 1
            while (at > 1 && out[at - 1].Index > d.Index)
                at--
            out.InsertAt(at, d)
        }
        return out
    }

    static _Get(spec, name) => IsObject(spec) && spec.HasOwnProp(name) ? spec.%name% : (IsObject(spec) && spec.HasOwnProp(StrLower(name)) ? spec.%StrLower(name)% : [])

    ; A name as PadForge keeps it (ProgramDeviceDeclaration.Clean): control
    ; and format characters out, and what XML cannot hold, a line or
    ; paragraph separator a space, cut at 64 without splitting a pair, and
    ; trimmed.
    static _Clean(text) {
        text := RegExReplace(String(text), "[\p{Cc}\p{Cf}\x{FFFE}\x{FFFF}]")
        text := RegExReplace(text, "[\p{Zl}\p{Zp}]", " ")
        if (StrLen(text) > 64)
            text := SubStr(text, 1, (Ord(SubStr(text, 64, 1)) & 0xFC00) = 0xD800 ? 63 : 64)
        return RegExReplace(text, "(*UCP)^\s+|\s+$")
    }

    ; A declared name as scripts name it: its letters and digits, each
    ; word's first letter upper case, as PadForge makes it from the name it
    ; keeps. "" when nothing is left, it starts with a digit or it runs past
    ; 32 letters.
    static _Word(label) {
        word := ""
        for part in StrSplit(RegExReplace(PadForgeController._Clean(label), "[^A-Za-z0-9]+", " "), " ")
            if (part != "")
                word .= StrUpper(SubStr(part, 1, 1)) SubStr(part, 2)
        return (word = "" || RegExMatch(word, "^\d") || StrLen(word) > 32) ? "" : word
    }

    ; Whether a word reads as a numbered control (Button3), the pipe's short
    ; form (b3), a touchpad's word (t0, tc0) or a hat's direction (HatUp).
    static _LooksNumbered(word) {
        if RegExMatch(word, "i)^([bah])(\d{1,4})$", &m) && Integer(m[2]) < (StrLower(m[1]) = "b" ? 256 : StrLower(m[1]) = "a" ? 32 : 4)
            return true
        if RegExMatch(word, "i)^(Button|Axis|Slider|Hat)\d+$")
            return true
        if RegExMatch(word, "i)^t(\d|c)")
            return true
        return RegExMatch(word, "i)^Hat.*(Up|Right|Down|Left)$") > 0
    }

    _Declare() {
        spec := this._spec
        json := "{" '"buttons":' PadForgeController._Json(PadForgeController._Get(spec, "Buttons"))
            . ',"axes":' PadForgeController._Json(PadForgeController._Rests(PadForgeController._Get(spec, "Axes")))
            . ',"sliders":' PadForgeController._Json(PadForgeController._Rests(PadForgeController._Get(spec, "Sliders")))
            . ',"hats":' PadForgeController._Json(PadForgeController._Get(spec, "Hats"))
        for part in ["Gyro", "Accel", "Battery"]
            if (PadForgeController._Get(spec, part) = true)
                json .= ',"' StrLower(part) '":true'
        touch := PadForgeController._Get(spec, "Touchpads")
        if (touch is Array && touch.Length > 0)
            json .= ',"touchpads":' PadForgeController._Json(touch)
        json .= ',"feedback":' PadForgeController._Json(PadForgeController._Get(spec, "Feedback")) "}"
        p := this._pf.Request("declare key=" PadForge.Word(this._key) " name=" PadForge.Quote(this._label) " " json)
        this._Row(Map("id", p["id"], "name", this._label, "type", "program"))
        ; Back after a loss: OnConnect hears it, as it heard the loss.
        if !this._connected {
            this._connected := true
            this._Changed("", true)
        }
        ; What the script set before, put back after PadForge starts again.
        if (this._set.Count > 0) {
            line := ""
            for name, wire in this._set
                line .= " " name "=" wire
            this._pf._Send("s " this._id line)
        }
    }

    ; Axes with a rest as a share, for the declaration: "50%".
    static _Rests(list) {
        out := []
        for item in list {
            if IsObject(item) && !(item is Array) && item.HasOwnProp("rest") {
                copy := {}
                for k, v in item.OwnProps()
                    copy.%k% := k = "rest" ? PadForgeController._RestText(v) : v
                out.Push(copy)
            } else
                out.Push(item)
        }
        return out
    }

    ; A rest as a share of the travel: a number, or text such as "50%",
    ; read as the number before its percent sign.
    static _Share(v) {
        if !(v is String)
            return v
        t := Trim(RTrim(Trim(v), "%"))
        return IsNumber(t) ? Number(t) : v
    }

    ; A rest for the declaration: its share and a percent sign, or text
    ; PadForge refuses in its own words.
    static _RestText(v) {
        share := PadForgeController._Share(v)
        return share is Number ? share "%" : share
    }

    static _Json(value) {
        if IsObject(value) && value is Array {
            parts := ""
            for item in value
                parts .= (parts = "" ? "" : ",") PadForgeController._Json(item)
            return "[" parts "]"
        }
        if IsObject(value) {
            parts := ""
            for k, v in (value is Map ? value : value.OwnProps())
                parts .= (parts = "" ? "" : ",") PadForgeController._Json(String(k)) ":" PadForgeController._Json(v)
            return "{" parts "}"
        }
        if (value is Number)
            return String(value)
        text := StrReplace(StrReplace(String(value), "\", "\\"), '"', '\"')
        return '"' text '"'
    }

    _Read(name) {
        switch name, false {
            case "Gyro", "Accel", "Battery", "Charging":
                return this._extras.Get(name, "")
        }
        c := this._Control(name)
        if (c = "") {
            ; An axis's two halves, from the controller's own read of it.
            if (SubStr(name, -4) = "Plus" && StrLen(name) > 4) || (SubStr(name, -5) = "Minus" && StrLen(name) > 5) {
                minus := SubStr(name, -5) = "Minus"
                axis := this._Control(SubStr(name, 1, StrLen(name) - (minus ? 5 : 4)))
                if (axis != "" && axis.Kind = "a") {
                    v := this._Read(axis.Name)
                    return Max(0, axis.Stick ? (minus ? -v : v) : (minus ? (50 - v) * 2 : (v - 50) * 2))
                }
            }
            d := this._Direction(name)
            if (d = "")
                return super._Read(name)
            words := PadForge.HatWords(this._set.Get(this._HatName(d), ""))
            return InStr("+" words "+", "+" d.Bit "+") > 0
        }
        wire := this._set.Get(c.Name, "")
        if (c.Kind = "b")
            return wire = "1"
        if (c.Kind = "h")
            return wire = "" || wire = "-1" ? -1 : (InStr(wire, "deg") ? Number(StrReplace(wire, "deg")) : PadForge.DirectionDegrees(PadForge.HatWords(wire)))
        return wire = "" || wire = "rest" ? this._RestOf(c) : Number(StrReplace(wire, "%"))
    }

    ; What an axis reads at its rest: 0 on a stick, its rest's share of the
    ; travel on a trigger, a slider or a pedal.
    _RestOf(c) => c.Stick ? 0 : this._rests.Get(c.Index, 0)

    ; A control's rest as the s line takes it. An axis rests at the rest it
    ; was declared with, which PadForge reads for the word rest.
    static _RestWire(c) => c.Kind = "b" || c.Kind = "d" ? "0" : c.Kind = "h" ? "-1" : "rest"

    ; Sets a control of the script's own controller. "" puts it at rest.
    _Hold(name, value) {
        switch name, false {
            case "Gyro", "Accel", "Battery", "Charging":
                part := StrLower(name) = "battery" ? "bat" : StrLower(name)
                if (value = "") {
                    ; Nothing more is sent, as for a hold given back.
                    this._extras.Delete(name)
                    this._set.Delete(part)
                    return
                }
                wire := part = "bat" ? Round(value) : part = "charging" ? (value ? 1 : 0) : value[1] "," value[2] "," value[3]
                this._extras[name] := value
                this._Part(part, wire)
                return
        }
        c := this._Control(name)
        if (c = "") {
            d := this._Direction(name)
            if (d = "")
                throw PropertyError("No such control: " name, -2)
            this._Send(this._Turn(d, value))
            return
        }
        wire := value = "" ? PadForgeController._RestWire(c) : PadForge.Wire(c.Kind, value)
        this._set[c.Name] := wire
        this._Send(c.Name "=" wire)
    }

    ; The name of the hat a direction is of.
    _HatName(d) {
        hat := this._byIndex.Get("h" d.Hat, "")
        return hat != "" ? hat.Name : "Hat" d.Hat
    }

    ; A hat's direction pressed or released (true, false or ""): the hat
    ; the script set keeps it, for after a lapse or a restart, and the word
    ; that sends it, which PadForge puts into the hat as the change lands,
    ; beside the hat's other directions.
    _Turn(d, value) {
        hat := this._HatName(d)
        pressed := value != "" && PadForge.Wire("b", value) = "1"
        words := PadForge.Turn(PadForge.HatWords(this._set.Get(hat, "")), d.Bit, pressed)
        this._set[hat] := PadForge.Wire("h", PadForge.DirectionDegrees(words))
        return hat d.Bit "=" (pressed ? "1" : "0")
    }

    _Send(controls) {
        if (this._id != "")
            this._pf._Send("s " this._id " " controls)
    }

    ; Motion and the battery, kept with the controls so they go back after
    ; a lapse or a restart.
    _Part(key, wire) {
        this._set[key] := wire
        this._Send(key "=" wire)
    }

    ; Everything set, sent again as one change.
    _Resend() {
        if (this._set.Count = 0 || this._id = "")
            return
        line := ""
        for name, wire in this._set
            line .= " " name "=" wire
        this._Send(SubStr(line, 2))
    }

    ; Several controls as one change: Hold("LeftX", 50, "LeftY", -20).
    Hold(pairs*) {
        if Mod(pairs.Length, 2)
            throw ValueError("hold takes names and values in pairs.", -1)
        line := ""
        loop pairs.Length // 2 {
            c := this._Control(pairs[A_Index * 2 - 1])
            if (c = "") {
                d := this._Direction(pairs[A_Index * 2 - 1])
                if (d = "")
                    throw ValueError("No such control: " pairs[A_Index * 2 - 1], -1)
                line .= " " this._Turn(d, pairs[A_Index * 2])
                continue
            }
            ; "" puts the control at rest, as setting it to "" does.
            value := pairs[A_Index * 2]
            wire := value = "" ? PadForgeController._RestWire(c) : PadForge.Wire(c.Kind, value)
            this._set[c.Name] := wire
            line .= " " c.Name "=" wire
        }
        if (line != "")
            this._Send(SubStr(line, 2))
    }

    ; Puts controls at rest: Release("A"), or every control.
    Release(names*) {
        if (names.Length = 0) {
            this._set.Clear()
            if (this._id != "")
                this._pf._Send("rest " this._id)
            return
        }
        for name in names
            this._Hold(name, "")
    }

    Set(name, value) => this._Hold(name, value)

    ; A tap before PadForge takes the declaration, or while PadForge is not
    ; connected, is refused, as one on a device not found yet is.
    Tap(name, ms := 50, value := "") {
        if (this._id = "" || !this._pf.Connected)
            throw this._Absent("tap")
        c := this._Target(name)
        if (c = "")
            throw ValueError("No such control: " name, -1)
        if (value = "")
            value := c.Kind = "b" || c.Kind = "d" ? true : c.Kind = "h" ? 0 : 100
        if !this._pf._Send("s " this._id " " c.Name "=" PadForge.Wire(c.Kind, value))
            throw this._Absent("tap")
        this._Send(c.Name "=" PadForgeController._RestWire(c) " in=" Round(ms))
    }

    Sequence(steps*) {
        if Mod(steps.Length, 2)
            throw ValueError("sequence takes names and times in pairs.", -1)
        if (this._id = "" || !this._pf.Connected)
            throw this._Absent("sequence")
        lines := [], at := 0
        loop steps.Length // 2 {
            c := this._Target(steps[A_Index * 2 - 1])
            if (c = "")
                throw ValueError("No such control: " steps[A_Index * 2 - 1], -1)
            on := c.Kind = "b" || c.Kind = "d" ? "1" : c.Kind = "h" ? "0deg" : "100%"
            ; Each step is timed from now, as a device's Sequence is.
            lines.Push(c.Name "=" on " in=" at)
            at += Round(steps[A_Index * 2])
            lines.Push(c.Name "=" PadForgeController._RestWire(c) " in=" at)
        }
        for line in lines
            if (!this._pf._Send("s " this._id " " line) && A_Index = 1)
                throw this._Absent("sequence")
    }

    ; A finger on touchpad pad: x and y from 0 to 1.
    Touch(pad, finger, x, y, down := true, pressure := 1) => this._Send("t" pad "." finger "=" x "," y "," (down ? 1 : 0) "," pressure)

    ; A touchpad's click.
    Click(pad, down := true) => this._Send("tc" pad "=" (down ? 1 : 0))

    Hotkey(keyName, callback := "") {
        throw Error("A controller of the script's own has no hotkeys. Follow a device PadForge reads with PadForge.Device.", -1)
    }

    ; callback(low, high) when a game rumbles the controller, 0 to 65535.
    OnRumble(callback) => this._On("rumble", callback)
    ; callback(left, right) for the trigger motors.
    OnTriggerRumble(callback) => this._On("trigrumble", callback)
    ; callback(left, right): each trigger's 11-byte effect as 22 hex digits.
    OnTriggerEffects(callback) => this._On("trigfx", callback)
    ; callback("rrggbb").
    OnLight(callback) => this._On("led", callback)
    ; callback(number), from 1, and 0 once no virtual controller holds it.
    OnPlayer(callback) => this._On("player", callback)

    _On(kind, callback) {
        if !this._fbs.Has(kind)
            this._fbs[kind] := []
        this._fbs[kind].Push(callback)
    }

    _Feedback(w) {
        if !this._fbs.Has(w[2])
            return
        ; Motors and the player number are numbers. A light's rrggbb and a
        ; trigger's effect stay their digits.
        numbers := w[2] = "rumble" || w[2] = "trigrumble" || w[2] = "player"
        args := []
        loop w.Length - 3
            args.Push(numbers && IsNumber(w[A_Index + 3]) ? Integer(w[A_Index + 3]) : w[A_Index + 3])
        for callback in this._fbs[w[2]]
            callback(args*)
    }

    ; Takes the controller off PadForge's device list. It is not connected
    ; from then on, OnConnect hears it go, and a set sends nothing.
    Remove() {
        if this._pf.Mine.Has(this._key)
            this._pf.Mine.Delete(this._key)
        gone := this._id
        if (gone = "")
            return
        if (this._pf.ById.Has(gone) && this._pf.ById[gone] = this)
            this._pf.ById.Delete(gone)
        this._id := ""
        this._Lost()
        if this._pf.Connected
            this._pf.Request("gone " gone)
    }
}

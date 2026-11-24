; PadForge Automation for AutoHotkey v2. Speaks protocol 1 of PadForge's
; Automation pipe, PadForge.Automation.
;
; Turn on Automation in PadForge (Extras, Automation) at the level your script
; needs: Read to follow devices, Send Inputs to add controllers, hold controls
; and set values.
;
; Example:
;
;   #Requires AutoHotkey v2.0
;   #Include PadForge.ahk
;   pf := PadForge("Turbo A")
;   pad := pf.Find("gamepad")
;   pf.OnButton(pad, 0, (down) => ToolTip("A " (down ? "down" : "up")))
;
; Values are in plain units: a button is true or false, a stick runs from -100
; to 100 with 0 at rest, a trigger or slider from 0 to 100, and a hat is in
; degrees clockwise from up, or -1 at rest. A hat's directions are Up, Right,
; Down and Left, two for a diagonal, such as "Up+Right". Axes 0 to 23 are the
; axes and 24 to 31 the sliders.
;
; PadForge posts the message PadForge.Wake to this script's window after it
; writes, and the library then reads everything that waits in the pipe. The
; message only wakes the script, so one AutoHotkey drops loses nothing: the
; next one reads it all. OnButton works whichever window is active.
;
; A plugin is a program PadForge starts itself (Extras, Plugins). It connects
; with PadForge.Plugin(name), which reads the one-time key and the pipe's
; name PadForge put in its environment. A plugin declares, drives and removes
; its own controllers whatever Automation's level, and writes to its log on
; the Plugins page with Log.

#Requires AutoHotkey v2.0

class PadForge {
    static Protocol := 1
    static DefaultPipe := "PadForge.Automation"

    ; Opens a plugin's connection: PadForge started this script, and its
    ; environment holds the one-time key and the pipe's name. The key is
    ; taken once, so a plugin connects once for each start.
    static Plugin(name, lease := 2000) {
        token := EnvGet("PADFORGE_PLUGIN_TOKEN")
        if (token = "")
            throw Error("PadForge did not start this program as a plugin. Add it on the Plugins page.")
        pipe := EnvGet("PADFORGE_PIPE")
        return PadForge(name, pipe != "" ? pipe : PadForge.DefaultPipe, lease, token)
    }

    ; Opens a connection to the PadForge that runs on this PC.
    __New(name, pipe := PadForge.DefaultPipe, lease := 2000, token := "") {
        this.Pipe := pipe
        this.Lease := lease
        this.Answers := Map()
        this.Callbacks := Map()
        this.Subscribed := Map()
        this.Mirror := Map()
        this.Events := []
        this.Pending := ""
        this.NextTag := 0
        this.Level := ""
        this.Errors := []
        this.WakeMessage := DllCall("RegisterWindowMessage", "Str", "PadForge.Wake", "UInt")
        ; Bound once, so a timer or a message can be turned off by the same
        ; function. AutoHotkey's names ignore case, so these must not share
        ; a name with a method.
        this.WakeFn := ObjBindMethod(this, "_OnWake")
        this.DispatchFn := ObjBindMethod(this, "_Dispatch")
        this.BeatFn := ObjBindMethod(this, "_Beat")
        ; Closed until the pipe is open, so a failed open leaves nothing to
        ; close.
        this.Closed := true
        this.Handle := 0
        this.Handle := this._Open(pipe)
        this.Closed := false
        OnMessage(this.WakeMessage, this.WakeFn)
        kind := token != "" ? "kind=plugin token=" PadForge.Quote(token) : "kind=script"
        hello := this.Request("hello name=" PadForge.Quote(name) " " kind " protocol=" PadForge.Protocol
            " units=plain lease=" lease " hwnd=" A_ScriptHwnd)
        this.Version := hello.Get("padforge", "")
        this.Level := hello.Get("level", "")
        this.Session := Integer(hello.Get("session", "0"))
        SetTimer(this.BeatFn, Max(50, lease // 3))
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
            if (err = 231 && A_TickCount - start < 5000) {   ; ERROR_PIPE_BUSY
                DllCall("WaitNamedPipe", "Str", path, "UInt", 1000)
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
    ; without them does not have.
    static Runs() {
        after := 0
        loop 64 {
            after := DllCall("FindWindowEx", "Ptr", -3, "Ptr", after, "Ptr", 0, "Str", "PadForge.Instance", "Ptr")
            if !after
                return false
            pid := 0
            DllCall("GetWindowThreadProcessId", "Ptr", after, "UInt*", &pid)
            if (PadForge.Integrity(pid) >= 0x3000)
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

    ; The words of a line: .Words holds the bare ones, .Pairs the key=value
    ; ones, .Json the JSON the line ends with.
    static Split(line) {
        words := [], pairs := Map(), json := ""
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

    ; ── The pipe ──

    _Write(line) {
        if this.Closed
            throw Error("The connection to PadForge is closed.")
        size := StrPut(line "`n", "UTF-8") - 1
        buf := Buffer(size + 1)
        StrPut(line "`n", buf, "UTF-8")
        if !DllCall("WriteFile", "Ptr", this.Handle, "Ptr", buf, "UInt", size, "UInt*", &written := 0, "Ptr", 0) {
            this._Closed()
            throw Error("The connection to PadForge is closed.")
        }
    }

    ; Reads everything that waits in the pipe, without blocking, and sorts
    ; each line: answers to their request, events to the mirror and the
    ; queue of callbacks.
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
                buf := Buffer(avail)
                if !DllCall("ReadFile", "Ptr", this.Handle, "Ptr", buf, "UInt", avail, "UInt*", &got := 0, "Ptr", 0) {
                    this._Closed()
                    break
                }
                this.Pending .= StrGet(buf, got, "UTF-8")
                while (at := InStr(this.Pending, "`n")) {
                    line := RTrim(SubStr(this.Pending, 1, at - 1), "`r")
                    this.Pending := SubStr(this.Pending, at + 1)
                    this._Line(line)
                }
            }
        } finally {
            Critical(prev)
        }
        if (this.Events.Length > 0)
            SetTimer(this.DispatchFn, -1)
    }

    _Line(line) {
        if (SubStr(line, 1, 2) = "e ") {
            this._Mirror(line)
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
                this.Errors.Push(line)
            return
        }
        answer := this.Answers[tag]
        if (line = "ok" || SubStr(line, 1, 3) = "ok " || SubStr(line, 1, 4) = "err ")
            answer.Final := line
        else
            answer.Rows.Push(line)
    }

    ; The local copy of the devices the script follows, so a read of a button
    ; or an axis costs no round trip.
    _Mirror(line) {
        w := StrSplit(line, " ")
        if (w.Length < 5)
            return
        kind := w[2], dev := w[3]
        if !(kind = "b" || kind = "h" || kind = "a")
            return
        if !this.Mirror.Has(dev)
            this.Mirror[dev] := {b: Map(), h: Map(), a: Map()}
        this.Mirror[dev].%kind%[Integer(w[4])] := kind = "b" ? w[5] = "1" : Number(w[5])
    }

    ; The directions a hat in degrees holds: the nearest of the eight
    ; points, a diagonal holding two, "" at rest.
    static Directions(degrees) {
        if (degrees < 0)
            return ""
        point := Mod(Round(degrees / 45), 8)
        static names := ["Up", "Up+Right", "Right", "Right+Down", "Down", "Down+Left", "Left", "Up+Left"]
        return names[point + 1]
    }

    ; A value as the pipe takes it in plain units: a button true or false, an
    ; axis a percent, a hat degrees or directions.
    static Plain(control, value) {
        kind := SubStr(control, 1, 1)
        if (kind = "b")
            return value ? "1" : "0"
        if (kind = "a")
            return value "%"
        if (kind = "h")
            return value is Number ? (value < 0 ? "-1" : value "deg") : (value = "" ? "-1" : value)
        return value
    }

    _OnWake(wParam, lParam, msg, hwnd) {
        Critical
        this._Drain()
        return 0
    }

    _Dispatch() {
        while (this.Events.Length > 0) {
            line := this.Events.RemoveAt(1)
            try this._Event(line)
            catch as e
                this.Errors.Push("callback failed: " e.Message)
        }
    }

    _Event(line) {
        e := PadForge.Split(line)
        w := e.Words
        if (w.Length < 2)
            return
        switch w[2] {
            case "b": this._Call("b|" w[3] "|" w[4], w[5] = "1")
            case "h": this._Call("h|" w[3] "|" w[4], Number(w[5]))
            case "a": this._Call("a|" w[3] "|" w[4], Number(w[5]))
            case "x": this._Call("x|" w[3] "|" w[4], w[5] = "1")
            case "dev": this._Call("dev", w[3], w[4] = "online")
            case "profile": this._Call("profile", w.Length > 2 ? w[3] : "default")
            case "engine": this._Call("engine", w[3])
            case "level":
                this.Level := w[3]
                this._Call("level", w[3])
            case "rumble", "trigrumble": this._Call(w[2] "|" w[3], Integer(w[4]), Integer(w[5]))
            case "led", "player": this._Call(w[2] "|" w[3], w[4])
            case "lease": this._Call("lease", w[3])
        }
    }

    _Call(key, args*) {
        if !this.Callbacks.Has(key)
            return
        for callback in this.Callbacks[key]
            callback(args*)
    }

    _Add(key, callback) {
        if !this.Callbacks.Has(key)
            this.Callbacks[key] := []
        this.Callbacks[key].Push(callback)
    }

    _Beat() {
        try this._Write("beat")
        catch
            SetTimer(this.BeatFn, 0)
    }

    _Closed() {
        if this.Closed
            return
        this.Closed := true
        SetTimer(this.BeatFn, 0)
        OnMessage(this.WakeMessage, this.WakeFn, 0)
    }

    ; Sends a line and waits for its answer. Returns the ok line's key=value
    ; pairs as a Map, with .Rows holding the rows before it as objects from
    ; Split. Throws PadForge's refusal in words.
    Request(line, timeoutMs := 10000) {
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
                    throw Error("The connection to PadForge closed.")
                if (A_TickCount - start > timeoutMs)
                    throw Error("PadForge did not answer.")
                Sleep(1)
            }
        } finally {
            this.Answers.Delete(tag)
        }
        final := answer.Final
        if (SubStr(final, 1, 4) = "err ") {
            parts := StrSplit(final, " ", , 3)
            code := parts.Length > 1 ? parts[2] : "internal"
            verb := StrSplit(line, " ")[1]
            if (code = "not-allowed")
                throw Error("PadForge refused " verb ": Automation's level is " this.Level ", too low for it. Raise it in Extras, Automation.")
            throw Error("PadForge refused " verb ": " (parts.Length > 2 ? parts[3] : "") " (" code ").")
        }
        result := PadForge.Split(SubStr(final, 3))
        pairs := result.Pairs
        pairs.Rows := []
        for row in answer.Rows
            pairs.Rows.Push(PadForge.Split(row))
        pairs.Json := result.Json
        pairs.Words := result.Words
        return pairs
    }

    static Id(device) => IsObject(device) ? device.Id : String(device)

    ; ── Events ──

    ; callback(down) on every press and release of a button of any device,
    ; keyboards and mice included.
    OnButton(device, button, callback) {
        id := PadForge.Id(device)
        this._Add("b|" id "|" button, callback)
        if !this.Subscribed.Has("b|" id) {
            this.Request("sub " id " buttons")
            this.Subscribed["b|" id] := true
        }
    }

    ; callback(degrees) when a hat moves: degrees clockwise from up, or -1.
    ; PadForge.Directions(degrees) gives its directions.
    OnHat(device, hat, callback) {
        id := PadForge.Id(device)
        this._Add("h|" id "|" hat, callback)
        if !this.Subscribed.Has("h|" id) {
            this.Request("sub " id " hats")
            this.Subscribed["h|" id] := true
        }
    }

    ; With above, callback(true) when the axis reaches it and callback(false)
    ; when it falls back past above - hyst, worked out by PadForge. above and
    ; hyst are plain: OnAxis(pad, 0, fn, 80) on a stick. Without above,
    ; callback(value) up to hz times a second.
    OnAxis(device, axis, callback, above := "", hyst := "", hz := 100) {
        id := PadForge.Id(device)
        if (above != "") {
            this._Add("x|" id "|" axis, callback)
            this.Request("sub " id " axis=" axis " above=" above "%" (hyst != "" ? " hyst=" hyst "%" : ""))
        } else {
            this._Add("a|" id "|" axis, callback)
            this.Request("sub " id " axes " hz)
        }
    }

    ; callback(id, online) when a device connects or disconnects.
    OnDevice(callback) {
        this._Add("dev", callback)
        if !this.Subscribed.Has("devices") {
            this.Request("sub devices")
            this.Subscribed["devices"] := true
        }
    }

    ; callback(name) when the profile changes, "default" for the default.
    OnProfile(callback) {
        this._Add("profile", callback)
        if !this.Subscribed.Has("profile") {
            this.Request("sub profile")
            this.Subscribed["profile"] := true
        }
    }

    ; callback(state): running, idle, stopping or stopped.
    OnEngine(callback) {
        this._Add("engine", callback)
        if !this.Subscribed.Has("engine") {
            this.Request("sub engine")
            this.Subscribed["engine"] := true
        }
    }

    ; callback(level) when the user changes Automation's level.
    OnLevel(callback) => this._Add("level", callback)

    ; A button, an axis or a hat of a device the script follows, from the
    ; local copy: no round trip.
    Button(device, button) => this._Seen(device, "b", button, false)
    Hat(device, hat) => this._Seen(device, "h", hat, -1)
    Axis(device, axis) => this._Seen(device, "a", axis, 0)
    ; A hat's directions, "Up+Right", or "" at rest.
    HatDirections(device, hat) => PadForge.Directions(this.Hat(device, hat))
    ; Whether a hat holds one direction: HatHolds(pad, 0, "Up").
    HatHolds(device, hat, direction) => InStr("+" this.HatDirections(device, hat) "+", "+" direction "+") > 0

    _Seen(device, kind, index, otherwise) {
        id := PadForge.Id(device)
        return this.Mirror.Has(id) && this.Mirror[id].%kind%.Has(index) ? this.Mirror[id].%kind%[index] : otherwise
    }

    ; ── Read ──

    ; Every device PadForge knows, connected or not.
    Devices() {
        list := []
        for row in this.Request("devices").Rows {
            if (row.Words.Length = 0 || row.Words[1] != "device")
                continue
            p := row.Pairs
            list.Push({Id: p["id"], Type: p["type"], Online: p["online"] = "1", Name: p["name"],
                Buttons: Integer(p["buttons"]), Axes: Integer(p["axes"]), Sliders: Integer(p["sliders"]), Hats: Integer(p["hats"])})
        }
        return list
    }

    ; The first connected device whose type is nameOrType (gamepad, keyboard,
    ; mouse, wheel, program, ...) or whose name holds it.
    Find(nameOrType) {
        for d in this.Devices()
            if (d.Online && (d.Type = nameOrType || d.Id = nameOrType || InStr(d.Name, nameOrType)))
                return d
        return ""
    }

    ; A device's whole state in plain units: .Buttons (true or false, from
    ; button 0), .Axes (0 to 31), .Hats in degrees, .HatDirections and
    ; .Battery.
    Read(device) {
        p := this.Request("read " PadForge.Id(device))
        hex := p["b"], buttons := []
        loop StrLen(hex) // 2 {
            byte := Integer("0x" SubStr(hex, A_Index * 2 - 1, 2))
            loop 8
                buttons.Push(((byte >> (A_Index - 1)) & 1) = 1)
        }
        axes := [], hats := [], directions := []
        for v in StrSplit(p["a"], ",")
            axes.Push(Number(v))
        for v in StrSplit(p["h"], ",") {
            hats.Push(Number(v))
            directions.Push(PadForge.Directions(Number(v)))
        }
        return {Buttons: buttons, Axes: axes, Hats: hats, HatDirections: directions, Battery: Integer(StrSplit(p["bat"], ",")[1])}
    }

    ; What virtual controller number sends to the game now, as a Map.
    Outputs(number) => this.Request("outputs " number)

    ; ── Send Inputs ──

    ; Adds a controller of the script's own. key keeps its identity, and its
    ; mappings, across runs. buttons, axes, sliders and hats are arrays of
    ; names, or of objects with index, name and rest. A rest is where the
    ; axis rests as a share of its travel, 0 to 100: 50 makes a stick. An
    ; axis rests at 50 and a slider at 0 when rest is left out. feedback
    ; holds any of "rumble", "trigrumble", "light" and "player".
    AddController(key, name, buttons := [], axes := [], sliders := [], hats := [], feedback := []) {
        json := "{" '"buttons":' PadForge._Json(buttons) ',"axes":' PadForge._Json(PadForge._Rests(axes))
            . ',"sliders":' PadForge._Json(PadForge._Rests(sliders)) ',"hats":' PadForge._Json(hats)
            . ',"feedback":' PadForge._Json(feedback) "}"
        p := this.Request("declare key=" key " name=" PadForge.Quote(name) " " json)
        return PadForgeController(this, p["id"])
    }

    ; Axes with a rest as a share, for the declaration: "50%".
    static _Rests(list) {
        out := []
        for item in list {
            if IsObject(item) && !(item is Array) && item.HasOwnProp("rest") {
                copy := {}
                for k, v in item.OwnProps()
                    copy.%k% := k = "rest" ? v "%" : v
                out.Push(copy)
            } else
                out.Push(item)
        }
        return out
    }

    static _Json(value) {
        if IsObject(value) && value is Array {
            parts := ""
            for item in value
                parts .= (parts = "" ? "" : ",") PadForge._Json(item)
            return "[" parts "]"
        }
        if IsObject(value) {
            parts := ""
            for k, v in (value is Map ? value : value.OwnProps())
                parts .= (parts = "" ? "" : ",") PadForge._Json(String(k)) ":" PadForge._Json(v)
            return "{" parts "}"
        }
        if (value is Number)
            return String(value)
        text := StrReplace(StrReplace(String(value), "\", "\\"), '"', '\"')
        return '"' text '"'
    }

    ; Holds controls of any device at a value until released, in plain units:
    ; Hold(pad, "b3=1 a0=40% h0=Up").
    Hold(device, controls) => this.Request("own " PadForge.Id(device) " " controls)

    ; Lets go of controls held on a device, "b3 a0", or all of them.
    Release(device, controls := "all") => this.Request("rel " PadForge.Id(device) " " controls)

    ; A named value Custom Expression macro triggers read.
    SetValue(name, value) => this.Request("var " name "=" value)

    _Set(id, controls) => this._Write("s " id " " controls)

    ; A line for this plugin's log on the Plugins page. Only a plugin has a
    ; log.
    Log(text) => this._Write("log " PadForge.Quote(text))

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

    ; Runs an assistant tool, at the tool's own level. Returns JSON text.
    Call(tool, json := "{}") => this.Request("call " tool " " json).Json

    Close() {
        if !this.Closed {
            try this._Write("bye")
            this._Closed()
        }
        if this.Handle {
            DllCall("CloseHandle", "Ptr", this.Handle)
            this.Handle := 0
        }
    }

    __Delete() => this.Close()
}

; A controller of the script's own on PadForge's device list. Map and assign
; it like any device.
class PadForgeController {
    __New(padforge, id) {
        this.PadForge := padforge
        this.Id := id
        this.Staged := Map()
    }

    Press(button) => this.PadForge._Set(this.Id, "b" button "=1")
    Release(button) => this.PadForge._Set(this.Id, "b" button "=0")

    ; Stages a value to go out with Send, in plain units: Set("a0", -50) for a
    ; stick half left, Set("a24", 100) for a slider at its top, Set("h0", 90)
    ; or Set("h0", "Up+Right") for a hat, Set("b3", true) for a button.
    Set(control, value) => this.Staged[control] := PadForge.Plain(control, value)

    ; Every control at rest in one word.
    Rest() => this.PadForge.Request("rest " this.Id)

    ; Sends what Set staged as one change: a stick's two axes never land on
    ; different passes.
    Send() {
        if (this.Staged.Count = 0)
            return
        parts := ""
        for k, v in this.Staged
            parts .= (parts = "" ? "" : " ") k "=" v
        this.Staged := Map()
        this.PadForge._Set(this.Id, parts)
    }

    ; callback(low, high) when a game rumbles the controller.
    OnRumble(callback) => this.PadForge._Add("rumble|" this.Id, callback)
    OnTriggerRumble(callback) => this.PadForge._Add("trigrumble|" this.Id, callback)
    ; callback("rrggbb") and callback(number).
    OnLight(callback) => this.PadForge._Add("led|" this.Id, callback)
    OnPlayer(callback) => this.PadForge._Add("player|" this.Id, callback)

    Remove() => this.PadForge.Request("gone " this.Id)
}

"""PadForge Automation for Python. Speaks protocol 1 of PadForge's
Automation pipe, PadForge.Automation. Standard library only, on Windows.

Turn on Automation in PadForge (Extras, Automation) at the level your
script needs: Read to follow devices, Send Inputs to add controllers,
hold controls and set values.

Example:

    import padforge

    pf = padforge.connect("Turbo A")
    pad = pf.find("gamepad")
    pf.on_button(pad, 0, lambda down: print("A", "down" if down else "up"))
    pf.run()

Buttons, axes and hats use PadForge's own numbers and ranges: an axis runs
0 to 65535 with a stick at rest on 32768, axes 0 to 23 are the axes and 24
to 31 the sliders, and a hat is in hundredths of a degree or -1.
"""

import _winapi
import ctypes
import itertools
import json
import queue
import threading
import time

PROTOCOL = 1
PIPE = "PadForge.Automation"

_ERROR_FILE_NOT_FOUND = 2
_ERROR_PIPE_BUSY = 231
_ERROR_IO_PENDING = 997
_ERROR_OPERATION_ABORTED = 995
_ERROR_BROKEN_PIPE = 109
_HWND_MESSAGE = -3


class PadForgeError(Exception):
    """A request PadForge refused, or a connection it could not make.
    `code` is the protocol's fixed word, such as not-allowed."""

    def __init__(self, code, message):
        super().__init__(message)
        self.code = code


# ── Words on the line ──

def quote(text):
    """Text in double quotes, with \\" and \\\\ inside."""
    text = str(text).replace("\\", "\\\\").replace('"', '\\"')
    return '"' + text.replace("\r", " ").replace("\n", " ") + '"'


def word(text):
    """A word for the line: bare when it can be, quoted otherwise."""
    text = str(text)
    if text == "" or any(c in text for c in ' "\\={\r\n'):
        return quote(text)
    return text


def split(line):
    """Splits a line into its words. Returns (words, pairs, json): the bare
    words in order, the key=value pairs, and the JSON the line ends with."""
    words, pairs, i, n = [], {}, 0, len(line)
    while i < n:
        while i < n and line[i] == " ":
            i += 1
        if i >= n:
            break
        if line[i] == "{":
            return words, pairs, line[i:]
        key, value, quoted = None, [], False
        while i < n and (quoted or line[i] != " "):
            c = line[i]
            if quoted:
                if c == "\\" and i + 1 < n and line[i + 1] in '"\\':
                    value.append(line[i + 1])
                    i += 2
                    continue
                if c == '"':
                    quoted = False
                else:
                    value.append(c)
            elif c == '"':
                quoted = True
            elif c == "=" and key is None:
                key, value = "".join(value), []
            else:
                value.append(c)
            i += 1
        if key is None:
            words.append("".join(value))
        else:
            pairs[key] = "".join(value)
    return words, pairs, None


# ── The pipe ──

def _padforge_runs():
    """Whether a PadForge runs on this PC: its message-only window
    PadForge.Instance stands while it does."""
    try:
        find = ctypes.windll.user32.FindWindowExW
        find.restype = ctypes.c_void_p
        find.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_wchar_p]
        return bool(find(ctypes.c_void_p(_HWND_MESSAGE), None, None, "PadForge.Instance"))
    except Exception:
        return False


class _Pipe:
    """The pipe, opened overlapped, so a read that waits on the reader
    thread never holds up a write from another thread. Windows serializes
    I/O on a handle opened without overlap."""

    def __init__(self, name, timeout):
        path = "\\\\.\\pipe\\" + name
        deadline = time.monotonic() + timeout
        while True:
            try:
                self.handle = _winapi.CreateFile(
                    path, _winapi.GENERIC_READ | _winapi.GENERIC_WRITE, 0, _winapi.NULL,
                    _winapi.OPEN_EXISTING, _winapi.FILE_FLAG_OVERLAPPED, _winapi.NULL)
                break
            except OSError as e:
                if e.winerror == _ERROR_PIPE_BUSY and time.monotonic() < deadline:
                    try:
                        _winapi.WaitNamedPipe(path, 1000)
                    except OSError:
                        time.sleep(0.05)
                    continue
                if e.winerror == _ERROR_FILE_NOT_FOUND:
                    if name != PIPE:
                        raise PadForgeError("closed", "Nothing serves the pipe %s." % name) from None
                    if _padforge_runs():
                        raise PadForgeError(
                            "off", "PadForge is running, but Automation is off. Turn it on in Extras, Automation.") from None
                    raise PadForgeError("closed", "PadForge is not running.") from None
                raise
        self._write_lock = threading.Lock()
        self._reading = None
        self._closed = False

    def write(self, data):
        with self._write_lock:
            if self._closed:
                raise PadForgeError("closed", "The connection to PadForge is closed.")
            ov, err = _winapi.WriteFile(self.handle, data, overlapped=True)
            try:
                ov.GetOverlappedResult(True)
            except OSError:
                raise PadForgeError("closed", "The connection to PadForge is closed.") from None

    def read(self):
        """Bytes, or b"" once the pipe is closed."""
        if self._closed:
            return b""
        try:
            ov, err = _winapi.ReadFile(self.handle, 65536, overlapped=True)
        except OSError:
            return b""
        self._reading = ov
        try:
            # The wait multiprocessing.connection makes for its pipes. A
            # close on another thread cancels the read, which then ends
            # here with ERROR_OPERATION_ABORTED.
            if err == _ERROR_IO_PENDING:
                _winapi.WaitForMultipleObjects([ov.event], False, _winapi.INFINITE)
            n, err = ov.GetOverlappedResult(True)
        except OSError:
            try:
                ov.cancel()
            except OSError:
                pass
            return b""
        finally:
            self._reading = None
        return bytes(ov.getbuffer())

    def close(self):
        if self._closed:
            return
        self._closed = True
        ov = self._reading
        if ov is not None:
            try:
                ov.cancel()
            except OSError:
                pass
        try:
            _winapi.CloseHandle(self.handle)
        except OSError:
            pass


# ── What PadForge describes ──

class Device:
    """A device on PadForge's list. `id` is the short ID PadForge makes up,
    the same for this device across runs."""

    def __init__(self, pairs):
        self.id = pairs.get("id", "")
        self.type = pairs.get("type", "device")
        self.online = pairs.get("online") == "1"
        self.name = pairs.get("name", "")
        self.buttons = int(pairs.get("buttons", "0"))
        self.axes = int(pairs.get("axes", "0"))
        self.sliders = int(pairs.get("sliders", "0"))
        self.hats = int(pairs.get("hats", "0"))
        self.gyro = pairs.get("gyro") == "1"
        self.accel = pairs.get("accel") == "1"
        self.touchpads = int(pairs.get("touchpads", "0"))

    def __repr__(self):
        return "Device(%s, %s, %r)" % (self.id, self.type, self.name)


class State:
    """A device's whole state: buttons, axes 0 to 31 (24 to 31 the
    sliders), hats, motion and battery."""

    def __init__(self, pairs):
        raw = bytes.fromhex(pairs.get("b", "00"))
        self.buttons = [bool(raw[i // 8] & (1 << (i % 8))) for i in range(len(raw) * 8)]
        self.axes = [int(v) for v in pairs.get("a", "").split(",") if v != ""]
        self.hats = [int(v) for v in pairs.get("h", "").split(",") if v != ""]
        self.gyro = tuple(float(v) for v in pairs.get("gyro", "0,0,0").split(","))
        self.accel = tuple(float(v) for v in pairs.get("accel", "0,0,0").split(","))
        battery = pairs.get("bat", "-1,0").split(",")
        self.battery = int(battery[0])
        self.charging = len(battery) > 1 and battery[1] == "1"

    def button(self, index):
        return index < len(self.buttons) and self.buttons[index]


def _id(device):
    return device.id if isinstance(device, Device) else str(device)


class Controller:
    """A controller of the script's own on PadForge's device list. Map and
    assign it like any device. Its feedback comes back through on_rumble,
    on_trigger_rumble, on_light and on_player."""

    def __init__(self, padforge, device_id):
        self._pf = padforge
        self.id = device_id
        self._staged = {}

    def press(self, *buttons):
        self._pf._send_set(self.id, {"b%d" % b: 1 for b in buttons})

    def release(self, *buttons):
        self._pf._send_set(self.id, {"b%d" % b: 0 for b in buttons})

    def set(self, control=None, value=None, **controls):
        """Stages values to go out together with send(): set("a0", 40000),
        or set(a0=40000, a1=32768, b3=1). Names are b<i>, a<i>, h<i>, gyro,
        accel, t<pad>.<finger>, tc<pad>, bat and charging."""
        if control is not None:
            controls[control] = value
        for key, v in controls.items():
            if isinstance(v, (tuple, list)):
                v = ",".join(str(x) for x in v)
            elif isinstance(v, bool):
                v = 1 if v else 0
            self._staged[key] = v

    def send(self, **controls):
        """Sends what set() staged, and these, as one change: a stick's two
        axes never land on different passes."""
        self.set(**controls)
        staged, self._staged = self._staged, {}
        if staged:
            self._pf._send_set(self.id, staged)

    def on_rumble(self, callback):
        """callback(low, high), each 0 to 65535."""
        self._pf._feedback.setdefault(("rumble", self.id), []).append(callback)

    def on_trigger_rumble(self, callback):
        """callback(left, right), each 0 to 65535."""
        self._pf._feedback.setdefault(("trigrumble", self.id), []).append(callback)

    def on_light(self, callback):
        """callback("rrggbb")."""
        self._pf._feedback.setdefault(("led", self.id), []).append(callback)

    def on_player(self, callback):
        """callback(number)."""
        self._pf._feedback.setdefault(("player", self.id), []).append(callback)

    def remove(self):
        self._pf.request("gone " + self.id)


class PadForge:
    """One connection to PadForge. Requests wait for their answer. Events
    run your callbacks on a thread of their own, so a callback may make
    requests."""

    def __init__(self, name, pipe=PIPE, lease=None, timeout=5.0):
        self._pipe = _Pipe(pipe, timeout)
        self._timeout = timeout
        self._tags = itertools.count(1)
        self._pending = {}
        self._pending_lock = threading.Lock()
        self._events = queue.Queue()
        self._buttons = {}
        self._hats = {}
        self._axes = {}
        self._crossings = {}
        self._devices = []
        self._profile = []
        self._engine = []
        self._level_callbacks = []
        self._feedback = {}
        self.errors = []
        self._closed = threading.Event()
        self._reader = threading.Thread(target=self._read_loop, name="padforge-reader", daemon=True)
        self._reader.start()
        self._dispatcher = threading.Thread(target=self._dispatch_loop, name="padforge-events", daemon=True)
        self._dispatcher.start()
        hello = "hello name=%s kind=script protocol=%d" % (quote(name), PROTOCOL)
        if lease:
            hello += " lease=%d" % lease
        _, pairs, _ = self._ask(hello)
        self.version = pairs.get("padforge", "")
        self.level = pairs.get("level", "")
        self.session = int(pairs.get("session", "0"))
        self._lease = (lease or 2000) / 1000.0
        self._beater = threading.Thread(target=self._beat_loop, name="padforge-lease", daemon=True)
        self._beater.start()

    # ── Lines ──

    def _read_loop(self):
        pending = b""
        while True:
            data = self._pipe.read()
            if not data:
                break
            pending += data
            while b"\n" in pending:
                raw, pending = pending.split(b"\n", 1)
                self._line(raw.decode("utf-8", "replace").rstrip("\r"))
        self._closed.set()
        with self._pending_lock:
            waiting = list(self._pending.values())
            self._pending.clear()
        for entry in waiting:
            entry["done"].set()
        self._events.put(None)

    def _line(self, line):
        if line.startswith("e "):
            self._events.put(line)
            return
        tag = None
        if line.startswith("@"):
            tag, _, line = line.partition(" ")
        with self._pending_lock:
            entry = self._pending.get(tag) if tag else None
        if entry is None:
            if line.startswith("err "):
                self.errors.append(line)
            return
        if line == "ok" or line.startswith("ok ") or line.startswith("err "):
            entry["final"] = line
            entry["done"].set()
        else:
            entry["rows"].append(line)

    def _ask(self, line):
        tag = "@%d" % next(self._tags)
        entry = {"rows": [], "final": None, "done": threading.Event()}
        with self._pending_lock:
            self._pending[tag] = entry
        try:
            self._pipe.write((tag + " " + line + "\n").encode("utf-8"))
            if not entry["done"].wait(self._timeout + 30):
                raise PadForgeError("timeout", "PadForge did not answer.")
        finally:
            with self._pending_lock:
                self._pending.pop(tag, None)
        final = entry["final"]
        if final is None:
            raise PadForgeError("closed", "The connection to PadForge closed.")
        if final.startswith("err "):
            self._raise(line, final)
        words, pairs, js = split(final[2:].strip())
        return entry["rows"], pairs, js

    def _raise(self, line, final):
        parts = final.split(" ", 2)
        code = parts[1] if len(parts) > 1 else "internal"
        words = parts[2] if len(parts) > 2 else ""
        verb = line.split(" ", 1)[0]
        if code == "not-allowed":
            raise PadForgeError(code, "PadForge refused %s: Automation's level is %s, too low for it. Raise it in Extras, Automation."
                                % (verb, self.level or "unknown"))
        raise PadForgeError(code, "PadForge refused %s: %s (%s)." % (verb, words, code))

    def request(self, line):
        """Sends a line and gives the words of its ok as a dict. Raises
        PadForgeError for an err."""
        return self._ask(line)[1]

    def rows(self, line):
        """Sends a line that answers rows, and gives them as (words,
        pairs) pairs."""
        rows, _, _ = self._ask(line)
        result = []
        for row in rows:
            words, pairs, _ = split(row)
            result.append((words, pairs))
        return result

    def _send_set(self, device_id, controls):
        parts = ["s", device_id] + ["%s=%s" % (k, v) for k, v in controls.items()]
        self._pipe.write((" ".join(parts) + "\n").encode("utf-8"))

    # ── Events ──

    def _dispatch_loop(self):
        while True:
            line = self._events.get()
            if line is None:
                return
            try:
                self._event(line)
            except Exception as e:
                self.errors.append("callback failed: %r" % e)

    def _event(self, line):
        words, pairs, _ = split(line)
        kind = words[1] if len(words) > 1 else ""
        if kind == "b" and len(words) >= 5:
            for cb in self._buttons.get((words[2], int(words[3])), []):
                cb(words[4] == "1")
        elif kind == "h" and len(words) >= 5:
            for cb in self._hats.get((words[2], int(words[3])), []):
                cb(int(words[4]))
        elif kind == "a" and len(words) >= 5:
            for cb in self._axes.get((words[2], int(words[3])), []):
                cb(int(words[4]))
        elif kind == "x" and len(words) >= 5:
            for cb in self._crossings.get((words[2], int(words[3])), []):
                cb(words[4] == "1")
        elif kind == "dev" and len(words) >= 4:
            for cb in self._devices:
                cb(words[2], words[3] == "online")
        elif kind == "profile":
            name = words[2] if len(words) > 2 else "default"
            for cb in self._profile:
                cb(name)
        elif kind == "engine" and len(words) > 2:
            for cb in self._engine:
                cb(words[2])
        elif kind == "level" and len(words) > 2:
            self.level = words[2]
            for cb in self._level_callbacks:
                cb(words[2])
        elif kind in ("rumble", "trigrumble") and len(words) >= 5:
            for cb in self._feedback.get((kind, words[2]), []):
                cb(int(words[3]), int(words[4]))
        elif kind == "led" and len(words) >= 4:
            for cb in self._feedback.get(("led", words[2]), []):
                cb(words[3])
        elif kind == "player" and len(words) >= 4:
            for cb in self._feedback.get(("player", words[2]), []):
                cb(int(words[3]))

    def on_button(self, device, button, callback):
        """callback(down) on every press and release of a button of any
        device, keyboards and mice included."""
        did = _id(device)
        first = not any(k[0] == did for k in self._buttons)
        self._buttons.setdefault((did, button), []).append(callback)
        if first:
            self.request("sub %s buttons" % did)

    def on_hat(self, device, hat, callback):
        """callback(value) when a hat moves: hundredths of a degree, or -1."""
        did = _id(device)
        first = not any(k[0] == did for k in self._hats)
        self._hats.setdefault((did, hat), []).append(callback)
        if first:
            self.request("sub %s hats" % did)

    def on_axis(self, device, axis, callback, above=None, hyst=None, hz=None):
        """With `above`, callback(True) when the axis reaches it and
        callback(False) when it falls back past above - hyst, worked out by
        PadForge. Without it, callback(value) up to `hz` times a second."""
        did = _id(device)
        if above is not None:
            self._crossings.setdefault((did, axis), []).append(callback)
            line = "sub %s axis=%d above=%d" % (did, axis, above)
            if hyst is not None:
                line += " hyst=%d" % hyst
            self.request(line)
        else:
            first = not any(k[0] == did for k in self._axes)
            self._axes.setdefault((did, axis), []).append(callback)
            if first or hz:
                self.request("sub %s axes %d" % (did, hz or 100))

    def on_device(self, callback):
        """callback(device_id, online) when a device connects or
        disconnects."""
        if not self._devices:
            self.request("sub devices")
        self._devices.append(callback)

    def on_profile(self, callback):
        """callback(name) when the profile changes, "default" for the
        default profile."""
        if not self._profile:
            self.request("sub profile")
        self._profile.append(callback)

    def on_engine(self, callback):
        """callback(state): running, idle, stopping or stopped."""
        if not self._engine:
            self.request("sub engine")
        self._engine.append(callback)

    def on_level(self, callback):
        """callback(level) when the user changes Automation's level."""
        self._level_callbacks.append(callback)

    # ── Read ──

    def devices(self):
        """Every device PadForge knows, connected or not."""
        return [Device(pairs) for words, pairs in self.rows("devices") if words and words[0] == "device"]

    def find(self, name_or_type, online=True):
        """The first device whose type is name_or_type (gamepad, keyboard,
        mouse, wheel, program, ...) or whose name holds it."""
        wanted = str(name_or_type).lower()
        for d in self.devices():
            if online and not d.online:
                continue
            if d.type == wanted or d.id == name_or_type or wanted in d.name.lower():
                return d
        return None

    def caps(self, device):
        """Each control of a device with its index and name, as dicts."""
        return [dict(pairs, kind=words[0]) for words, pairs in self.rows("caps " + _id(device)) if words]

    def read(self, device):
        """A device's whole state now."""
        return State(self.request("read " + _id(device)))

    def outputs(self, number):
        """What virtual controller `number` sends to the game now."""
        return self.request("outputs %d" % number)

    # ── Send Inputs ──

    def add_controller(self, key, name, buttons=(), axes=(), sliders=(), hats=(), gyro=False, accel=False,
                       touchpads=(), battery=False, feedback=()):
        """Adds a controller of the script's own. `key` keeps its identity,
        and its mappings, across runs. buttons, axes, sliders and hats are
        lists of names or of dicts with index, name and rest. feedback holds
        any of rumble, trigrumble, light and player."""
        shape = {"buttons": list(buttons), "axes": list(axes), "sliders": list(sliders), "hats": list(hats),
                 "gyro": bool(gyro), "accel": bool(accel), "touchpads": list(touchpads),
                 "battery": bool(battery), "feedback": list(feedback)}
        pairs = self.request("declare key=%s name=%s %s" % (word(key), quote(name), json.dumps(shape)))
        return Controller(self, pairs["id"])

    def hold(self, device, **controls):
        """Holds controls of any device at a value until released:
        hold(pad, b3=1, a0=40000)."""
        parts = ["%s=%s" % (k, v) for k, v in controls.items()]
        self.request("own %s %s" % (_id(device), " ".join(parts)))

    def release(self, device=None, *controls):
        """Lets go of controls held on a device, or all of them."""
        if device is None:
            self.request("rel all")
        else:
            self.request("rel %s %s" % (_id(device), " ".join(controls) if controls else "all"))

    def set_value(self, name, value):
        """A named value Custom Expression macro triggers read."""
        self.request("var %s=%s" % (name, repr(float(value))))

    # ── Commands ──

    def switch_profile(self, name):
        return self.request("activate " + quote(name))

    def release_profile(self):
        return self.request("deactivate")

    def engine(self, on):
        return self.request("engine " + ("on" if on else "off"))

    def controllers(self, on):
        return self.request("controllers " + ("on" if on else "off"))

    def controller(self, number, on):
        return self.request("slot %d %s" % (number, "on" if on else "off"))

    def show(self):
        return self.request("show")

    def hide(self):
        return self.request("hide")

    def snapshot(self):
        return self.request("snapshot")

    def state(self):
        """What runs now, as PadForge's state JSON."""
        return json.loads(self._ask("state")[2] or "{}")

    def call(self, tool, arguments=None):
        """Runs an assistant tool, at the tool's own level."""
        return json.loads(self._ask("call %s %s" % (word(tool), json.dumps(arguments or {})))[2] or "{}")

    # ── The connection ──

    def _beat_loop(self):
        while not self._closed.wait(self._lease / 3):
            try:
                self._pipe.write(b"beat\n")
            except PadForgeError:
                return

    @property
    def closed(self):
        return self._closed.is_set()

    def run(self, seconds=None):
        """Waits while events arrive, until the connection closes or
        `seconds` pass."""
        self._closed.wait(seconds)

    def close(self):
        if not self._closed.is_set():
            try:
                self._pipe.write(b"bye\n")
            except PadForgeError:
                pass
            self._closed.wait(1.0)
        self._pipe.close()
        self._closed.set()

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()


def connect(name, pipe=PIPE, lease=None, timeout=5.0):
    """Opens a connection to the PadForge that runs on this PC."""
    return PadForge(name, pipe=pipe, lease=lease, timeout=timeout)

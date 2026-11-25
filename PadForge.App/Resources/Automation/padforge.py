"""PadForge Automation for Python. Speaks protocol 1 of PadForge's
Automation pipe, PadForge.Automation. Standard library only, on Windows.

Install it from PadForge with Install on the Python row of the Scripts
card (Extras, Automation), then a script anywhere starts with import
padforge. Turn on Automation at the level the script needs: Read to
follow devices, Send Inputs to hold controls, take them in hotkeys and
add controllers.

Example:

    import padforge

    pad = padforge.device("gamepad")
    pad.hotkey("A", lambda: pad.tap("B", 50))      # A presses B for the game
    pad.hotkey("LeftX > 80%", lambda: print("right"))
    pad.hotkey("Back", lambda: pad.hold("RightTrigger", 100))   # holds it
    pad.hotkey("Back up", lambda: pad.release("RightTrigger"))  # gives it back
    padforge.wait()

A device is an object found by its script ID, its type (gamepad, keyboard,
mouse, wheel, joystick, program...) or a part of its name. Its attributes
are its controls by the names scripts use, the names its Script ID row on
PadForge's Devices page lists: A, B, LeftX, RightTrigger, DPad for a pad,
MouseX and MouseLeft for a mouse, KeyA for a keyboard, Axis0, Slider0, Hat0
and Button0 for any device. Names are read without regard to case. By
number: pad.button[0], pad.axis[0], pad.slider[0], pad.hat[0]. A name that
is also a method's, such as Set, reads as pad["Set"].

Values are plain units: a button is True or False, a stick runs from -100
to 100 with 0 at rest, a trigger or slider from 0 to 100, a hat is degrees
clockwise from up or -1 at rest, and its directions read as HatUp,
HatRight, HatDown and HatLeft (DPadUp on a pad). LeftXPlus and LeftXMinus
are the two halves of an axis, 0 to 100 each. A read costs nothing: the
library keeps the device's whole state, which PadForge sends in full when
the script starts following it. A device that is not connected reads None
and its connected attribute is False.

Setting an attribute holds the control at that value for the game:
pad.A = True. None gives it back: pad.A = None, or pad.release("A").
False and 0 are values like any other, so pad.A = False holds A up.
pad.set("LeftX", 40) puts a value once, until the device's own input moves
the control. pad.tap("A", 50) presses A for 50 ms and pad.sequence("A", 50,
"B", 50) taps one after the other, timed on PadForge's own clock, and a tap
sent between two of PadForge's passes is never lost.

Before the device is found, and while PadForge is not running, setting an
attribute, hold and set wait under the control's name and apply when the
device is found, a set once. A tap or a sequence raises PadForgeError then,
since a tap sent later is not the one the script asked for: "PadForge is
not connected" while PadForge does not run, and "the device is not
connected" while it runs and has no device that matches. PadForge refuses a
tap or a sequence on a device it has that is not connected, in the same
words, and the library raises its refusal. A tap or a sequence on the
script's own controller raises the same way until PadForge has added it.

A hat's direction is set as a button is: pad.DPadUp = True presses Up and
False holds it released, beside the hat's other directions. The hat
itself, held or the device's own, is what the held directions go over, so
pad.DPad = "Down" with DPadRight held reads Down and Right. Up and Down
together cancel, as Left and Right do.

Hotkeys take AutoHotkey's forms: pad.hotkey("A", fn) takes A from the game
while the hotkey exists, "~A" leaves it to the game too, and "A up",
"HatUp", "LeftX > 80%", "LeftX < -80%" and "LeftX > 80% up" work. fn gets
the hotkey's name when it takes an argument. pad.hotkey("A", "Off")
removes it. PadForge decides the claim inside its own pass, so the game
never sees a press the hotkey took.

Callbacks run one at a time, in the order their events came, on a thread
of the library's own. A callback may read, hold and make requests. The
script's own thread ends the script when it ends, so a script that only
waits for its callbacks ends with padforge.wait().

The script and PadForge start in either order. While PadForge is not
running, padforge.why says so and the library keeps trying, and when
PadForge starts again it puts back what the script held, claimed and
followed. A thread of the library's own beats every 31 ms, so a script
busy in its own code keeps its holds. A script that stops beating for
250 ms lets go of what it held and claimed until it beats again, and its
controllers go offline after 5 seconds of silence.

A plugin is a program PadForge starts itself (Extras, Plugins). It connects
with padforge.plugin(name), which reads the one-time key and the pipe's
name PadForge put in its environment. A plugin declares, drives and removes
its own controllers whatever Automation's level, and writes to its log on
the Plugins page with log().

Some online games' anti-cheat flags a player while a script drives the
game. Check a game's rules before a script drives it.
"""

import _winapi
import ctypes
import inspect
import itertools
import json as _json
import os
import queue
import re
import sys
import threading
import time
import traceback
from collections import namedtuple

PROTOCOL = 1
PIPE = "PadForge.Automation"
TOKEN_VARIABLE = "PADFORGE_PLUGIN_TOKEN"
PIPE_VARIABLE = "PADFORGE_PIPE"
BEAT_SECONDS = 0.031
RETRY_SECONDS = 1.0

_ERROR_FILE_NOT_FOUND = 2
_ERROR_PIPE_BUSY = 231
_ERROR_IO_PENDING = 997
_ERROR_OPERATION_ABORTED = 995
_ERROR_BROKEN_PIPE = 109
_HWND_MESSAGE = -3
_SECURITY_SQOS_PRESENT = 0x00100000
_SECURITY_IDENTIFICATION = 0x00010000
_SE_KERNEL_OBJECT = 6
_OWNER_SECURITY_INFORMATION = 1

# The owners a PadForge pipe has: Administrators and the system. A program
# without administrator rights cannot give a pipe it makes either one.
_PADFORGE_OWNERS = ("S-1-5-32-544", "S-1-5-18")

# The names of the pipes PadForge serves, the control pipe and the Automation
# pipe, in lower case: Windows compares pipe names without regard to case.
_OWN_NAMES = ("padforge.control", "padforge.automation")


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


def directions(degrees):
    """The directions a hat in degrees holds: the nearest of the eight
    points, a diagonal holding two, "" at rest."""
    if degrees is None or degrees < 0:
        return ""
    point = int(degrees / 45.0 + 0.5) % 8
    return ("Up", "Up+Right", "Right", "Right+Down", "Down", "Down+Left", "Left", "Up+Left")[point]


def _hat_words(wire):
    """The directions a hat value as the pipe takes it holds: "45deg",
    "Up+Right", or "-1" at rest."""
    if wire in ("", "-1", "rest"):
        return ""
    if wire.endswith("deg"):
        return directions(_read(wire[:-3]))
    return wire


def _direction_degrees(words):
    """The degrees a hat that holds these directions reads, -1 for none. Up
    and Down together cancel, as Left and Right do."""
    held = {w.strip().lower() for w in words.split("+") if w.strip()}
    up, down, right, left = "up" in held, "down" in held, "right" in held, "left" in held
    if up and down:
        up = down = False
    if right and left:
        right = left = False
    if up:
        return 45 if right else 315 if left else 0
    if down:
        return 135 if right else 225 if left else 180
    return 90 if right else 270 if left else -1


def _turn(words, bit, pressed):
    """words with one direction pressed or released."""
    held = [w for w in words.split("+") if w.strip() and w.strip().lower() != bit.lower()]
    if pressed:
        held.append(bit)
    return "+".join(held)


def _number(value):
    """A number as the line takes it: whole when it is, else up to three
    places."""
    if isinstance(value, bool):
        return "1" if value else "0"
    if isinstance(value, int):
        return str(value)
    text = ("%.3f" % float(value)).rstrip("0").rstrip(".")
    return "0" if text in ("-0", "") else text


def _read(text):
    """A number as PadForge wrote it: whole when it has no point."""
    try:
        return float(text) if "." in text else int(text)
    except ValueError:
        try:
            return float(text)
        except ValueError:
            return text


def _wire(kind, value):
    """A value as the pipe takes it for a control of a kind (b, a or h, or d
    for a hat's direction, which takes a button's), in plain units."""
    if kind in ("b", "d"):
        if isinstance(value, str):
            return "1" if value.strip().lower() in ("1", "true") else "0"
        return "1" if value else "0"
    if kind == "a":
        if isinstance(value, str):
            text = value.strip()
            return text if text == "rest" or text.endswith("%") else text + "%"
        return _number(value) + "%"
    if value is None:
        return "-1"
    if isinstance(value, (int, float)) and not isinstance(value, bool):
        return "-1" if value < 0 else _number(value) + "deg"
    text = str(value).replace(" ", "")
    return text if text else "-1"


def _value(value):
    """A named value as the var line takes it."""
    if isinstance(value, (int, float)):
        return _number(value)
    return str(value)


def _gone(value):
    """Whether a value gives a control back: None or an empty string."""
    return value is None or (isinstance(value, str) and value == "")


def _arity(fn):
    """How many arguments a callback takes by position, or None for any
    number."""
    try:
        params = inspect.signature(fn).parameters.values()
    except (TypeError, ValueError):
        return None
    count = 0
    for p in params:
        if p.kind == p.VAR_POSITIONAL:
            return None
        if p.kind in (p.POSITIONAL_ONLY, p.POSITIONAL_OR_KEYWORD):
            count += 1
    return count


# ── The pipe ──

def _integrity(pid):
    """A process's integrity level as its RID (0x3000 is high), or None."""
    try:
        kernel = ctypes.windll.kernel32
        advapi = ctypes.windll.advapi32
        kernel.OpenProcess.restype = ctypes.c_void_p
        kernel.CloseHandle.argtypes = [ctypes.c_void_p]
        advapi.OpenProcessToken.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.POINTER(ctypes.c_void_p)]
        advapi.GetTokenInformation.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p, ctypes.c_uint32,
                                               ctypes.POINTER(ctypes.c_uint32)]
        advapi.GetSidSubAuthorityCount.restype = ctypes.POINTER(ctypes.c_ubyte)
        advapi.GetSidSubAuthorityCount.argtypes = [ctypes.c_void_p]
        advapi.GetSidSubAuthority.restype = ctypes.POINTER(ctypes.c_uint32)
        advapi.GetSidSubAuthority.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
        process = kernel.OpenProcess(0x1000, False, pid)
        if not process:
            return None
        try:
            token = ctypes.c_void_p()
            if not advapi.OpenProcessToken(process, 0x0008, ctypes.byref(token)):
                return None
            try:
                size = ctypes.c_uint32()
                advapi.GetTokenInformation(token, 25, None, 0, ctypes.byref(size))
                if size.value == 0:
                    return None
                buffer = ctypes.create_string_buffer(size.value)
                if not advapi.GetTokenInformation(token, 25, buffer, size, ctypes.byref(size)):
                    return None
                sid = ctypes.c_void_p.from_buffer(buffer).value
                count = advapi.GetSidSubAuthorityCount(sid).contents.value
                return advapi.GetSidSubAuthority(sid, count - 1).contents.value
            finally:
                kernel.CloseHandle(token)
        finally:
            kernel.CloseHandle(process)
    except Exception:
        return None


def _padforge_runs():
    """Whether a PadForge runs on this PC: its message-only window
    PadForge.Instance stands while it does. A window of that name counts
    only when its process runs with administrator rights, which a program
    that made one without them does not."""
    try:
        user = ctypes.windll.user32
        user.FindWindowExW.restype = ctypes.c_void_p
        user.FindWindowExW.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_wchar_p]
        user.GetWindowThreadProcessId.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint32)]
        after = None
        for _ in range(64):
            after = user.FindWindowExW(ctypes.c_void_p(_HWND_MESSAGE), after, None, "PadForge.Instance")
            if not after:
                return False
            pid = ctypes.c_uint32()
            user.GetWindowThreadProcessId(ctypes.c_void_p(after), ctypes.byref(pid))
            level = _integrity(pid.value)
            if level is not None and level >= 0x3000:
                return True
        return False
    except Exception:
        return False


def _sid_text(sid):
    """A SID as text, or None."""
    advapi = ctypes.windll.advapi32
    kernel = ctypes.windll.kernel32
    kernel.LocalFree.argtypes = [ctypes.c_void_p]
    advapi.ConvertSidToStringSidW.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p)]
    text = ctypes.c_void_p()
    if not advapi.ConvertSidToStringSidW(sid, ctypes.byref(text)):
        return None
    try:
        return ctypes.wstring_at(text.value)
    finally:
        kernel.LocalFree(text)


def _pipe_owner(handle):
    """The SID of the pipe's owner as text, or None when it cannot be read."""
    try:
        advapi = ctypes.windll.advapi32
        kernel = ctypes.windll.kernel32
        kernel.LocalFree.argtypes = [ctypes.c_void_p]
        advapi.GetSecurityInfo.restype = ctypes.c_uint32
        advapi.GetSecurityInfo.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_uint32,
                                           ctypes.POINTER(ctypes.c_void_p), ctypes.c_void_p, ctypes.c_void_p,
                                           ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p)]
        owner = ctypes.c_void_p()
        descriptor = ctypes.c_void_p()
        if advapi.GetSecurityInfo(ctypes.c_void_p(handle), _SE_KERNEL_OBJECT, _OWNER_SECURITY_INFORMATION,
                                  ctypes.byref(owner), None, None, None, ctypes.byref(descriptor)) != 0:
            return None
        try:
            return _sid_text(owner)
        finally:
            kernel.LocalFree(descriptor)
    except Exception:
        return None


def _own_user():
    """The SID of the user this script runs as, as text, or None."""
    try:
        advapi = ctypes.windll.advapi32
        kernel = ctypes.windll.kernel32
        kernel.GetCurrentProcess.restype = ctypes.c_void_p
        kernel.CloseHandle.argtypes = [ctypes.c_void_p]
        advapi.OpenProcessToken.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.POINTER(ctypes.c_void_p)]
        advapi.GetTokenInformation.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p, ctypes.c_uint32,
                                               ctypes.POINTER(ctypes.c_uint32)]
        token = ctypes.c_void_p()
        if not advapi.OpenProcessToken(kernel.GetCurrentProcess(), 0x0008, ctypes.byref(token)):
            return None
        try:
            size = ctypes.c_uint32()
            advapi.GetTokenInformation(token, 1, None, 0, ctypes.byref(size))
            if size.value == 0:
                return None
            buffer = ctypes.create_string_buffer(size.value)
            if not advapi.GetTokenInformation(token, 1, buffer, size, ctypes.byref(size)):
                return None
            # TOKEN_USER opens with the pointer to the user's SID.
            return _sid_text(ctypes.c_void_p.from_buffer(buffer).value)
        finally:
            kernel.CloseHandle(token)
    except Exception:
        return None


def _served_by_padforge(handle, name):
    """Whether PadForge serves the pipe. Its owner must be Administrators
    or the system, which a program without administrator rights cannot
    give a pipe it makes. A pipe of another name than PadForge's own
    (_OWN_NAMES), which only a test serves, is also taken from this
    script's own user. Windows reads a pipe's name without regard to case,
    so PadForge's own are told apart the same way."""
    found = _pipe_owner(handle)
    if found in _PADFORGE_OWNERS:
        return True
    return name.lower() not in _OWN_NAMES and found is not None and found == _own_user()


class _Pipe:
    """The pipe, opened overlapped, so a read that waits on the reader
    thread never holds up a write from another thread. Windows serializes
    I/O on a handle opened without overlap.

    It is opened at the identification level, so a server that is not
    PadForge cannot act as this script's user, and nothing is sent until
    its owner is PadForge's: Administrators or the system, which a program
    without administrator rights cannot give a pipe it made under
    PadForge's name. A pipe of another name, which a test serves, is
    taken from this script's own user too."""

    def __init__(self, name, timeout):
        path = "\\\\.\\pipe\\" + name
        deadline = time.monotonic() + timeout
        while True:
            try:
                self.handle = _winapi.CreateFile(
                    path, _winapi.GENERIC_READ | _winapi.GENERIC_WRITE, 0, _winapi.NULL,
                    _winapi.OPEN_EXISTING,
                    _winapi.FILE_FLAG_OVERLAPPED | _SECURITY_SQOS_PRESENT | _SECURITY_IDENTIFICATION, _winapi.NULL)
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
        if not _served_by_padforge(self.handle, name):
            _winapi.CloseHandle(self.handle)
            raise PadForgeError(
                "not-padforge",
                "Another program holds the pipe %s. It is not PadForge, so nothing was sent to it." % name)
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



# ── Answers and controls ──

Row = namedtuple("Row", "words pairs json")
Row.__doc__ = "A row an answer carries before its ok: its bare words, its pairs and its JSON."

Control = namedtuple("Control", "name kind index")
Control.__doc__ = "A control of a device: its name, its kind (button, axis, slider or hat) and its index."

# A control as the library keeps it: its name, its kind on the pipe (b, a
# or h), its index (sliders from 24) and whether it is a stick.
_Control = namedtuple("_Control", "name kind index stick")

_Direction = namedtuple("_Direction", "hat bit name")


class Answer(dict):
    """The pairs of an ok line. `rows` holds the rows that came before it,
    `words` its bare words and `json` the JSON it ends with, or None."""

    def __init__(self, pairs, words=(), json=None, rows=()):
        super().__init__(pairs)
        self.words = list(words)
        self.json = json
        self.rows = list(rows)


class _Entry:
    """A request that waits for its answer."""

    def __init__(self, pipe):
        self.pipe = pipe
        self.rows = []
        self.final = None
        self.done = threading.Event()


class _Hotkey:
    """One hotkey: its name as the script gave it and what it follows."""

    def __init__(self, name, passes, up, kind, what, line, key, hat=0, bit=""):
        self.name = name
        self.passes = passes
        self.up = up
        self.kind = kind
        self.what = what
        self.line = line
        self.key = key
        self.hat = hat
        self.bit = bit
        self.fn = None


class _Key:
    """What the hotkeys on one control or one line share: the subscription,
    whether it claims, and the line's number."""

    def __init__(self, hotkey):
        self.what = hotkey.what
        self.kind = hotkey.kind
        self.line = hotkey.line
        self.number = 0
        self.claimed = -1
        self.items = []


# The verbs of the command set, which PadForge answers while it still asks
# the user about a script.
_COMMAND_SET = ("hello", "state", "activate", "deactivate", "query", "engine", "controllers", "slot", "show", "hide",
                "minimize", "snapshot", "record", "version", "exit")

_NUMBERED = re.compile(r"^(button|axis|slider|hat)(\d+)$", re.IGNORECASE | re.ASCII)
_SHORT = re.compile(r"^([bah])(\d+)$", re.IGNORECASE | re.ASCII)
_HOTKEY = re.compile(r"^(\w+)\s*(?:([<>])\s*(-?\d+(?:\.\d+)?)\s*%?)?$", re.ASCII)
_UP_DOWN = re.compile(r"\s+(up|down)$", re.IGNORECASE)
_ENDS = ("Up", "Right", "Down", "Left")


# ── The connection ──

class Connection:
    """A connection to the PadForge that runs on this PC. It does not wait:
    while PadForge is not running, connected is False, why says why, and
    the library tries again every second."""

    def __init__(self, name, pipe=None, level="send-inputs", token=None, lease=0):
        self.name = name
        self.pipe = pipe or _pipe_name()
        self.level = ""
        self.version = ""
        self.session = 0
        self.why = "Not connected yet."
        self.errors = []
        self._wanted = level
        self._token = token
        self._lease = lease
        # Held while the model changes: the devices, their holds and
        # hotkeys, the controllers. The reader never takes it, so a request
        # made under it is always answered.
        self._lock = threading.RLock()
        self._pipe_lock = threading.Lock()
        self._answers = {}
        self._answers_lock = threading.Lock()
        self._tags = itertools.count(1)
        self._queue = queue.Queue()
        self._callbacks = {}
        self._wanting = {}
        self._by_id = {}
        self._mine = {}
        self._values = {}
        self._follows = []
        self._pipe = None
        self._open = False
        self._waiting = False
        self._generation = 0
        self._next_try = 0.0
        self._ended = threading.Event()
        threading.Thread(target=self._dispatch_loop, name="padforge-events", daemon=True).start()
        with self._lock:
            self._try_open()
        if token is None:
            threading.Thread(target=self._keep_loop, name="padforge-retry", daemon=True).start()
            beat = BEAT_SECONDS
        else:
            beat = max(BEAT_SECONDS, lease / 3000.0) if lease else BEAT_SECONDS
        threading.Thread(target=self._beat_loop, args=(beat,), name="padforge-beat", daemon=True).start()

    @property
    def connected(self):
        """True while PadForge answers."""
        return self._open

    def __repr__(self):
        return "Connection(%r, %s)" % (self.name, "connected" if self._open else "not connected")

    # ── Opening, and opening again ──

    def _try_open(self):
        """Opens the pipe, says hello and puts back what the script holds,
        claims and follows. Runs under the model's lock."""
        self._next_try = time.monotonic() + RETRY_SECONDS
        try:
            pipe = _Pipe(self.pipe, 2.0)
        except Exception as e:
            self.why = str(e)
            return False
        with self._pipe_lock:
            self._pipe = pipe
        threading.Thread(target=self._read_loop, args=(pipe,), name="padforge-reader", daemon=True).start()
        if self._token is not None:
            hello = "hello name=%s kind=plugin token=%s protocol=%d units=plain" % (quote(self.name), quote(self._token), PROTOCOL)
        else:
            hello = "hello name=%s kind=script protocol=%d units=plain" % (quote(self.name), PROTOCOL)
        if self._wanted:
            hello += " level=" + self._wanted
        if self._lease:
            hello += " lease=%d" % self._lease
        try:
            answer = self._ask(hello, 10.0)
        except Exception as e:
            self._drop(pipe)
            self.why = str(e)
            return False
        self.version = answer.get("padforge", "")
        self.level = answer.get("level", "")
        self.session = int(answer.get("session", "0") or 0)
        self._waiting = answer.get("consent") == "waiting"
        self.why = "PadForge asks you whether this script may work with it." if self._waiting else ""
        self._generation += 1
        self._open = True
        if self._token is None:
            self._restore()
        if self._open:
            self.why = ""
        return True

    def _restore(self):
        """Everything the script holds, claims and follows, put back on a
        new connection: its devices found again, its controllers declared
        again under the same keys, its values set again. A step PadForge
        refuses goes to errors, and the next one still runs."""
        for kind in list(self._follows):
            self._step(lambda: self._ask("sub " + kind))
        self._step(lambda: self._ask("sub devices"))
        self._step(self._relist)
        for ctl in list(self._mine.values()):
            self._step(ctl._declare)
        for name, value in list(self._values.items()):
            self._send("var %s=%s" % (name, _value(value)))
        for pad in list(self._wanting.values()):
            pad._find()

    def _relist(self):
        """Every device the script listed takes its connection from
        PadForge's list again, on a new connection."""
        if not self._by_id:
            return
        for row in self._ask("devices").rows:
            if not row.words or row.words[0] != "device":
                continue
            pad = self._by_id.get(row.pairs.get("id", ""))
            if pad is not None and not isinstance(pad, Controller):
                pad._online = row.pairs.get("online") == "1"

    def _step(self, fn):
        try:
            fn()
        except Exception as e:
            self.errors.append(str(e))

    def _keep_loop(self):
        while not self._ended.wait(0.05):
            if self._pipe is None and time.monotonic() >= self._next_try:
                with self._lock:
                    if self._pipe is None and not self._ended.is_set():
                        self._try_open()

    def _beat_loop(self, seconds):
        # No beat before hello's answer: PadForge takes hello first.
        while not self._ended.wait(seconds):
            if self._open:
                self._send("beat")

    def _drop(self, pipe):
        """The pipe closed: every device of the script goes offline and
        every request that waits on it ends."""
        with self._pipe_lock:
            if pipe is not self._pipe:
                pipe.close()
                return
            self._pipe = None
            self._open = False
        pipe.close()
        with self._answers_lock:
            waiting = [e for e in self._answers.values() if e.pipe is pipe]
        for entry in waiting:
            entry.done.set()
        self._next_try = time.monotonic() + RETRY_SECONDS
        if self._ended.is_set():
            self.why = "The script closed the connection."
        elif self._token is not None:
            self.why = "PadForge closed the connection."
            self._ended.set()
        else:
            self.why = "PadForge closed the connection. The library tries again."
        for pad in list(self._by_id.values()):
            pad._lost()

    # ── The pipe ──

    def _read_loop(self, pipe):
        pending = b""
        while True:
            data = pipe.read()
            if not data:
                break
            pending += data
            while b"\n" in pending:
                raw, pending = pending.split(b"\n", 1)
                self._line(raw.decode("utf-8", "replace").rstrip("\r"))
        self._drop(pipe)

    def _line(self, line):
        if line.startswith("e "):
            # The copy takes the event at once, and a change the copy holds
            # already (a second subscription's copy of it) goes no further.
            item = self._mirror(line)
            if item is not None:
                self._queue.put(item)
            return
        tag = None
        if line.startswith("@"):
            tag, _, line = line.partition(" ")
        with self._answers_lock:
            entry = self._answers.get(tag) if tag else None
        if entry is None:
            if line.startswith("err "):
                self.errors.append(line)
            return
        if line == "ok" or line.startswith("ok ") or line.startswith("err "):
            entry.final = line
            entry.done.set()
        else:
            entry.rows.append(line)

    def _mirror(self, line):
        """An event into the copy of its device, and what the dispatcher
        gets of it. None for a change the copy holds already."""
        w = line.split(" ")
        if len(w) < 3:
            return ("event", line, None)
        pad = self._by_id.get(w[2])
        if pad is None:
            return ("event", line, None)
        go, extra = pad._take(w, line)
        return ("event", line, extra) if go else None

    def _write(self, line, pipe=None):
        pipe = pipe or self._pipe
        if pipe is None:
            raise PadForgeError("closed", "PadForge is not connected: " + self.why)
        try:
            pipe.write((line + "\n").encode("utf-8"))
        except PadForgeError:
            self._drop(pipe)
            raise PadForgeError("closed", "PadForge is not connected: the pipe closed.") from None
        return pipe

    def _send(self, line):
        """A line that needs no answer. While PadForge is not connected it is
        dropped: what it changed is put back from what the library keeps."""
        if not self._open:
            return False
        try:
            self._write(line)
        except PadForgeError:
            return False
        return True

    def _ask(self, line, timeout=None):
        tag = "@%d" % next(self._tags)
        pipe = self._pipe
        if pipe is None:
            raise PadForgeError("closed", "PadForge is not connected: " + self.why)
        entry = _Entry(pipe)
        with self._answers_lock:
            self._answers[tag] = entry
        try:
            self._write(tag + " " + line, pipe)
            if timeout is None:
                # A line that waits for the user's answer to this script's
                # first connection waits as long as PadForge's question
                # card does.
                timeout = 150.0 if self._waiting else 10.0
            if not entry.done.wait(timeout):
                raise PadForgeError("timeout", "PadForge did not answer.")
        finally:
            with self._answers_lock:
                self._answers.pop(tag, None)
        final = entry.final
        if final is None:
            raise PadForgeError("closed", "PadForge is not connected: the pipe closed.")
        verb = line.split(" ", 1)[0]
        if self._waiting and verb not in _COMMAND_SET:
            self._waiting = False
        if final.startswith("err "):
            self._raise(verb, final)
        words, pairs, js = split(final[2:].strip())
        return Answer(pairs, words, js, [Row(*split(r)) for r in entry.rows])

    def _raise(self, verb, final):
        parts = final.split(" ", 2)
        code = parts[1] if len(parts) > 1 else "internal"
        if code == "not-allowed":
            text = "PadForge refused %s: Automation's level is %s, too low for it. Raise it in Extras, Automation."
            raise PadForgeError(code, text % (verb, self.level or "unknown"))
        raise PadForgeError(code, "PadForge refused %s: %s (%s)." % (verb, parts[2] if len(parts) > 2 else "", code))

    def _timed(self, verb, lines):
        """A tap's or a sequence's lines, written one after the other with no
        wait between them, so their times on PadForge's clock stay as sent,
        and then each one's answer. PadForge refuses a timed step on a device
        that is not connected: the first refusal is raised in words, named by
        verb, the call's name."""
        pipe = self._pipe
        if pipe is None:
            raise PadForgeError("closed", "PadForge is not connected: " + self.why)
        entries = []
        try:
            for line in lines:
                tag = "@%d" % next(self._tags)
                entry = _Entry(pipe)
                with self._answers_lock:
                    self._answers[tag] = entry
                entries.append((tag, entry))
                self._write(tag + " " + line, pipe)
            timeout = 150.0 if self._waiting else 10.0
            for _, entry in entries:
                if not entry.done.wait(timeout):
                    raise PadForgeError("timeout", "PadForge did not answer.")
        finally:
            with self._answers_lock:
                for tag, _ in entries:
                    self._answers.pop(tag, None)
        self._waiting = False
        for _, entry in entries:
            if entry.final is None:
                raise PadForgeError("closed", "PadForge is not connected: the pipe closed.")
            if entry.final.startswith("err "):
                self._raise(verb, entry.final)

    def request(self, line):
        """Sends a line and waits for its answer: the ok line's pairs as a
        dict, an Answer with rows, words and json. Raises PadForgeError with
        PadForge's refusal in words."""
        return self._ask(line)

    # ── Events ──

    def _dispatch_loop(self):
        # One thread for the connection's whole life, across every pipe it
        # opens, so callbacks run one at a time and in order.
        while True:
            item = self._queue.get()
            if item is None:
                return
            try:
                if item[0] == "event":
                    self._event(item[1], item[2])
                else:
                    self._invoke(item[1], item[2])
            except Exception:
                self._report()

    def _invoke(self, fn, args):
        """Runs a callback with the arguments it takes. Its error is printed
        and kept in errors, and the next callback still runs."""
        count = _arity(fn)
        try:
            fn(*(args if count is None else args[:count]))
        except Exception:
            self._report()

    def _report(self):
        kind, error, _ = sys.exc_info()
        self.errors.append("".join(traceback.format_exception_only(kind, error)).strip())
        try:
            sys.stderr.write("A PadForge callback failed:\n")
            traceback.print_exc()
            sys.stderr.flush()
        except Exception:
            pass

    def _event(self, line, extra):
        words, pairs, _ = split(line)
        if len(words) < 2:
            return
        kind = words[1]
        if kind in ("b", "h", "a", "x", "state"):
            pad = self._by_id.get(words[2]) if len(words) > 2 else None
            if pad is not None:
                pad._fire(words, extra)
        elif kind == "dev":
            online = len(words) > 3 and words[3] == "online"
            # A device the script listed keeps its connection current.
            listed = self._by_id.get(words[2]) if len(words) > 2 else None
            if listed is not None:
                listed._online = online
            self._call("dev", words[2] if len(words) > 2 else "", online)
            if online:
                with self._lock:
                    for pad in list(self._wanting.values()):
                        if pad._generation != self._generation:
                            pad._find()
        elif kind == "profile":
            self._call("profile", words[2] if len(words) > 2 else "default")
        elif kind == "engine":
            self._call("engine", words[2] if len(words) > 2 else "")
        elif kind == "level":
            self.level = words[2] if len(words) > 2 else ""
            self._waiting = False
            self._call("level", self.level)
        elif kind in ("rumble", "trigrumble", "trigfx", "led", "player"):
            pad = self._by_id.get(words[2]) if len(words) > 2 else None
            if isinstance(pad, Controller):
                pad._feedback(words)
        elif kind == "lease":
            # The lapse put the script's own controllers at rest, and the
            # line that wakes the lease brings back only its holds and
            # claims. What the script set goes out again, and that line
            # wakes the lease.
            with self._lock:
                for ctl in list(self._mine.values()):
                    ctl._send_set()
        else:
            self._call("event|" + kind, pairs)

    def _call(self, key, *args):
        for fn in list(self._callbacks.get(key, ())):
            self._invoke(fn, args)

    def _add(self, key, fn):
        if not callable(fn):
            raise ValueError("A callback is a function.")
        self._callbacks.setdefault(key, []).append(fn)

    def _follow(self, kind, key, fn):
        self._add(key, fn)
        with self._lock:
            # Devices are always followed: a device found again needs them.
            if kind != "devices" and kind not in self._follows:
                self._follows.append(kind)
                if self._open:
                    self._ask("sub " + kind)

    def on_device(self, fn):
        """fn(id, online) when a device connects or disconnects."""
        self._follow("devices", "dev", fn)

    def on_profile(self, fn):
        """fn(name) when the profile changes, "default" for the default."""
        self._follow("profile", "profile", fn)

    def on_engine(self, fn):
        """fn(state): running, idle, stopping or stopped."""
        self._follow("engine", "engine", fn)

    def on_level(self, fn):
        """fn(level) when the user changes Automation's level."""
        self._add("level", fn)

    def on_event(self, kind, fn):
        """fn(pairs) for every event of this kind, with the line's key=value
        words as a dict: setting, press and choices for a plugin's declared
        settings."""
        self._add("event|" + kind, fn)

    # ── Devices ──

    def device(self, what):
        """A device by its script ID, its type or a part of its name. The
        same words give the same object. It is found again whenever PadForge
        connects and whenever a device connects while it is missing."""
        key = str(what).lower()
        with self._lock:
            pad = self._wanting.get(key)
            if pad is None:
                # A device devices() listed is the same object device() gives
                # for its script ID.
                listed = self._by_id.get(key)
                if listed is not None and not isinstance(listed, Controller) and listed._what.lower() == key:
                    pad = listed
                    self._wanting[key] = pad
                    if self._open and listed._generation < 0:
                        pad._find()
                    return pad
                pad = Device(self, what)
                self._wanting[key] = pad
                if self._open:
                    pad._find()
            return pad

    def devices(self):
        """Every device PadForge knows, connected or not, as device
        objects. A device listed here is the same object device() gives for
        its script ID, and it follows itself on its first use of any kind: a
        read, a hotkey, a change callback, a hold, set, tap or sequence."""
        with self._lock:
            out = []
            for row in self._ask("devices").rows:
                if not row.words or row.words[0] != "device":
                    continue
                pad = self._by_id.get(row.pairs.get("id", "")) or Device(self, row.pairs.get("id", ""))
                pad._row(row.pairs)
                out.append(pad)
            return out

    # ── Send Inputs ──

    def add_controller(self, key, name, buttons=(), axes=(), sliders=(), hats=(), gyro=False, accel=False,
                       touchpads=(), battery=False, feedback=()):
        """Adds a controller of the script's own. `key` keeps its identity,
        and its mappings, across runs, and the same key gives the same
        object. buttons, axes, sliders and hats are lists of names, or of
        dicts with index, name and, for an axis or slider, rest as a share of
        its travel from 0 to 100: 50 makes a stick. An axis rests at 50 and
        a slider at 0 when rest is left out. touchpads holds each touchpad's
        finger count, and feedback any of rumble, trigrumble, trigfx, light
        and player."""
        spec = {"buttons": list(buttons), "axes": list(axes), "sliders": list(sliders), "hats": list(hats),
                "gyro": bool(gyro), "accel": bool(accel), "touchpads": list(touchpads), "battery": bool(battery),
                "feedback": list(feedback)}
        with self._lock:
            ctl = self._mine.get(key)
            if ctl is None:
                ctl = Controller(self, key, name, spec)
                self._mine[key] = ctl
                if self._open:
                    ctl._declare()
            return ctl

    def set_value(self, name, value):
        """A named value Custom Expression macro triggers read. Set again
        after PadForge starts again."""
        with self._lock:
            self._values[name] = value
            if self._open:
                self._ask("var %s=%s" % (name, _value(value)))

    def log(self, text):
        """A line for this plugin's log on the Plugins page. Only a plugin
        has a log."""
        self._send("log " + quote(text))

    # ── Commands ──

    def switch_profile(self, name):
        return self._ask("activate " + quote(name))

    def release_profile(self):
        return self._ask("deactivate")

    def engine(self, on):
        return self._ask("engine " + ("on" if on else "off"))

    def controllers(self, on):
        return self._ask("controllers " + ("on" if on else "off"))

    def controller(self, number, on):
        return self._ask("slot %d %s" % (number, "on" if on else "off"))

    def show(self):
        return self._ask("show")

    def hide(self):
        return self._ask("hide")

    def snapshot(self):
        return self._ask("snapshot")

    def state(self):
        """What runs now, as JSON text."""
        return self._ask("state").json

    def outputs(self, number):
        """What virtual controller `number` sends to the game now, as a
        dict."""
        return self._ask("outputs %d" % number)

    def tool(self, name, json="{}"):
        """Runs an assistant tool, at the tool's own level:
        tool("press", '{"id": "..."}') presses what the user could. json is
        text or a dict. Returns JSON text."""
        text = json if isinstance(json, str) else _json.dumps(json)
        return self._ask("call %s %s" % (word(name), text)).json

    # ── The end ──

    def wait(self, seconds=None):
        """Waits while callbacks run: for good, for `seconds`, or until the
        connection closes for good (a plugin's when PadForge closes it)."""
        deadline = None if seconds is None else time.monotonic() + seconds
        while True:
            left = 0.25 if deadline is None else min(0.25, deadline - time.monotonic())
            if left <= 0 or self._ended.wait(left):
                return

    def close(self):
        """Closes the connection for good."""
        self._ended.set()
        pipe = self._pipe
        if pipe is not None:
            if self._open:
                self._send("bye")
            self._drop(pipe)
        self.why = "The script closed the connection."
        self._queue.put(None)

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()


# ── Devices ──

class _Numbered:
    """pad.button[0] and the like: a control by its numbered name."""

    __slots__ = ("_device", "_stem")

    def __init__(self, device, stem):
        self._device = device
        self._stem = stem

    def __getitem__(self, index):
        return self._device._read("%s%d" % (self._stem, index))

    def __setitem__(self, index, value):
        self._device._hold("%s%d" % (self._stem, index), value)


class Device:
    """A device PadForge reads, found by padforge.device(). Its attributes
    are its controls (see the top of this file)."""

    def __init__(self, connection, what):
        self._pf = connection
        self._what = str(what)
        self._id = ""
        self._name = ""
        self._type = ""
        self._online = False
        self._controls = {}
        self._by_index = {}
        self._values = {}
        self._hats = {}
        self._connected = False
        self._generation = -1
        self._holds = {}
        # Sets made before the device was found, put once when it is.
        self._sets = {}
        self._keys = {}
        self._lines = {}
        self._changes = {}
        self._on_connect = []

    def __repr__(self):
        return "Device(%s, %s, %r)" % (self._id or self._what, self._type, self._name)

    @property
    def id(self):
        """Its script ID, "" until it is found."""
        return self._id

    @property
    def name(self):
        return self._name

    @property
    def type(self):
        return self._type

    @property
    def connected(self):
        """True while PadForge reads it and the library follows it. A device
        from devices() that the script does not follow says whether PadForge
        has it."""
        return self._connected if self._generation >= 0 else self._online

    @property
    def controls(self):
        """Its controls: a dict of each name to a Control with name, kind
        (button, axis, slider or hat) and index."""
        with self._pf._lock:
            if not self._controls and self._id and self._pf._open:
                try:
                    self._caps()
                except PadForgeError as e:
                    self._pf.errors.append(str(e))
            out = {}
            for c in self._controls.values():
                kind = "button" if c.kind == "b" else "hat" if c.kind == "h" else "slider" if c.index >= 24 else "axis"
                out[c.name] = Control(c.name, kind, c.index)
            return out

    @property
    def button(self):
        return _Numbered(self, "Button")

    @property
    def axis(self):
        return _Numbered(self, "Axis")

    @property
    def slider(self):
        return _Numbered(self, "Slider")

    @property
    def hat(self):
        return _Numbered(self, "Hat")

    def __getattr__(self, name):
        if name.startswith("_"):
            raise AttributeError(name)
        return self._read(name)

    def __setattr__(self, name, value):
        if name.startswith("_") or isinstance(getattr(type(self), name, None), property):
            object.__setattr__(self, name, value)
        else:
            self._hold(name, value)

    def __getitem__(self, name):
        return self._read(name)

    def __setitem__(self, name, value):
        self._hold(name, value)

    # ── Finding it ──

    def _find(self):
        pf = self._pf
        with pf._lock:
            try:
                rows = pf._ask("devices").rows
            except PadForgeError as e:
                pf.errors.append(str(e))
                return
            want = self._what.lower()
            pick = spare = None
            for row in rows:
                if not row.words or row.words[0] != "device":
                    continue
                p = row.pairs
                if not (p.get("id") == self._what or p.get("type", "").lower() == want
                        or want in p.get("name", "").lower()):
                    continue
                if p.get("online") == "1":
                    pick = p
                    break
                if spare is None:
                    spare = p
            pick = pick or spare
            if pick is None:
                return
            self._row(pick)
            try:
                self._attach()
            except PadForgeError as e:
                pf.errors.append(str(e))

    def _row(self, p):
        pf = self._pf
        new = p.get("id", "")
        if self._id and self._id != new and pf._by_id.get(self._id) is self:
            del pf._by_id[self._id]
        self._id = new
        self._name = p.get("name", "")
        self._type = p.get("type", "")
        self._online = p.get("online") == "1"
        pf._by_id[new] = self

    def _attach(self):
        """Reads its controls, follows its whole state, and puts back what
        the script holds and claims on it."""
        pf = self._pf
        self._caps()
        self._generation = pf._generation
        pf._ask("sub %s state" % self._id)
        # Hotkeys, holds and change callbacks made before the controls were
        # known are read again by the device's own names.
        items = [item for key in self._keys.values() for item in key.items]
        self._keys = {}
        self._lines = {}
        for item in items:
            try:
                self.hotkey(item.name, item.fn)
            except Exception as e:
                pf.errors.append(str(e))
        changes = {}
        for lower, fns in self._changes.items():
            c = self._control(lower)
            changes.setdefault(c.name.lower() if c else lower, []).extend(fns)
        self._changes = changes
        held, parts = {}, []
        for name, value in self._holds.values():
            c = self._target(name)
            if c is None:
                pf.errors.append("No such control: " + name)
                continue
            held[c.name.lower()] = (c.name, value)
            parts.append("%s=%s" % (c.name, _wire(c.kind, value)))
        self._holds = held
        if parts:
            pf._send("own %s %s" % (self._id, " ".join(parts)))
        # Sets made before the device was found are put once now. One whose
        # name or value the device does not take goes to errors.
        sets, parts = self._sets, []
        self._sets = {}
        for name, value in sets.values():
            c = self._target(name)
            if c is None:
                pf.errors.append("No such control: " + name)
                continue
            try:
                parts.append("%s=%s" % (c.name, _wire(c.kind, value)))
            except (TypeError, ValueError) as e:
                pf.errors.append(str(e))
        if parts:
            pf._send("set %s %s" % (self._id, " ".join(parts)))

    def _follow_self(self):
        """A device devices() listed follows itself on its first use of any
        kind, a read, a hotkey, a change callback, a hold, set, tap or
        sequence, as one device() found does: its controls are read, its
        state and connection stay current, and PadForge finds it again after
        a restart. A device already wanted is found again by PadForge's
        restore and its device events."""
        if self._generation >= 0 or not self._id or isinstance(self, Controller):
            return
        pf = self._pf
        with pf._lock:
            key = self._what.lower()
            if self._generation >= 0 or key in pf._wanting:
                return
            pf._wanting[key] = self
            if not pf._open:
                return
            try:
                self._attach()
            except PadForgeError as e:
                pf.errors.append(str(e))

    def _caps(self):
        controls, by_index = {}, {}
        for row in self._pf._ask("caps " + self._id).rows:
            if not row.words or row.words[0] not in ("button", "axis", "hat"):
                continue
            p = row.pairs
            k = row.words[0][0]
            index = int(p.get("index", "0"))
            name = p.get("script") or _numbered_name(k, index)
            rest = int(p["rest"]) if "rest" in p else (32768 if index < 24 else 0)
            c = _Control(name, k, index, rest in (32767, 32768))
            controls[name.lower()] = c
            by_index[(k, index)] = c
        self._controls = controls
        self._by_index = by_index

    def _lost(self):
        """The pipe closed. Runs on the reader, so the callbacks wait for
        the dispatcher. A device the script listed is not connected until
        PadForge's list says it is again."""
        self._online = False
        if self._connected:
            self._connected = False
            self._pf._queue.put(("call", self._fire_connect, (False,)))

    def _fire_connect(self, connected):
        for fn in list(self._on_connect):
            self._pf._invoke(fn, (connected,))

    # ── The copy ──

    def _take(self, w, line):
        """An event into the copy, on the reader. Returns whether it goes on
        to the dispatcher, and what the dispatcher needs of it."""
        kind = w[1]
        try:
            if kind == "b":
                key, down = ("b", int(w[3])), w[4] == "1"
                if key in self._values and self._values[key] == down:
                    return False, None
                self._values[key] = down
            elif kind in ("h", "a"):
                key, value = (kind, int(w[3])), _read(w[4])
                if key in self._values and self._values[key] == value:
                    return False, None
                self._values[key] = value
            elif kind == "state":
                was = self._connected
                if len(w) > 3 and w[3] == "offline":
                    self._connected = False
                    return True, (was, False, None)
                hats = self._state(split(line)[1])
                self._connected = True
                return True, (not was, True, hats)
        except (IndexError, ValueError):
            return False, None
        return True, None

    def _state(self, p):
        text = p.get("b", "")
        for i in range(len(text) // 2):
            byte = int(text[i * 2:i * 2 + 2], 16)
            for bit in range(8):
                self._values[("b", i * 8 + bit)] = bool((byte >> bit) & 1)
        for i, v in enumerate(p.get("a", "").split(",")):
            if v:
                self._values[("a", i)] = _read(v)
        hats = []
        for i, v in enumerate(p.get("h", "").split(",")):
            if v:
                self._values[("h", i)] = _read(v)
                hats.append(_read(v))
        return hats

    def _control(self, name):
        """The control a name names, or None."""
        name = str(name)
        c = self._controls.get(name.lower())
        if c is not None:
            return c
        m = _NUMBERED.match(name)
        if m:
            stem = m.group(1).capitalize()
            k = "b" if stem == "Button" else "h" if stem == "Hat" else "a"
            index = int(m.group(2)) + (24 if stem == "Slider" else 0)
            return self._by_index.get((k, index)) or _Control(stem + m.group(2), k, index, stem == "Axis")
        m = _SHORT.match(name)
        if m:
            k, index = m.group(1).lower(), int(m.group(2))
            return self._by_index.get((k, index)) or _Control(_numbered_name(k, index), k, index, k == "a" and index < 24)
        return None

    def _target(self, name):
        """What a setting names: a control, or one direction of a hat, kind
        d, named by its hat's name and set as a button is. None for
        neither."""
        c = self._control(name)
        if c is not None:
            return c
        d = self._direction(name)
        if d is None:
            return None
        hat = self._by_index.get(("h", d.hat))
        return _Control((hat.name if hat else "Hat%d" % d.hat) + d.bit, "d", d.hat * 4 + _ENDS.index(d.bit), False)

    def _direction(self, name):
        """The hat direction a name names, or None."""
        name = str(name)
        lower = name.lower()
        for end in _ENDS:
            if len(name) <= len(end) or not lower.endswith(end.lower()):
                continue
            head = name[:-len(end)]
            if head.lower() == "hat":
                return _Direction(0, end, name)
            c = self._control(head)
            if c is not None and c.kind == "h":
                return _Direction(c.index, end, name)
        return None

    def _value(self, c):
        if not self._connected:
            return None
        return self._values.get((c.kind, c.index), False if c.kind == "b" else -1 if c.kind == "h" else 0)

    def _read(self, name):
        self._follow_self()
        name = str(name)
        lower = name.lower()
        for half in ("plus", "minus"):
            if len(name) > len(half) and lower.endswith(half):
                c = self._control(name[:-len(half)])
                if c is not None and c.kind == "a":
                    v = self._value(c)
                    if v is None:
                        return None
                    minus = half == "minus"
                    value = (-v if minus else v) if c.stick else ((50 - v) * 2 if minus else (v - 50) * 2)
                    return max(0, value)
        c = self._control(name)
        if c is not None:
            return self._value(c)
        d = self._direction(name)
        if d is not None:
            if not self._connected:
                return None
            return d.bit in directions(self._values.get(("h", d.hat), -1)).split("+")
        if not self._controls:
            # Not found yet: its controls are not known.
            return None
        raise AttributeError("No such control: " + name)

    # ── Holding ──

    def _hold(self, name, value):
        """Sets a control: holds it at the value, or gives it back for None.
        A hat's direction is held as a button is, over the hat's other
        directions: the hat itself, held or the device's own, with each held
        direction pressed or released over it. Before the device is found its
        controls are not known yet, and the hold waits under the name
        given."""
        self._follow_self()
        with self._pf._lock:
            c = self._target(name)
            if c is None and self._controls:
                raise AttributeError("No such control: " + name)
            key = c.name if c is not None else str(name)
            # A hold or a give-back takes the place of a set that waits.
            self._sets.pop(key.lower(), None)
            if _gone(value):
                self._holds.pop(key.lower(), None)
                if self._id and c is not None:
                    self._pf._send("rel %s %s" % (self._id, key))
                return
            self._holds[key.lower()] = (key, value)
            if self._id and c is not None:
                self._pf._send("own %s %s=%s" % (self._id, key, _wire(c.kind, value)))

    def _pairs(self, pairs, what):
        if len(pairs) % 2:
            raise ValueError("%s takes names and values in pairs." % what)
        out = []
        for i in range(0, len(pairs), 2):
            c = self._target(pairs[i])
            if c is None and self._controls:
                raise ValueError("No such control: %s" % pairs[i])
            out.append((c, str(pairs[i]), pairs[i + 1]))
        return out

    def hold(self, *pairs):
        """Holds several controls in one change: hold("LeftX", 50, "LeftY",
        -20). None for a value gives that control back."""
        self._follow_self()
        with self._pf._lock:
            own, rel = [], []
            for c, name, value in self._pairs(pairs, "hold"):
                key = c.name if c is not None else name
                self._sets.pop(key.lower(), None)
                if _gone(value):
                    self._holds.pop(key.lower(), None)
                    if c is not None:
                        rel.append(key)
                else:
                    self._holds[key.lower()] = (key, value)
                    if c is not None:
                        own.append("%s=%s" % (key, _wire(c.kind, value)))
            if self._id and own:
                self._pf._send("own %s %s" % (self._id, " ".join(own)))
            if self._id and rel:
                self._pf._send("rel %s %s" % (self._id, " ".join(rel)))

    def release(self, *names):
        """Gives controls back: release("A", "LeftX"), or all of them."""
        self._follow_self()
        with self._pf._lock:
            if not names:
                self._holds.clear()
                self._sets.clear()
                if self._id:
                    self._pf._send("rel %s all" % self._id)
                return
            rel = []
            for name in names:
                c = self._target(name)
                if c is None and self._controls:
                    raise ValueError("No such control: %s" % name)
                key = c.name if c is not None else str(name)
                self._holds.pop(key.lower(), None)
                self._sets.pop(key.lower(), None)
                if c is not None:
                    rel.append(key)
            if self._id and rel:
                self._pf._send("rel %s %s" % (self._id, " ".join(rel)))

    def _known(self, name):
        """The control or hat direction a method names. None before the
        device is found."""
        c = self._target(name)
        if c is None and self._controls:
            raise ValueError("No such control: %s" % name)
        return c

    def set(self, name, value):
        """Puts a value once, until the device's own input moves the
        control. Before the device is found its controls are not known yet:
        the value waits under the name given, and is put when the device is
        found. While PadForge is not connected it waits the same way, as a
        hold does, and is put when the device is found again."""
        self._follow_self()
        with self._pf._lock:
            c = self._known(name)
            if c is None:
                self._sets[str(name).lower()] = (str(name), value)
                return
            if not self._id or not self._pf._send("set %s %s=%s" % (self._id, c.name, _wire(c.kind, value))):
                self._sets[c.name.lower()] = (c.name, value)

    def _absent(self, verb):
        """The refusal of a timed action before the device is found: a tap
        sent later, at a moment the script did not choose, is not the tap it
        asked for. PadForge's own error while PadForge is not connected, else
        a read's of a device that is not connected."""
        pf = self._pf
        if not pf._open:
            return PadForgeError("closed", "PadForge is not connected: " + pf.why)
        return PadForgeError("offline", "PadForge refused %s: the device is not connected (offline)." % verb)

    def tap(self, name, ms=50, value=None):
        """Presses a control for ms milliseconds on PadForge's clock: a
        button or a hat's direction, or an axis to value (100 by default) and
        back. Refused before the device is found, and by PadForge while the
        device is not connected."""
        if not self._id:
            raise self._absent("tap")
        self._follow_self()
        c = self._known(name)
        if c is None:
            # Its controls are not read yet: PadForge is not connected, or
            # has not given them.
            raise self._absent("tap")
        if value is None:
            value = True if c.kind in ("b", "d") else 0 if c.kind == "h" else 100
        self._pf._timed("tap", ["own %s %s=%s in=0" % (self._id, c.name, _wire(c.kind, value)),
                                "rel %s %s in=%d" % (self._id, c.name, round(ms))])

    def sequence(self, *steps):
        """Taps controls one after the other, each held its ms:
        sequence("A", 50, "B", 50). Each starts as the one before lets
        go. Refused before the device is found, and by PadForge while the
        device is not connected."""
        self._follow_self()
        steps = self._pairs(steps, "sequence")
        if not self._id:
            raise self._absent("sequence")
        if any(c is None for c, _, _ in steps):
            raise self._absent("sequence")
        lines = []
        for i, (c, _, ms) in enumerate(steps):
            on = "1" if c.kind in ("b", "d") else "0deg" if c.kind == "h" else "100%"
            lines.append("own %s %s=%s %s" % (self._id, c.name, on, "in=0" if i == 0 else "after=0"))
            lines.append("rel %s %s after=%d" % (self._id, c.name, round(ms)))
        if lines:
            self._pf._timed("sequence", lines)

    # ── Hotkeys ──

    def hotkey(self, key_name, fn=None):
        """hotkey(key_name, fn): see the top of this file. fn gets the
        hotkey's name when it takes an argument. hotkey(key_name, "Off"), or
        None, removes it."""
        self._follow_self()
        with self._pf._lock:
            h = self._parse(key_name)
            kid = h.key.lower()
            if fn is None or (isinstance(fn, str) and fn.lower() == "off"):
                key = self._keys.get(kid)
                if key is not None:
                    key.items = [i for i in key.items if i.name.lower() != h.name.lower()]
                    if not key.items:
                        del self._keys[kid]
                        self._unclaim(key)
                    else:
                        self._claim(key)
                return
            if not callable(fn):
                raise ValueError('A hotkey needs a function or "Off": %s' % key_name)
            h.fn = fn
            key = self._keys.get(kid)
            if key is None:
                key = self._keys[kid] = _Key(h)
            key.items = [i for i in key.items if i.name.lower() != h.name.lower()] + [h]
            self._claim(key)

    def _parse(self, key_name):
        """A hotkey's words: its name, whether it leaves the control to the
        game, on release, and what it follows."""
        key_name = str(key_name)
        text = key_name.strip()
        passes = text.startswith("~")
        if passes:
            text = text[1:].lstrip()
        up = False
        m = _UP_DOWN.search(text)
        if m:
            up = m.group(1).lower() == "up"
            text = text[:m.start()].rstrip()
        m = _HOTKEY.match(text)
        if not m:
            raise ValueError("Not a control: " + key_name)
        name, op, limit = m.groups()
        if op:
            half, base = "", name
            lower = name.lower()
            if lower.endswith("plus") and len(name) > 4:
                half, base = "Plus", name[:-4]
            elif lower.endswith("minus") and len(name) > 5:
                half, base = "Minus", name[:-5]
            c = self._control(base if half else name)
            if c is None and half:
                c, half = self._control(name), ""
            if c is None and self._controls:
                raise ValueError(("Only an axis or a slider takes a limit: " if self._direction(name) is not None
                                  else "No such control: ") + key_name)
            if c is not None and c.kind != "a":
                raise ValueError("Only an axis or a slider takes a limit: " + key_name)
            limit = float(limit)
            low = op == "<"
            if half:
                stick = c.stick if c is not None else True
                if half == "Minus":
                    limit, low = (-limit if stick else 50 - limit / 2), not low
                else:
                    limit = limit if stick else 50 + limit / 2
            what = c.name if c is not None else name
            line = ("below=" if low else "above=") + _number(limit) + "%"
            return _Hotkey(key_name, passes, up, "l", what, line, what + " " + line)
        d = self._direction(name)
        if d is not None:
            return _Hotkey(key_name, passes, up, "d", d.name, "", d.name, d.hat, d.bit)
        c = self._control(name)
        if c is None and self._controls:
            raise ValueError("No such control: " + key_name)
        if c is not None and c.kind == "a":
            raise ValueError("An axis or slider hotkey needs a limit, such as LeftX > 80%: " + key_name)
        if c is not None and c.kind == "h":
            raise ValueError("A hat's directions are its hotkeys, such as %sUp: %s" % (c.name, key_name))
        what = c.name if c is not None else name
        return _Hotkey(key_name, passes, up, "b", what, "", what)

    def _claim(self, key):
        """Subscribes what a hotkey follows, claimed unless every hotkey on
        it leaves it to the game. A line's number comes back in the
        answer."""
        claim = any(not i.passes for i in key.items)
        if not self._id or not self._pf._open:
            key.claimed = -1
            return
        if key.kind != "l" and not claim:
            # A button or direction the game keeps: the whole state's
            # subscription carries its events.
            if key.claimed == 1:
                self._pf._ask("unsub %s %s" % (self._id, key.what))
            key.claimed = 0
            return
        if key.kind == "l" and key.claimed == (1 if claim else 0) and key.number:
            return
        if key.kind == "l" and key.number:
            self._lines.pop(key.number, None)
            self._pf._ask("unsub %s line=%d" % (self._id, key.number))
            key.number = 0
        answer = self._pf._ask("sub %s %s%s%s" % (self._id, key.what, " " + key.line if key.line else "",
                                                 " claim" if claim else ""))
        key.claimed = 1 if claim else 0
        if key.kind == "l":
            key.number = int(answer.get("line", "0") or 0)
            self._lines[key.number] = key

    def _unclaim(self, key):
        if not self._id or not self._pf._open:
            return
        if key.kind == "l" and key.number:
            self._lines.pop(key.number, None)
            self._pf._ask("unsub %s line=%d" % (self._id, key.number))
        elif key.claimed == 1:
            self._pf._ask("unsub %s %s" % (self._id, key.what))

    # ── Events ──

    def on_change(self, name, fn):
        """fn(value, name) each time a control changes: a button, a hat, or
        an axis as it moves, up to 100 times a second."""
        if not callable(fn):
            raise ValueError("A callback is a function.")
        self._follow_self()
        with self._pf._lock:
            c = self._control(name)
            key = (c.name if c is not None else str(name)).lower()
            self._changes.setdefault(key, []).append(fn)

    def on_connect(self, fn):
        """fn(connected) when the device connects or disconnects."""
        if not callable(fn):
            raise ValueError("A callback is a function.")
        self._follow_self()
        self._on_connect.append(fn)

    def _fire(self, w, extra):
        """An event's callbacks, on the dispatcher. They run after the
        model's lock is let go."""
        calls = []
        with self._pf._lock:
            kind = w[1]
            if kind == "b" and len(w) > 4:
                index, down = int(w[3]), w[4] == "1"
                c = self._by_index.get(("b", index))
                name = c.name if c is not None else _numbered_name("b", index)
                calls += self._change_calls(name, down)
                key = self._keys.get(name.lower())
                if key is not None and key.kind == "b":
                    calls += [(i.fn, (i.name,)) for i in key.items if i.up != down]
            elif kind == "h" and len(w) > 4:
                index, deg = int(w[3]), _read(w[4])
                c = self._by_index.get(("h", index))
                calls += self._change_calls(c.name if c is not None else _numbered_name("h", index), deg)
                before = directions(self._hats.get(index, -1)).split("+")
                after = directions(deg).split("+")
                self._hats[index] = deg
                for key in self._keys.values():
                    if key.kind != "d" or not key.items or key.items[0].hat != index:
                        continue
                    bit = key.items[0].bit
                    now = bit in after
                    if (bit in before) == now:
                        continue
                    calls += [(i.fn, (i.name,)) for i in key.items if i.up != now]
            elif kind == "a" and len(w) > 4:
                index = int(w[3])
                c = self._by_index.get(("a", index))
                calls += self._change_calls(c.name if c is not None else _numbered_name("a", index), _read(w[4]))
            elif kind == "x" and len(w) > 4:
                key = self._lines.get(int(w[5]) if len(w) > 5 else 0)
                if key is not None:
                    past = w[4] == "1"
                    calls += [(i.fn, (i.name,)) for i in key.items if i.up != past]
            elif kind == "state" and extra is not None:
                changed, connected, hats = extra
                for i, deg in enumerate(hats or ()):
                    self._hats[i] = deg
                if changed:
                    calls += [(fn, (connected,)) for fn in self._on_connect]
        for fn, args in calls:
            self._pf._invoke(fn, args)

    def _change_calls(self, name, value):
        return [(fn, (value, name)) for fn in self._changes.get(name.lower(), ())]


def _numbered_name(kind, index):
    if kind == "b":
        return "Button%d" % index
    if kind == "h":
        return "Hat%d" % index
    return "Slider%d" % (index - 24) if index >= 24 else "Axis%d" % index


# ── A controller of the script's own ──

# The words of the pipe a declared name may not take, as PadForge keeps
# them.
_RESERVED = frozenset(("gyro", "accel", "bat", "charging", "rest", "after", "in", "claim", "above", "below", "hyst",
                       "line", "all", "state", "buttons", "hats", "axes", "axis", "hz", "devices", "profile", "engine"))


def _word(label):
    """A declared name as scripts name it: its letters and digits, each
    word's first letter upper case, as PadForge makes it. None when nothing
    is left, it starts with a digit or it runs past 32 letters."""
    if label is None or not str(label).strip():
        return None
    out, start = [], True
    for ch in str(label):
        if ord(ch) < 128 and ch.isalnum():
            out.append(ch.upper() if start else ch)
            start = False
        else:
            start = True
    text = "".join(out)
    if not text or text[0].isdigit() or len(text) > 32:
        return None
    return text


def _looks_numbered(text):
    """Whether a word reads as a numbered control (Button3), the pipe's short
    form (b3), a touchpad's word (t0, tc0) or a hat's direction (HatUp)."""
    lower = text.lower()
    m = _SHORT.match(lower)
    if m and int(m.group(2)) < {"b": 256, "a": 32, "h": 4}[m.group(1)]:
        return True
    if _NUMBERED.match(lower):
        return True
    if len(lower) > 1 and lower[0] == "t" and (lower[1].isdigit() or lower.startswith("tc")):
        return True
    return lower.startswith("hat") and any(lower.endswith(d.lower()) for d in _ENDS)


def _declared(items, base, default_rest):
    """A declaration's list as PadForge reads it: (index, name, rest share)
    for each, the index from the item or its place."""
    out = []
    for place, item in enumerate(items):
        if isinstance(item, dict):
            index = int(item.get("index", place))
            label = item.get("name")
            rest = item.get("rest", default_rest)
        else:
            index, label, rest = place, item, default_rest
        if isinstance(rest, str):
            rest = _read(rest.strip().rstrip("%"))
        out.append((base + index, None if label is None else str(label).strip(), rest))
    return out


def _rests(items):
    """Axes with a rest as a share of the travel, for the declaration."""
    out = []
    for item in items:
        if isinstance(item, dict) and "rest" in item and not isinstance(item["rest"], str):
            item = dict(item, rest=_number(item["rest"]) + "%")
        out.append(item)
    return out


class Controller(Device):
    """A controller of the script's own on PadForge's device list. Map and
    assign it like any device. Setting an attribute sets that control:
    ctl.A = True, ctl.LeftX = -50, ctl.DPad = "Up". None puts it at rest."""

    def __init__(self, connection, key, name, spec):
        super().__init__(connection, key)
        self._key = key
        self._label = name
        self._spec = spec
        self._set = {}
        self._extra = {}
        self._feedbacks = {}
        self._connected = True
        self._shape()

    def __repr__(self):
        return "Controller(%s, %r)" % (self._key, self._label)

    @property
    def key(self):
        return self._key

    @property
    def name(self):
        return self._label

    @property
    def type(self):
        return "program"

    @property
    def connected(self):
        return bool(self._id) and self._pf._open

    @property
    def gyro(self):
        return self._extra.get("gyro")

    @gyro.setter
    def gyro(self, value):
        self._hold("gyro", value)

    @property
    def accel(self):
        return self._extra.get("accel")

    @accel.setter
    def accel(self, value):
        self._hold("accel", value)

    @property
    def battery(self):
        return self._extra.get("battery")

    @battery.setter
    def battery(self, value):
        self._hold("battery", value)

    @property
    def charging(self):
        return self._extra.get("charging")

    @charging.setter
    def charging(self, value):
        self._hold("charging", value)

    def _shape(self):
        """The controls the declaration names, as PadForge names them:
        buttons, then axes and sliders, then hats, each name taken once."""
        spec = self._spec
        taken = set()

        def pick(label, numbered):
            text = _word(label)
            if text is None or text.lower() in taken or text.lower() in _RESERVED or _looks_numbered(text):
                text = numbered
            taken.add(text.lower())
            return text

        controls, by_index = {}, {}

        def add(c):
            controls[c.name.lower()] = c
            by_index[(c.kind, c.index)] = c

        for index, label, _ in sorted(_declared(spec["buttons"], 0, 0), key=lambda t: t[0]):
            add(_Control(pick(label, "Button%d" % index), "b", index, False))
        axes = _declared(spec["axes"], 0, 50) + _declared(spec["sliders"], 24, 0)
        for index, label, rest in sorted(axes, key=lambda t: t[0]):
            add(_Control(pick(label, _numbered_name("a", index)), "a", index, rest == 50))
        hats = {}
        for index, label, _ in _declared(spec["hats"], 0, 0):
            hats[index] = label
        for index in range(max(hats) + 1 if hats else 0):
            add(_Control(pick(hats.get(index), "Hat%d" % index), "h", index, False))
        self._controls = controls
        self._by_index = by_index

    def _declare(self):
        spec = self._spec
        shape = {"buttons": spec["buttons"], "axes": _rests(spec["axes"]), "sliders": _rests(spec["sliders"]),
                 "hats": spec["hats"]}
        for part in ("gyro", "accel", "battery"):
            if spec[part]:
                shape[part] = True
        if spec["touchpads"]:
            shape["touchpads"] = spec["touchpads"]
        shape["feedback"] = spec["feedback"]
        p = self._pf._ask("declare key=%s name=%s %s" % (word(self._key), quote(self._label), _json.dumps(shape)))
        self._row({"id": p.get("id", ""), "name": self._label, "type": "program", "online": "1"})
        self._generation = self._pf._generation
        if not self._connected:
            self._connected = True
            self._pf._queue.put(("call", self._fire_connect, (True,)))
        # What the script set before, put back after PadForge starts again.
        self._send_set()

    def _send_set(self):
        """Everything the script set on the controller, as one change."""
        parts = [n + "=" + w for n, w in self._set.values()] + [self._special(k, v) for k, v in self._extra.items()]
        if parts:
            self._send(" ".join(parts))

    def _row(self, p):
        super()._row(p)
        self._name = self._label
        self._type = "program"

    def _lost(self):
        if self._connected:
            self._connected = False
            self._pf._queue.put(("call", self._fire_connect, (False,)))

    @staticmethod
    def _special(name, value):
        if name in ("gyro", "accel"):
            x, y, z = value
            return "%s=%s,%s,%s" % (name, _number(x), _number(y), _number(z))
        if name == "battery":
            return "bat=%d" % round(value)
        return "charging=%d" % (1 if value else 0)

    def _read(self, name):
        name = str(name)
        if name.lower() in ("gyro", "accel", "battery", "charging"):
            return self._extra.get(name.lower())
        c = self._control(name)
        if c is None:
            d = self._direction(name)
            if d is None:
                return super()._read(name)
            entry = self._set.get(self._hat_name(d).lower())
            return d.bit.lower() in [w.lower() for w in _hat_words(entry[1] if entry else "").split("+")]
        entry = self._set.get(c.name.lower())
        wire = entry[1] if entry else ""
        if c.kind == "b":
            return wire == "1"
        if c.kind == "h":
            if wire in ("", "-1"):
                return -1
            return _read(wire[:-3]) if wire.endswith("deg") else wire
        return 0 if wire == "" else _read(wire.rstrip("%"))

    def _rest_wire(self, c):
        return "0" if c.kind in ("b", "d") else "-1" if c.kind == "h" else "0%"

    def _hat_name(self, d):
        """The name of the hat a direction is of."""
        hat = self._by_index.get(("h", d.hat))
        return hat.name if hat else "Hat%d" % d.hat

    def _turn(self, d, value):
        """A hat's direction pressed or released (True, False or None): the
        hat the script set keeps it, for after a lapse or a restart, and the
        word that sends it, which PadForge puts into the hat as the change
        lands, beside the hat's other directions."""
        hat = self._hat_name(d)
        pressed = not _gone(value) and _wire("b", value) == "1"
        entry = self._set.get(hat.lower())
        words = _turn(_hat_words(entry[1] if entry else ""), d.bit, pressed)
        self._set[hat.lower()] = (hat, _wire("h", _direction_degrees(words)))
        return "%s%s=%s" % (hat, d.bit, "1" if pressed else "0")

    def _control_wire(self, name, value):
        c = self._control(name)
        if c is None:
            raise AttributeError("No such control: %s" % name)
        wire = self._rest_wire(c) if _gone(value) else _wire(c.kind, value)
        return c, "0%" if wire == "rest" else wire

    def _hold(self, name, value):
        """Sets a control of the script's own controller. None puts it at
        rest."""
        lower = str(name).lower()
        with self._pf._lock:
            if lower in ("gyro", "accel", "battery", "charging"):
                if _gone(value):
                    self._extra.pop(lower, None)
                    return
                if lower in ("gyro", "accel"):
                    value = tuple(value)
                self._extra[lower] = value
                self._send(self._special(lower, value))
                return
            if self._control(name) is None and self._direction(name) is not None:
                self._send(self._turn(self._direction(name), value))
                return
            c, wire = self._control_wire(name, value)
            self._set[c.name.lower()] = (c.name, wire)
            self._send("%s=%s" % (c.name, wire))

    def _send(self, controls):
        if self._id:
            self._pf._send("s %s %s" % (self._id, controls))

    def hold(self, *pairs):
        """Several controls as one change: hold("LeftX", 50, "LeftY", -20)."""
        if len(pairs) % 2:
            raise ValueError("hold takes names and values in pairs.")
        with self._pf._lock:
            parts = []
            for i in range(0, len(pairs), 2):
                if self._control(pairs[i]) is None and self._direction(pairs[i]) is not None:
                    parts.append(self._turn(self._direction(pairs[i]), pairs[i + 1]))
                    continue
                c, wire = self._control_wire(pairs[i], pairs[i + 1])
                self._set[c.name.lower()] = (c.name, wire)
                parts.append("%s=%s" % (c.name, wire))
            if parts:
                self._send(" ".join(parts))

    def release(self, *names):
        """Puts controls at rest: release("A"), or every control."""
        if not names:
            with self._pf._lock:
                self._set.clear()
                if self._id:
                    self._pf._send("rest " + self._id)
            return
        for name in names:
            self._hold(name, None)

    def set(self, name, value):
        self._hold(name, value)

    def tap(self, name, ms=50, value=None):
        """Refused before PadForge has the declaration, or while PadForge is
        not connected, as a tap on a device not found yet is."""
        if not self._id or not self._pf._open:
            raise self._absent("tap")
        c = self._target(name)
        if c is None:
            raise ValueError("No such control: %s" % name)
        if value is None:
            value = True if c.kind in ("b", "d") else 0 if c.kind == "h" else 100
        if not self._pf._send("s %s %s=%s" % (self._id, c.name, _wire(c.kind, value))):
            raise self._absent("tap")
        self._send("%s=%s in=%d" % (c.name, self._rest_wire(c), round(ms)))

    def sequence(self, *steps):
        if len(steps) % 2:
            raise ValueError("sequence takes names and times in pairs.")
        if not self._id or not self._pf._open:
            raise self._absent("sequence")
        lines = []
        for i in range(0, len(steps), 2):
            c = self._target(steps[i])
            if c is None:
                raise ValueError("No such control: %s" % steps[i])
            on = "1" if c.kind in ("b", "d") else "0deg" if c.kind == "h" else "100%"
            lines.append("%s=%s%s" % (c.name, on, "" if i == 0 else " after=0"))
            lines.append("%s=%s after=%d" % (c.name, self._rest_wire(c), round(steps[i + 1])))
        for i, line in enumerate(lines):
            if not self._pf._send("s %s %s" % (self._id, line)) and i == 0:
                raise self._absent("sequence")

    def touch(self, pad, finger, x, y, down=True, pressure=1):
        """A finger on touchpad pad: x and y from 0 to 1."""
        self._send("t%d.%d=%s,%s,%d,%s" % (pad, finger, _number(x), _number(y), 1 if down else 0, _number(pressure)))

    def click(self, pad, down=True):
        """A touchpad's click."""
        self._send("tc%d=%d" % (pad, 1 if down else 0))

    def hotkey(self, key_name, fn=None):
        raise TypeError("A controller of the script's own has no hotkeys. Follow a device PadForge reads with "
                        "padforge.device().")

    def on_rumble(self, fn):
        """fn(low, high) when a game rumbles the controller, 0 to 65535."""
        self._on("rumble", fn)

    def on_trigger_rumble(self, fn):
        """fn(left, right) for the trigger motors, 0 to 65535."""
        self._on("trigrumble", fn)

    def on_trigger_effects(self, fn):
        """fn(left, right): each trigger's 11-byte effect as 22 hex
        digits."""
        self._on("trigfx", fn)

    def on_light(self, fn):
        """fn("rrggbb")."""
        self._on("led", fn)

    def on_player(self, fn):
        """fn(number), from 1."""
        self._on("player", fn)

    def _on(self, kind, fn):
        if not callable(fn):
            raise ValueError("A callback is a function.")
        self._feedbacks.setdefault(kind, []).append(fn)

    def _feedback(self, w):
        kind = w[1]
        args = tuple(w[3:])
        if kind in ("rumble", "trigrumble", "player"):
            try:
                args = tuple(int(a) for a in args)
            except ValueError:
                pass
        for fn in list(self._feedbacks.get(kind, ())):
            self._pf._invoke(fn, args)

    def remove(self):
        """Takes the controller off PadForge's device list."""
        with self._pf._lock:
            self._pf._mine.pop(self._key, None)
            if self._id:
                self._pf._ask("gone " + self._id)


# ── The script's connection ──

def _pipe_name():
    """The pipe the script's connection opens: PadForge's own, or the one
    PADFORGE_PIPE names, as a test's host serves."""
    return os.environ.get(PIPE_VARIABLE) or PIPE


def _script_name():
    name = os.path.splitext(os.path.basename(sys.argv[0] if sys.argv and sys.argv[0] else ""))[0]
    return name if name and not name.startswith("-") else "Python script"


_default = None
_default_lock = threading.Lock()


def _connection():
    global _default
    with _default_lock:
        if _default is None:
            _default = Connection(_script_name())
        return _default


def connect(name=None, level="send-inputs", pipe=None):
    """Makes the script's connection with a name and the level it asks for:
    "read", "send-inputs" (the default) or "change-settings". Call it before
    anything else to choose them."""
    global _default
    with _default_lock:
        if _default is not None:
            _default.close()
        _default = Connection(name or _script_name(), pipe or _pipe_name(), level)
        return _default


def plugin(name, lease=2000):
    """Opens a plugin's connection: PadForge started this program, and its
    environment holds the one-time key and the pipe's name. The key is taken
    once, so a plugin connects once for each start and does not try
    again."""
    token = os.environ.get(TOKEN_VARIABLE)
    if not token:
        raise PadForgeError("not-a-plugin", "PadForge did not start this program as a plugin. Add it on the Plugins page.")
    c = Connection(name, os.environ.get(PIPE_VARIABLE) or PIPE, None, token, lease)
    if not c.connected:
        raise PadForgeError("closed", c.why)
    return c


def device(what):
    return _connection().device(what)


def devices():
    return _connection().devices()


def add_controller(key, name, buttons=(), axes=(), sliders=(), hats=(), gyro=False, accel=False, touchpads=(),
                   battery=False, feedback=()):
    return _connection().add_controller(key, name, buttons, axes, sliders, hats, gyro, accel, touchpads, battery,
                                        feedback)


def on_device(fn):
    _connection().on_device(fn)


def on_profile(fn):
    _connection().on_profile(fn)


def on_engine(fn):
    _connection().on_engine(fn)


def on_level(fn):
    _connection().on_level(fn)


def on_event(kind, fn):
    _connection().on_event(kind, fn)


def set_value(name, value):
    _connection().set_value(name, value)


def switch_profile(name):
    return _connection().switch_profile(name)


def release_profile():
    return _connection().release_profile()


def engine(on):
    return _connection().engine(on)


def controllers(on):
    return _connection().controllers(on)


def controller(number, on):
    return _connection().controller(number, on)


def show():
    return _connection().show()


def hide():
    return _connection().hide()


def snapshot():
    return _connection().snapshot()


def state():
    return _connection().state()


def outputs(number):
    return _connection().outputs(number)


def tool(name, json="{}"):
    return _connection().tool(name, json)


def request(line):
    return _connection().request(line)


def wait(seconds=None):
    """Keeps the script running while its callbacks do: for good, or for
    `seconds`."""
    _connection().wait(seconds)


def close():
    global _default
    with _default_lock:
        if _default is not None:
            _default.close()
            _default = None


def __getattr__(name):
    # padforge.connected, padforge.why and padforge.level read the script's
    # connection as they are now.
    if name in ("connected", "why", "level", "errors"):
        return getattr(_connection(), name)
    raise AttributeError("module 'padforge' has no attribute %r" % name)

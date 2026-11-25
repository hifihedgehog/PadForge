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

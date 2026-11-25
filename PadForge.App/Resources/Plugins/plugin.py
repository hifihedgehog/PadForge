"""A PadForge plugin in Python, from the PadForge Plugin Kit.

To run it, keep this file beside padforge.py, which the kit carries too, and
add it in PadForge: Extras, Plugins, Add a Plugin..., then pick this file, or
drop it on that page. PadForge shows the file, its signer and its SHA-256,
and runs it with your ordinary rights whenever the engine runs, in the Python
Windows opens .py files with. An edited file shows Approve and Restart on its
card.

What it does: it connects as a plugin with the one-time key PadForge put in
its environment, declares a controller with two buttons, a stick and rumble,
and four settings PadForge draws on its card: Speed, Tap Fire, Center and
Turns. It moves the stick around a circle at the Speed set, taps Fire once a
second while Tap Fire is on, counts its turns in Turns, writes to its log on
the Plugins page, and prints the feedback games send. Map the controller on
PadForge's Devices page like any other. README.md lists every verb, event
and kind of setting.
"""

import math
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import padforge  # noqa: E402

pf = padforge.plugin("Kit Plugin")
pad = pf.add_controller("kitpad", "Kit Pad", buttons=["Fire", "Jump"],
                        axes=[{"name": "Stick X"}, {"name": "Stick Y"}], feedback=["rumble"])
pad.on_rumble(lambda low, high: print("rumble", low, high, flush=True))
pf.log("Declared Kit Pad as %s." % pad.id)

# What the user sets on the plugin's card. Each declaration's answer carries
# the value set in an earlier run, and a change arrives as an event.
state = {"speed": 1.0, "fire": True, "center": False}


def setting(pairs):
    key, value = pairs.get("key"), pairs.get("value")
    if key == "speed":
        state["speed"] = float(value)
    elif key == "fire":
        state["fire"] = value == "true"
    else:
        return
    pf.log("%s is %s." % (key, value))


def press(pairs):
    if pairs.get("key") == "center":
        state["center"] = True


pf.on_event("setting", setting)
pf.on_event("press", press)
state["speed"] = float(pf.request('setting key=speed kind=number name=Speed min=0.5 max=4 step=0.5 unit=x default=1 '
                                  'help="How fast the stick goes round."')["value"])
state["fire"] = pf.request('setting key=fire kind=switch name="Tap Fire" default=true '
                           'help="Taps Fire once a second."')["value"] == "true"
pf.request('setting key=center kind=button name=Center help="Takes the stick back to the top."')
pf.request("setting key=turns kind=status name=Turns")

angle, last, shown = 0.0, time.monotonic(), -1
start = last
while pf.connected:
    now = time.monotonic()
    angle += (now - last) * math.pi * state["speed"]
    last = now
    if state["center"]:
        state["center"] = False
        angle = 0.0
    turns = int(angle / (2 * math.pi))
    if turns != shown:
        shown = turns
        pf.request("setting key=turns value=%d" % turns)
    # The stick's two axes and the button as one change.
    pad.hold("StickX", round(100 * math.sin(angle), 1), "StickY", round(-100 * math.cos(angle), 1),
             "Fire", state["fire"] and (now - start) % 1.0 < 0.2)
    time.sleep(0.02)

"""A PadForge plugin in Python, from the PadForge Plugin Kit.

To run it, keep this file beside padforge.py, which the kit carries too, and
add it in PadForge: Extras, Plugins, Add a Plugin..., then pick this file.
PadForge shows the file, its signer and its SHA-256, and runs it with your
ordinary rights whenever the engine runs, in the Python Windows opens .py
files with. An edited file is a new file, which PadForge asks about again
before it runs it.

What it does: it connects as a plugin with the one-time key PadForge put in
its environment, declares a controller with two buttons, a stick and rumble,
moves the stick around a circle and taps a button once a second, writes to
its log on the Plugins page, and prints the feedback games send. Map the
controller on PadForge's Devices page like any other.
"""

import math
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import padforge  # noqa: E402

pf = padforge.connect_plugin("Kit Plugin")
pad = pf.add_controller("kitpad", "Kit Pad", buttons=["Fire", "Jump"],
                        axes=[{"name": "Stick X"}, {"name": "Stick Y"}], feedback=["rumble"])
pad.on_rumble(lambda low, high: print("rumble", low, high, flush=True))
pf.log("Declared Kit Pad as %s." % pad.id)

start = time.monotonic()
while not pf.closed:
    t = time.monotonic() - start
    pad.send(a0=round(32768 + 32767 * math.cos(t * math.pi)),
             a1=round(32768 + 32767 * math.sin(t * math.pi)),
             b0=1 if t % 1.0 < 0.2 else 0)
    time.sleep(0.02)

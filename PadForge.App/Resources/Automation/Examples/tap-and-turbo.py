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

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

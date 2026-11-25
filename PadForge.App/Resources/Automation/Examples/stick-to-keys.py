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

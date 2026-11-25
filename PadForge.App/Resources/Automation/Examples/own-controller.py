# A controller of the script's own: a wheel that a pad's shoulders turn and
# its triggers drive, with a battery, motion and a touchpad, which hears the
# game's rumble. Map it on PadForge's Devices page like any other. Needs
# Send Inputs.
import padforge

wheel = padforge.add_controller(
    "padwheel", "Pad Wheel",
    buttons=["Shift Up", "Shift Down"],
    axes=[{"name": "Steer"}, {"name": "Gas", "rest": 0}, {"name": "Brake", "rest": 0}],
    gyro=True, touchpads=[2], battery=True,
    feedback=["rumble", "trigfx"])
wheel.battery = 100
wheel.on_rumble(lambda low, high: print("rumble", low, high, flush=True))

pad = padforge.device("gamepad")


def steer(by):
    wheel.Steer = max(-100, min(100, wheel.Steer + by))


def gas(value):
    wheel.Gas = value


def brake(value):
    wheel.Brake = value


pad.hotkey("LeftShoulder", lambda: steer(-10))
pad.hotkey("RightShoulder", lambda: steer(10))
pad.hotkey("Y", lambda: wheel.tap("ShiftUp", 80))
pad.hotkey("A", lambda: wheel.tap("ShiftDown", 80))
pad.on_change("RightTrigger", gas)
pad.on_change("LeftTrigger", brake)

padforge.wait()

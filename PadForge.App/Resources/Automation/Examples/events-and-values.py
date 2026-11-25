# What happens in PadForge, and a value for a macro: a device that comes
# or goes and a profile that changes are printed, and the left trigger's
# pull is handed to Custom Expression triggers as "pull". Needs Read, and
# Send Inputs for the value.
import padforge


def device_changed(device_id, online):
    print(device_id, "connected" if online else "disconnected", flush=True)


padforge.on_device(device_changed)
padforge.on_profile(lambda name: print("Profile:", name, flush=True))

pad = padforge.device("gamepad")
pad.on_change("LeftTrigger", lambda value: padforge.set_value("pull", value))
pad.on_connect(lambda connected: print(pad.name, "is here" if connected else "went", flush=True))

padforge.wait()

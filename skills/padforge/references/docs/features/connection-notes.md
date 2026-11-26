<!-- https://padforge.org/docs/features/connection-notes/ as PadForge's skill carries it, from padforge.org's wiki/features/connection-notes.md. -->
# Connection Notes

*A note on screen when a mapped device disconnects or comes back, the flyouts it shares a window with, and a device's connection as a value for macros and screens.*

<!-- SCREENSHOT: extras-overlays-what-shows (the Overlays page's What Shows card, every row) -->

---

## Turn them on

Open **Extras**, then **Overlays**. The **What Shows** card has a row for each flyout, each with its own switch and reset:

| Row | What it shows | Default |
|---|---|---|
| **Menu Overlay** | The ring, grid or bar of a menu while it is open. | On |
| **Shift Layer Flyout** | The layer's name when a shift layer turns on, for the time that layer sets. | On |
| **Profile Switch Overlay** | The profile's name when the profile changes. | On |
| **Controllers On or Off** | Whether the virtual controllers just turned on or off, from the shortcut or a macro. | On |
| **Device Disconnected Note** | A mapped device's name when it disconnects. | Off |
| **Device Connected Note** | A mapped device's name when it connects. | Off |

The rows are kept for the app and in each profile, so a profile can turn the notes on for one game. A settings file from before the **Controllers On or Off** row turns it on only if one of the other overlays was on.

## The notes

| Case | The line |
|---|---|
| A mapped device drops | **{device} disconnected**, with an amber mark, for four seconds |
| It was a controller's last device | **{device} disconnected. {controller} has no device left.** |
| A mapped device comes back | **{device} connected**, or **{device} connected, battery 80%** when the level is known, with a green check, for two seconds |
| Several within half a second | **{n} devices disconnected: {names}** or **{n} devices connected: {names}** |
| It comes back while its note shows | The same note turns to **{device} is back**, or **{n} devices are back: {names}** |
| A device that drops three times in a minute | **{device} dropped 3 times in a minute**, once, the count growing in the same note while it shows |

The **Controllers On or Off** note reads **Virtual Controllers Enabled** or **Virtual Controllers Disabled** for two seconds.

A device is named as the Devices page names it. A note counts each device assigned to a virtual controller that exists and is on, and that a person plugs in. Rows PadForge or an extra feeds, such as Twitch Chat, voice phrases, Game Telemetry, the Web Controller and Remote Link, raise no note.

### When a note shows

- A drop is said at once, a short one too, and every drop counts toward the three in a minute.
- While a device's note shows, the note follows the device in place: back, away again, and the count. It does not slide in again.
- Otherwise one note per device every ten seconds. A change inside that time is said when the time ends, if it still stands.
- After the count, that device's notes rest for five minutes.
- A drop PadForge causes itself says nothing and counts toward nothing: adding a virtual controller, cycling a USB port or a Bluetooth radio, reopening a controller driver, restarting a touchpad, changing a device's driver, or switching off the feature that reads a device.
- Nothing is said at startup, at engine start or stop, during a profile switch or across sleep. After the engine starts, notes wait until the devices have settled.
- While the flyout shows a profile switch or the controllers note, a device note waits for it.

## The flyouts

Every flyout, the profile name, the shift layer, the controllers note and the connection notes, shows at the bottom center of the monitor of the window in front, in that monitor's own scale, and lets every click through to the game. A line too long for the monitor ends in an ellipsis.

---

## A device's connection as a value

In a macro with a **Custom Expression** trigger, a variable row's **Source** can be **Device Connection**. Pick the device, connected or not. The variable reads 1 while that device is connected and 0 while it is not, on any controller or none. Its letter is the row's own, as for every variable, so with the connection on the first row, `!a` fires when the device drops.

An expression that reads a connection stays false until PadForge has listed the devices once, so `!a` does not fire at engine start. With no device connected, the macros that read a connection still run, so a "last device dropped" macro works. See [Macros](../guides/macros.md).

## Offline devices on a screen

[Keyboard screens](https://padforge.org/docs/features/keyboard-screens/) and the [Screen Overlay](screen-overlay.md) have **Offline Devices** in their **Show** list, and so does the **Show Keyboard Screen** macro action. It lists the mapped devices that are not connected, by the names the Devices page shows, and says **All connected** when none is missing.

<!-- https://padforge.org/docs/features/game-mods/ as PadForge's skill carries it, from padforge.org's wiki/features/game-mods.md. -->
# Game Mods

*Game mods made for DSX set your DualSense's adaptive triggers, lightbar, player lights and mute light through PadForge.*

Fans have written mods that give games without DualSense support adaptive triggers: the Grand Theft Auto games, Red Dead Redemption 2, Skyrim, Fallout 4, Escape from Tarkov, Forza, Wreckfest 2, Richard Burns Rally and others. Each mod watches its game and sends what the triggers and lights should do to a program named DSX, over the network on your own PC. PadForge can take DSX's place. Turn on **Accept DSX Game Mods** and the mods send to PadForge, which plays their effects on the DualSense and DualShock 4 controllers on your virtual controllers.

PadForge speaks the same interface as DSX and as the open programs that took it up: DualSenseY, DSZ and a fork of DS4Windows. A mod written for any of them works with PadForge unchanged.

<figure class="pf-video">
<video controls playsinline preload="none" width="1920" height="1080"
       poster="/assets/video/padforge-tutorial-dsx-game-mods-poster.jpg"
       aria-label="Video: DSX game mods with no DSX, recorded live in PadForge 5.2.1">
<source src="https://padforge.org/assets/video/padforge-tutorial-dsx-game-mods.mp4" type="video/mp4">
<track kind="captions" srclang="en" label="English" src="https://padforge.org/assets/video/padforge-tutorial-dsx-game-mods.vtt" default>
</video>
<figcaption>Accept DSX Game Mods on port 6969 and the DSX.exe stand-in, with the server listening, recorded live in PadForge 5.2.1 in 1 minute. PadForge 6 has since rebuilt the interface, so the screens differ from the steps on this page. No game or mod ran during the take. The narration is a generated voice, with English captions. <a href="https://padforge.org/assets/video/padforge-tutorial-dsx-game-mods-transcript.txt">Read the transcript</a>.</figcaption>
</figure>

---

## Turning it on

1. Open **Extras** in the sidebar and find the **Game Mods** tile under **Send It Elsewhere**. Turn on **Accept DSX Game Mods** on the tile, or open the tile and turn it on at the top of its page. It is off by default.
2. Leave **Game Mod Port**, under **More** on the Game Mods page, at **6969**, the port DSX and the mods use. The reset button beside it returns it to 6969.
3. Start the input engine from the flame on the **Home** page. The Game Mods page's status flame lights and its line reads **Listening on port 6969**.
4. Start the game with its mod installed.

While a mod sends, the line names it and the virtual controller its effects reach, for example *Listening on :6969. GTA5.exe is sending to virtual controller 1.* A mod that runs inside the game shows the game's program.

DSX and PadForge cannot both listen on one port. With DSX running on 6969 the line reads *Port 6969 is in use by another program, such as DSX. Close it or choose another port.* Close DSX, or give PadForge another port and set the mod to it. While the switch is on and the engine runs, PadForge tries the port again every 3 seconds, so the server starts on its own once DSX closes.

![The Game Mods page under Extras with Accept DSX Game Mods on](https://padforge.org/docs/images/dashboard-game-mods.png)

### Where mods find the port

Mods read the port from a file DSX writes. Mods made for DSX 2 read `C:\Temp\DualSenseX\DualSenseX_PortNumber.txt`, and some of them stop without it. Mods made for DSX 3.1 read `DSX_UDP_PortNumber.txt` in `%LOCALAPPDATA%\DSX` and use 6969 when it is missing. When the server starts, PadForge creates the first file if it is missing and corrects either file if it names another port. It makes the second file only for a port other than 6969, and it leaves both alone while DSX itself is running.

### Run a DSX.exe Stand-In

Some mods check that a program named DSX is running before they send anything. DualSense4GTAV, for one, turns itself off without it unless its connection messages are switched off in its menu. Turn on **Run a DSX.exe Stand-In**, under **More** on the Game Mods page, and PadForge runs a small program named DSX.exe while the server listens. It does nothing but wait, it ends when the server stops or PadForge closes, and Task Manager lists it as *PadForge stand-in for DSX game mods*. Leave the switch off unless a mod needs it.

---

## Which controller a mod drives

Mods name the controller they drive by number, and every mod checked for this release sends controller 1 (number 0 in the packets). PadForge counts the virtual controllers that have an online DualSense, DualSense Edge or DualShock 4 on them, a DualShock 4 through Sony's wireless adapter included, in the order of their numbers in the sidebar. A mod's controller 1 is the first of those, its controller 2 the second, and so on. A DualSense on virtual controller 2, behind a wheel on virtual controller 1, is still the mod's controller 1. When that list changes, because a virtual controller is turned on or off or one of those controllers arrives or leaves, a mod's effects move at once to the controller its number now names, and the controller they left drops them. A mod that sends to a number past the last of them gets a line that says so, for example *Listening on :6969. GTA5.exe is sending to controller index 1, and no virtual controller with a DualSense or DualShock 4 on it has that index.*

A DualShock 3 takes none of it. What a mod sets goes to every DualSense, DualSense Edge and DualShock 4 on that virtual controller:

| | DualSense and DualSense Edge | DualShock 4 |
|---|---|---|
| Adaptive triggers | Yes | No triggers to drive |
| Lightbar | Yes | Yes |
| Player lights | Yes | None on the controller |
| Mute light | Yes | None on the controller |

A mod's lightbar color also reaches the mice, keyboards and lighting rows this virtual controller lights, the way a game's color does (see [Lighting](lighting.md)).

An Xbox controller with impulse triggers plays a mod's trigger vibrations on them when **Translate Adaptive Trigger Vibrations** is on, in the [Impulse Triggers](https://padforge.org/docs/features/impulse-triggers/) card of the controller page's **Rumble & Haptics** section. Resistance has no impulse equivalent.

A Flydigi Apex pad on that virtual controller plays a mod's trigger effects the way it plays a game's: as resistance on an Apex 4 or Apex 5, rendered as vibration on an Apex 6 Pro. It plays them only on a virtual controller that also has a DualSense or DualShock 4 on it, since a mod's numbers count only those. No Flydigi pad was run with this build.

### Trigger thresholds

A mod can ask that a trigger count only past a point. Below that point the virtual controller reports the trigger as released, so a gun fires only once the trigger passes its resistance wall. This applies to the analog triggers of every virtual controller type: Xbox, PlayStation, Extended, and Nintendo layouts that have them. A Nintendo controller's ZL and ZR are buttons, and a threshold leaves them alone.

---

## How long a mod's effects last

A mod's effects stay until one of these happens:

- The program that sent them exits, or crashes. PadForge watches the process that owns the mod's connection.
- The mod resets the controller.
- You click **Clear Mod Effects**.
- You turn **Accept DSX Game Mods** off, or stop the engine.

When they end, your own settings return: the trigger settings in the [Adaptive Triggers](https://padforge.org/docs/features/adaptive-triggers/) card, the [Lighting](lighting.md) card's colors, and PadForge's player lights. A trigger with no setting of its own is released.

### What you see while a mod holds something

- On the [Devices](devices.md) page, the selected controller names the program and what it sets, such as *GTA5.exe is setting this controller's triggers and lights*, with a **Clear Mod Effects** button below. A mouse, keyboard or lighting row the virtual controller lights reads *GTA5.exe is setting this device's lights*.
- On the virtual controller's page, the **Adaptive Triggers** and **Impulse Triggers** cards in **Rumble & Haptics** and the **Lighting** card in **Lights & Sound** each show an orange line while a mod sets something there.

### Games and mods together

A game that writes to a virtual DualSense itself keeps the controller while it writes: its trigger and light writes win, and a mod's take over once the game stops writing. Your own settings come last. In practice a DSX mod runs with a game that has no DualSense support of its own, so the two rarely meet.

---

## Mods made for this interface

| Mod | Games |
|---|---|
| DualSense4GTA5 | GTA V |
| GTASense | GTA III, Vice City, San Andreas |
| DualSense4Rockstar | GTA IV, GTA V, Red Dead Redemption 2 |
| DSXSkyrim-NG, DSXFallout4 | Skyrim, Fallout 4 |
| TarkovDSX | Escape from Tarkov |
| ForzaDSXlegacy | Forza Horizon 4 and 5, Forza Motorsport 7 |
| WF2_DSX | Wreckfest 2 |
| RBR_Adaptive_Trigger | Richard Burns Rally |
| Race-Element | Driving and flight simulators |

PadForge's tests replay packets from TarkovDSX, DSXFallout4, DSXSkyrim-NG, DualSense4Rockstar, WF2_DSX, ForzaDSXlegacy, RBR_Adaptive_Trigger and Race-Element, and from two libraries mods are built with, DSX++ and a Godot plugin. Any other mod made for DSX speaks the same interface.

---

## Limits

- DSX's ten named trigger presets (Very Soft through Hardest, Choppy, Medium and others) were never published. PadForge sends the bytes DualSenseY sends for them, which came from DSX's own code, so they should feel as they do under DSX, but nobody has compared them on a controller side by side.
- No mod has yet been run against this release on a real DualSense.

See the technical page, [Game Mods Internals](https://padforge.org/docs/reference/game-mods-internals/).

---

*Last updated for PadForge 6.0.0.*

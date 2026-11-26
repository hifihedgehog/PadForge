<!-- https://padforge.org/docs/features/automation/ as PadForge's skill carries it, from padforge.org's wiki/features/automation.md. -->
# Automation

*Let launchers, scripts and assistants work with PadForge, each at a level you set and only after you allow it.*

Automation is the one door outside programs use to reach PadForge: a game launcher that switches profiles, a script that reads a pad or holds a button, an assistant that shows you where a setting lives. It is off until you turn it on. While it is off, no program can reach PadForge, and the page says so.

<!-- SCREENSHOT: extras-automation (the Automation page with a program connected, from the switch down to Activity) -->

---

## Turn it on

Open **Extras** in the sidebar and click the **Automation** tile, in the **With Other Programs** group. Turn on **Allow Outside Programs**, on the tile or at the top of the page. Home shows an Automation tile while it is on.

The status card under the switch says what is happening:

| Status | Meaning |
|---|---|
| **Off. No program can reach PadForge.** | Automation is off. |
| **On. No program is connected.** | Programs may connect, and none is. |
| **Connected: …** | The programs connected now, each named once. Plugins are left out of this line. |
| **Another program holds the pipe name …. Close that program.** | Another program took one of PadForge's pipe names first. PadForge serves nothing beside it and tries again while Automation is on. |
| **Windows refused the pipe …, so programs cannot connect.** | Windows would not create the pipe. |

Turning Automation off also releases a profile that a program had switched to.

The page also holds the [Assistants](assistants.md) card and the **Scripts** card, which installs PadForge's library for AutoHotkey, Python or PowerShell ([Scripts](scripts.md)).

---

## The level

The **What Programs May Do** card holds one setting, **Level**. It caps every program. A program asks for a level when it connects and gets what it asked for, up to this cap. Each level includes the ones above it in the table.

| Level | What a program may do |
|---|---|
| **Commands** | Switch profiles and release them, start and stop the engine, turn virtual controllers on and off, show and hide the window, close PadForge, ask what is running, save a diagnostics snapshot, and start, stop and mark a problem recording. The default. |
| **Read** | See devices, virtual controllers, mappings, profiles and settings, follow the inputs of every device as they happen, and show you a place in PadForge. |
| **Send Inputs** | Add devices of its own and drive them, hold a control of any device, and hand named values to macro triggers. |
| **Change Settings** | Change what you can change on the pages. Some changes always ask you first on PadForge's question card. |

A program at **Read** or higher sees every key pressed on every device, keyboards and mice included, and keys typed into windows that run as administrator. The footnote at the bottom of the page says so too.

---

## A program asks once

A program that asks for **Commands** gets it at once. A program that asks for **Read** or more works at **Commands** until you answer PadForge's question card, the first time it connects:

**Allow *program* to work with PadForge at *level*?**

The card names the program the way Windows knows it, never by the name the program gives itself:

- a script by its interpreter and its script file as the command line names it, as **python.exe running bot.py**, or by the module it runs, as **python.exe running -m bot**,
- a script run with no file, through `-c`, `-Command` or `-EncodedCommand`, by the words of its command line, shortened past 200 characters, so each command is a program of its own,
- an assistant's helper or PadForge's own command line by the program that started it, as **Started by …**, or as **Started by a program PadForge could not identify.** when that program has ended or Windows will not name it,
- any other program by its path,
- with its signer, **Signed by …** or **Not signed.**

**Allow** adds the program to **Allowed Programs** at the level the card names. Each of its connections open then works at that level or the level it asked for, whichever is lower. **Don’t Allow** refuses it until PadForge starts again. With no answer in two minutes the card counts as no, and the program waits ten minutes before it may ask again.

A program PadForge could not identify is never added to **Allowed Programs**, and no answer to its card is kept, since PadForge cannot tell it from another program. **Allow** holds for its connections open then, and its next connection asks again. A program that connects twice while its card is up waits on the one answer. Adding an assistant on its row of the [Assistants](assistants.md) card is your answer for that assistant, and no card asks.

Consent tells you which program is asking. It is no barrier against software already running as you, which could start an allowed program itself.

### The question card

The card sits at the bottom right of the screen PadForge's window is on, above other windows. It takes no focus, so it never pulls you out of a game, and it never raises PadForge's window. Its buttons ignore clicks and keys that PadForge itself sends while a program drives it. To answer from the keyboard, use the same question in **Waiting for You** on the Automation page. An answer in either place ends the question in both.

The card shows one question at a time, with **1 of 3** while more wait, and the time left: **No answer in 1:45 counts as no.** A program may have three questions waiting, and all programs ten. It hides while a PadForge dialog is open and comes back with the time it had left.

### Changes that always ask

At **Change Settings**, a program may change what you can change on the pages, with these exceptions. Each asks on the card with its reason, every time or when its row says:

| Reason on the card | What asks |
|---|---|
| **Opens a file, a folder or a web page.** | Links and buttons that open a place. |
| **Makes PadForge send keys or mouse input to Windows.** | Keyboard and mouse outputs in a macro or a mapping. |
| **Imports a profile from outside PadForge.** | Importing a profile or a config file. A card that knows the profile names each extra it holds that listens on the network. |
| **Listens on the network, so other devices can reach this PC.** | Turning on an extra that listens on the network. Head Tracking's switch asks only when it would start its UDP input. A profile switch that would turn such an extra on. Turning on **Switch Profiles by Game**, or adding a game to a profile's list while it is on, when a profile it could put in force holds such an extra, **Default** included, whether the extra is on or off now. Turning an extra off, or on when it is on already, never asks. |
| **Changes a driver or HidHide, which other programs and games rely on.** | Driver installs and removals, and HidHide. |
| **Changes which programs can read the device, through Use Only in PadForge.** | **Use Only in PadForge**. |
| **PadForge asks you before a program does this.** | A button behind a confirm, such as a profile's **Delete**, **Clear All** and **Save Current Setup Here**, and a layer tab's **Paste Rows into Layer** and **Clear Layer Rows**. **Save Current Setup Here** names each extra that listens on the network it would put into the profile, and **Paste Rows into Layer** names each key the rows would press on a keyboard and mouse controller. |

For a setting, the card names the setting and the value it would take. For an extra that listens on the network, it names the profile that turns the extra on, as in **The profile “Racing” turns on Web Controller**. If what the change would do grows while the card waits, PadForge refuses it, and the program has to ask again.

### What only you can do

Some controls no program can reach at any level: the whole Automation page, its approvals and the windows it opens, the question card, the Plugins page's own controls, the web and network switches and their cards, the address the controller database downloads from, access codes such as the Web Controller's, the review of a problem recording and **What to Record**, the path of the settings file, and the connected mark in the title bar. A program that tries is refused, and **Activity** shows **Only you can change this**.

---

## Waiting for You

This card shows while a question waits. Each row gives the whole question, every detail line included, with **Don’t Allow** and **Allow**.

## Allowed Programs

This card lists each program you allowed, while it lists one. A row gives the program, its signer, its level and **Connected now.**, **Last used …** or **Not used yet.**, with **Remove**.

**Remove** takes the program off the list. A connection it has open drops to **Commands** at once and lets go of whatever that level does not allow, and the program asks again the next time it connects. Allowing a listed program at a higher level raises it. The list keeps up to 64 programs and drops the one unused longest. It lives in PadForge.xml as `AllowedPrograms`.

### A program's reach

Each row has **Reach**: **All of PadForge**, the default, or **Only Its Own Devices and These**, which holds the program to the devices it added and the ones you tick in **Pick Devices…**. A program held this way drives and changes those devices alone. It may hold their controls and set their inputs, and change or press only what belongs to one of them, and every action of the **Commands** level is refused, such as switching profiles or stopping the engine. It still reads every device, keyboards and mice included. The row says so: **Drives its own devices and Xbox Wireless Controller. It still reads every device.** A call outside the reach is refused, and **Activity** shows **Outside the reach you set for it**. A change of reach applies to an open connection at once and lets go of holds outside it.

A program on this PC can still send the one-line commands of the [command line](../reference/command-line.md) through `PadForge.Control`, as every program can, and its row says that too: **Commands from the command line still reach PadForge, as they do for every program.**

A program may ask, when it connects, to drive only its own devices, and its question card then says **It asks to drive only its own devices.** It then never has all of PadForge: with **All of PadForge** on its row it drives its own devices alone, the row says **It asks to drive only its own devices.** while it is connected, and **Activity** shows **Outside the reach it asked for** for a call outside them. The devices you pick on its row still apply. What it asks for can narrow the reach on its row and never widen it. Network keys and web assistants carry the same **Reach** on their rows of the [Assistants](assistants.md) card.

## Activity

This card shows while it holds a call. It lists every call each program made this session, newest first: the time, the program, what it asked and what came of it, **Done** or the reason it was refused.

- A run of identical calls from one program that changed nothing shows as one line with its count. **Open** lists each call's time.
- **Undo** sits on the newest line of a program's latest run of changes. A run lasts until that program goes 60 seconds without a change or another program changes something. Undo puts back the active profile's controllers, assignments, mappings and macros, the profile list and the app settings as they were before the run.
- Activity keeps up to 100,000 calls. Past that the oldest go, and the card says how many went.
- **Export…** saves the list as a text file you name, oldest first, one tab-separated line a call. **Clear** empties it.

Activity is kept in memory only. PadForge writes no log file of it. It leaves the PC only when you export it or when a [problem recording](problem-recorder.md) carries it.

---

## Output held from administrator windows

PadForge runs as administrator, so the keys and clicks it sends reach windows that run as administrator, which a program without those rights cannot reach by itself. So PadForge holds back key and mouse output that a program feeds while the window in front, or the window under the pointer for a click, runs as administrator. Releases always go out, so nothing stays pressed. Output from your own devices alone goes out to every window, whether or not a program drives PadForge.

Each key press, button and pointer move carries its cause. A program's device, a program's hold on a control and a value a script set each mark what they feed. The mark stays with a macro they started or fed, through every later and timed step of that run, and with a shift layer, toggle, turbo or Custom Expression they changed, until your own input sets it again. A row that reads both your device and a program's is held.

The first time output is held during a program's session, **Activity** shows one line, such as **Key and mouse output it feeds is held back from Registry Editor, which runs as administrator**, with **Held**. An assistant learns it from `add_device`, `hold_control`, `set_device_inputs` and `set_value`, which say so when they put the hold in force, and from `status`, which says while it holds, names the window's program when output was held from it, and says when the engine stops while PadForge is behind other windows.

---

## The connected mark

While any program other than a plugin is connected, a small mark in the title bar, between the profile switcher and the window buttons, shows how many. It shows a globe while one of them came from the web, and a network sign while one is an [assistant on this network](assistants.md#assistants-on-this-network). Hover it to see who is connected, and click it to open Automation. It is hidden on Home and on the Automation page, where a card already names who is connected.

---

## What programs read

Reads are scrubbed. What a program reads carries no user name, PC name, serial number, Bluetooth address, folder path or access code, and no name you gave a device. A value only you can change is not read at all.

Programs reach PadForge through two local named pipes, `PadForge.Control` for one-line commands and `PadForge.Automation` for everything else. Each admits your own programs at medium integrity or above, in your own Windows session, plus administrators and the system, and refuses the network and app containers. PadForge creates both pipes first and sets their owner to Administrators, and PadForge's own clients send nothing to a pipe of those names with any other owner. `PadForge.Control` serves while Automation is on. `PadForge.Automation` also serves while [Plugins](plugins.md) run.

No program needs administrator rights to connect. A launcher, a script, an assistant and a plugin all run with your ordinary rights.

---

## From the old External Control setting

**Allow External Control by Launchers and Scripts** on the Profiles page became Automation. A settings file that had it on opens with Automation on at **Commands**, so a launcher that switched profiles keeps working. The **External Control** card on the Profiles page now says what Automation allows, with **Open Automation**.

## Related pages

- [Assistants](assistants.md): adding an assistant, the tools it calls, and assistants on the web.
- [Plugins](plugins.md): programs that add devices.
- [Scripts](scripts.md): AutoHotkey, Python and PowerShell.
- [Command Line](../reference/command-line.md): every switch, the exit codes and a PowerShell function for the pipe.

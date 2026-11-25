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
| **Commands** | Switch profiles and release them, start and stop the engine, turn virtual controllers on and off, show and hide the window, close PadForge, save a diagnostics snapshot and ask what is running. The default. |
| **Read** | See devices, virtual controllers, mappings, profiles and settings, follow the inputs of every device as they happen, and show you a place in PadForge. |
| **Send Inputs** | Add devices of its own and drive them, hold a control of any device, and hand named values to macro triggers. |
| **Change Settings** | Change what you can change on the pages. Some changes always ask you first on PadForge's question card. |

A program at **Read** or higher sees every key pressed on every device, keyboards and mice included, and keys typed into windows that run as administrator. The footnote at the bottom of the page says so too.

---

## A program asks once

A program that asks for **Commands** gets it at once. A program that asks for **Read** or more works at **Commands** until you answer PadForge's question card, the first time it connects:

**Allow *program* to work with PadForge at *level*?**

The card names the program the way Windows knows it, never by the name the program gives itself:

- a script by its interpreter and its script file, as **python.exe running bot.py**,
- an assistant's helper or PadForge's own command line by the program that started it, as **Started by …**,
- any other program by its path,
- with its signer, **Signed by …** or **Not signed.**

**Allow** grants the level it asked for and adds the program to **Allowed Programs**. **Don’t Allow** refuses it until PadForge starts again. With no answer in two minutes the card counts as no, and the program waits ten minutes before it may ask again. A program that connects twice while its card is up waits on the one answer. Adding an assistant on its row of the [Assistants](assistants.md) card is your answer for that assistant, and no card asks.

Consent tells you which program is asking. It is no barrier against software already running as you, which could start an allowed program itself.

### The question card

The card sits at the bottom right of the screen PadForge's window is on, above other windows. It takes no focus, so it never pulls you out of a game, and it never raises PadForge's window. Its buttons ignore clicks and keys that PadForge itself sends while a program drives it. To answer from the keyboard, use the same question in **Waiting for You** on the Automation page. An answer in either place ends the question in both.

The card shows one question at a time, with **1 of 3** while more wait, and the time left: **No answer in 1:45 counts as no.** A program may have three questions waiting, and all programs ten. It hides while a PadForge dialog is open and comes back with the time it had left.

### Changes that always ask

At **Change Settings**, a program may change what you can change on the pages, with these exceptions. Each asks on the card every time, with its reason:

| Reason on the card | What asks |
|---|---|
| **Opens a file, a folder or a web page.** | Links and buttons that open a place. |
| **Makes PadForge send keys or mouse input to Windows.** | Keyboard and mouse outputs in a macro or a mapping. |
| **Imports a profile from outside PadForge.** | Importing a profile or a config file. |
| **Listens on the network, so other devices can reach this PC.** | Turning on an extra that listens on the network. Turning one off does not ask. |
| **Changes a driver or HidHide, which other programs and games rely on.** | Driver installs and removals, and HidHide. |
| **Changes which programs can read the device, through Use Only in PadForge.** | **Use Only in PadForge**. |

For a setting, the card names the setting and the value it would take.

### What only you can do

Some controls no program can reach at any level: the whole Automation page and its approvals, the question card, the Plugins page's own controls, the web switch and its card, access codes such as the Web Controller's, the review of a problem recording and **What to Record**, the path of the settings file, and the connected mark in the title bar. A program that tries is refused, and **Activity** shows **Only you can change this**.

---

## Waiting for You

This card shows while a question waits. Each row gives the whole question, every detail line included, with **Don’t Allow** and **Allow**.

## Allowed Programs

This card lists each program you allowed, while it lists one. A row gives the program, its signer, its level and **Connected now.**, **Last used …** or **Not used yet.**, with **Remove**.

**Remove** takes the program off the list. A connection it has open drops to **Commands** at once and lets go of whatever that level does not allow, and the program asks again the next time it connects. Allowing a listed program at a higher level raises it. The list keeps up to 64 programs and drops the one unused longest. It lives in PadForge.xml as `AllowedPrograms`.

## Activity

This card shows while it holds a call. It lists every call each program made this session, newest first: the time, the program, what it asked and what came of it, **Done** or the reason it was refused.

- A run of identical calls from one program that changed nothing shows as one line with its count. **Open** lists each call's time.
- **Undo** sits on the newest line of a program's latest run of changes. A run lasts until that program goes 60 seconds without a change or another program changes something. Undo puts back the active profile's controllers, assignments, mappings and macros, the profile list and the app settings as they were before the run.
- Activity keeps up to 100,000 calls. Past that the oldest go, and the card says how many went.
- **Export…** saves the list as a text file you name, oldest first, one tab-separated line a call. **Clear** empties it.

Activity is kept in memory only. PadForge writes no log file of it. It leaves the PC only when you export it or when a [problem recording](problem-recorder.md) carries it.

---

## The connected mark

While any program other than a plugin is connected, a small mark in the title bar, between the profile switcher and the window buttons, shows how many. It shows a globe while one of them came from the web. Hover it to see who is connected, and click it to open Automation. It is hidden on Home and on the Automation page, where a card already names who is connected.

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

<!-- https://padforge.org/docs/features/problem-recorder/ as PadForge's skill carries it, from padforge.org's wiki/features/problem-recorder.md. -->
# Problem Recorder

*Record a problem as it happens, read every part of the recording before it leaves the PC, and send what you would rather not post by a private route.*

A problem recording is one zip that holds what PadForge saw while the problem happened: your settings and devices, every input and output, force feedback, timing, the window and its errors, what programs asked through Automation, and PadForge's diagnostics lines. A developer can replay it as the devices that made it. Private values are replaced before anything is written, and you read the result, part by part, before you save it.

<!-- SCREENSHOT: settings-record-problem (the Diagnostics card with Record a Problem recording) -->

---

## Record a problem

Open **Settings**, the **Data & Privacy** group, the **Diagnostics** card.

1. Click **Start Recording** on the **Record a Problem** row.
2. Make the problem happen. The button counts the time and the size: **Stop Recording (1:42, 3.4 MB)**.
3. Click **Stop Recording**. PadForge writes the summary and opens the recording for you to read.

A recording stops by itself after 30 minutes or at 20 MB in the zip, whichever comes first. It follows up to 256 devices. Every key and button pressed on every device while it records is in it, keyboards and mice included.

Before the first line is written, PadForge checks that it can keep this PC's own names, serials and addresses out of the file. If a value would get through, the recording does not start: **Recording did not start. A check found a private value of this PC that would not be kept out of the file.**

You can also start, stop and mark a recording from outside the page:

- **Mark the Recording** in the tray menu, while something is being recorded, puts a mark at that moment.
- A macro's **Problem Recording** action, in the System group, has **Start or Stop Recording**, **Mark the Recording** and **Save the Last 60 Seconds**, so you never leave the game.
- `PadForge.exe --record start`, `--record stop` and `--record mark [words]`. See [Command Line](../reference/command-line.md).
- An assistant's **record_problem** tool records for 3 to 60 seconds.

### What to Record

Under the Diagnostics card's **More**, **What to Record** has a switch for each part, all on:

**Settings and Devices**, **Input**, **Output**, **Feedback**, **Timing**, **Window and Errors**, **Activity**, **Diagnostics Lines**.

A part turned off is left out of the next recording, and the file says which parts it holds. The switches are grayed out while a recording runs.

### Keep the Last 60 Seconds

For a problem that comes once an evening, turn on **Keep the Last 60 Seconds** under More. While the engine runs, PadForge holds the last minute of what a recording holds, in memory only. When the problem happens, click **Save the Last 60 Seconds**, or use the macro action, and the minute opens for you to read like any recording. A mark made with no recording running goes into the minute kept.

**Write Typed Letters and Numbers as One Key**, also under More, writes the letter and number keys that no mapping or macro reads as one key, so a recording does not hold what you typed. Every other key, and every key a mapping or macro reads, is written as pressed.

---

## Read Your Recording

When you stop, the recording opens over the page as **Read Your Recording**: when it ran, how long, and why it stopped (**Stopped by you.**, **Stopped by itself at …**, **Stopped by a macro.** and so on).

The left side lists the **Summary** and the eight parts. Click a part to see it as it will be shared, with its size as text and in the zip. Turn a part's switch off to leave it out of what you save.

The scrub check line says what PadForge replaced: **Scrub check: PadForge replaced 12 values of 3 private kinds. None of this PC’s names, serials or addresses is in the parts you keep.** It runs again each time you turn a part off or on. If one of this PC's own values is still in a part you keep, the line says so, and **Save…** and **Save and Post** stay off until you turn that part off.

| Button | What it does |
|---|---|
| **Delete** | Deletes the recording. |
| **Copy Summary** | Puts the summary on the clipboard, for the body of a bug report. |
| **Save…** | Saves the zip where you pick, to Downloads the first time and then where you last saved, and shows it in Explorer. It is written as you, even while PadForge runs as administrator. With parts turned off, the saved zip holds only the parts you kept. |
| **Save and Post** | Saves the zip as **Save…** does, then copies the summary, shows the file in Explorer and opens a new Q&A discussion on PadForge's GitHub page, where reports go. Paste the summary there and drop the file on it. If you cancel the save, nothing is copied or opened. |

PadForge sends nothing itself. It runs no server, so a report reaches the developer only when you post it.

### A recording that waits for you

A recording that stops while nobody is at the page, at its limit, from a macro, a program, an assistant, `--record stop` or `--snapshot`, waits for this review. Home shows **A problem recording from 21:14 is waiting for you to read it.** with **Review** and **Delete**. Closing the review also leaves the recording waiting. A recording that waits is deleted after seven days unless you save it, since it holds what was pressed. A program is told that the recording waits, never where it is.

A recording PadForge did not stop, because PadForge closed or crashed, is finished at the next start and waits on Home.

While a recording runs, its file is in your own temp folder, in `PadForge Recordings`.

### Save a Snapshot

**Save a Snapshot…** on the Diagnostics card takes PadForge, Windows, your devices and settings as they are now, with the most recent engine events, and opens them through the same review. It is a recording with no events of its own, and works even when the diagnostics log is off.

### Read a recording's summary

`PadForge.exe --summarize <file>` prints a recording's summary, the same lines Copy Summary copies. It needs no running PadForge.

---

## What a recording holds

Beside the inputs and outputs, a recording holds:

- While the window does not answer: the operation the window was running, the last message it took, how much processor time its thread used, and Windows' wait chain of that thread, which says whether it waits or runs and names a deadlock.
- A device call that takes long, with its device, and an engine that stops running passes.
- A health line every ten seconds: memory, processor, handles, window and drawing objects, threads, .NET collections and power.
- A line when the whole PC was held up.
- Every exception, the window thread's included, up to 40, and how the window was shown.
- Force effects made, stopped, refused and failed, and what a game reads back through XInput.
- Driver versions, the Windows build and display scale, and every setting changed while it recorded.
- Automation's [Activity](automation.md#activity), when that part is on.

After a crash in native code, PadForge copies Windows' own record of it (Application Error, event 1000: the faulting module, its version, the offset and the exception code) into `crash.log` at its next start.

### What is replaced

Before anything is written, PadForge replaces your user name, PC name, domain and folders, each device's serial number, Bluetooth address and instance path, network addresses and host names, access codes, keys and long tokens, Remote Link peers and pairing codes, email addresses, and the names you typed for devices, profiles, macros, layers, NFC tags, voice phrases and chat words. Each becomes a stand-in such as `<user 1>` or `<serial of D1>`, so a developer can still tell one from another.

---

## Send a recording

A recording is scrubbed before it is written, and the review shows exactly what it holds. Once you have read it, you may attach it to a public bug report or discussion.

Anything you would still rather not post publicly goes by a direct message to the maintainer on [PadForge's Discord](https://discord.gg/qawTZHVhNH) instead. A recording fits in a Discord message as a file, since it stops at 20 MB in the zip. Post the report without it, and say in the report that a file was sent privately.

---

## Make a memory dump

Make a memory dump only when a recording and `crash.log` do not explain the problem, or when asked for one. A memory dump is not scrubbed. It can hold anything that was in PadForge's memory, typed text included. Never attach it to a public issue or discussion: post the report without it, and say a dump exists. A dump is far larger than the file a Discord message carries, so it goes by link:

1. Zip the dump.
2. Upload the zip to a file host of your choice, such as OneDrive or Google Drive.
3. Send only the share link, by a direct message to the maintainer on [PadForge's Discord](https://discord.gg/qawTZHVhNH). Where the host allows it, share the file with the maintainer alone rather than with anyone who has the link.
4. Delete the upload once the issue is solved.

PadForge runs as administrator, so the tool that dumps it must too. Several copies of PadForge.exe can run at once: the app, plus helpers for assistants (`--mcp`), the web (`--web`) and vendor libraries (`--native-host`). Dump the app by its process ID, never by its name.

### Find PadForge's process ID

Open PowerShell as administrator and run:

```powershell
Get-CimInstance Win32_Process -Filter "Name='PadForge.exe'" |
    Where-Object { $_.CommandLine -notmatch '--(mcp|web|native-host|record)' } |
    Select-Object ProcessId, CommandLine
```

The process listed is the app. Its ID is the number to use below. In Task Manager, the app is the PadForge.exe with the window named PadForge.

### With ProcDump

1. Download [ProcDump](https://learn.microsoft.com/sysinternals/downloads/procdump) from Microsoft and unzip it, for example into `C:\ProcDump`.
2. In the same administrator PowerShell, make a folder for the dump: `mkdir C:\PadForge-dumps`.
3. Run the command for what happens, with the app's ID in place of `1234`:

    | When PadForge | Command |
    |---|---|
    | is frozen now | `C:\ProcDump\procdump.exe -accepteula -ma 1234 C:\PadForge-dumps` |
    | freezes now and then | `C:\ProcDump\procdump.exe -accepteula -ma -h 1234 C:\PadForge-dumps` |
    | closes by itself | `C:\ProcDump\procdump.exe -accepteula -ma -e 1234 C:\PadForge-dumps` |

    `-ma` writes a full dump, with all of PadForge's memory. `-h` waits for a window that does not answer for five seconds, and `-e` for an exception nothing catches. Leave the window open until ProcDump writes the file.

4. ProcDump saves a file whose name starts with PadForge and ends in `.dmp`. Right-click it and choose **Compress to**, **ZIP File**, and send the zip's link as above.

### With Task Manager

While PadForge is frozen, open Task Manager, go to **Details**, right-click the app's `PadForge.exe` and choose **Create memory dump file**. Task Manager says where it saved the file.

---

## Related pages

- [Troubleshooting: Logs and diagnostics](../troubleshooting.md#logs-and-diagnostics)
- [Plugins: Replay](plugins.md#replay)

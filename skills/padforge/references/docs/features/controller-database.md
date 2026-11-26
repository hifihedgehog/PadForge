<!-- https://padforge.org/docs/features/controller-database/ as PadForge's skill carries it, from padforge.org's wiki/features/controller-database.md. -->
# Controller Database

*How SDL reads each controller, where its mapping came from, and how to copy, paste, find or guess one, with your own rows kept in PadForge.xml.*

PadForge reads gamepads through SDL, which knows a controller's layout from a mapping: one row that says which button is A, which axis is the left stick, and so on. SDL carries its own rows, PadForge's build adds the community SDL_GameControllerDB, and you can download newer community rows or add your own. Every device's panel shows which row it reads through.

<!-- SCREENSHOT: devices-sdl-mapping (a gamepad's SDL Mapping card with More open, one row marked differs) -->

---

## The SDL Mapping card

Select a gamepad or joystick on the **Devices** page. The **SDL Mapping** card sits under **Test Inputs**.

| Line | What it says |
|---|---|
| **Read as** | **A gamepad, "…"** with the name in SDL's mapping, **A joystick. SDL has no mapping for it.**, or **A joystick. PadForge reads it raw, so SDL’s mapping is not used.** A device that is not connected says **SDL reads devices while the engine runs.** |
| **From** | Where the mapping came from: **SDL**, **The community copy built in** with its date, **Downloaded** with its date, **PadForge’s own file**, **Yours, added** with its date, or **The SDL_GAMECONTROLLERCONFIG variable, which SDL holds above any row you add**. |
| **ID** | The device's ID as SDL reports it. |

The buttons under it:

| Button | What it does |
|---|---|
| **Copy SDL Mapping** | Puts the device's mapping on the clipboard as a row of the community database. |
| **Paste SDL Mapping…** | Takes a row for this device, checks it, keeps it as yours and applies it. |
| **Remove Mine** | Shows in place of Paste while you have a row for the device. Puts back the row and the controller rows from before. |
| **Find a Mapping…** | Searches every row PadForge carries by name and applies the one you pick, for a clone whose IDs differ. |
| **Guess a Layout** | Guesses a gamepad layout from the device's axes, buttons and hats, for a device no database knows. Shows while you have no row for it and SDL has no mapping. |

### More

The card's **More** holds:

- A table for the controller the device is on, with **Controller** to pick one when it is on several. Each of that controller's rows that reads the device shows its target, **Its input now**, and **SDL’s row says**, where SDL's row places that target. A line where the two differ is marked **differs**.
- **Not used by PadForge: …**, the fields of SDL's row that no row of the device's controllers reads.
- **The row** itself, under the community ID.
- **Map Again**, which makes the device's rows on each controller it is on again from SDL's layout, as assigning it does.
- **Community Database Page**, which opens the community database's guide to new mappings.

---

## Copy a mapping

**Copy SDL Mapping** writes the device's row in the community database's form, under the device's ID with its checksum and version zeroed, as the community's rows are. The bus stays: SDL compares it, so a Bluetooth pad's row keeps its own.

While SDL maps the device, the row is SDL's mapping. For a raw joystick, the row is built from the rows of the controller picked in More. SDL's form holds only a layout, so the result says what it left out: **Copied. SDL’s form cannot hold macros, curves and dead zones.** The five things it can leave out are macros, curves, dead zones, keyboard and mouse outputs, and turbo, toggles and other actions.

To share a mapping with everyone, send the row to the community database. **Community Database Page** under More opens its guide.

## Paste a mapping

**Paste SDL Mapping…** opens **Paste an SDL Mapping for …** with the clipboard's row filled in when it passes the check. It takes one row or a whole `gamecontrollerdb.txt`, skips comments and rows for other systems, and picks the row SDL would read this device through, then one of the same vendor and product, then the first. The dialog says what it found, such as **A row for this device. Windows, 23 fields, its ID matches this device.**, and which controllers' rows are mapped again from the new layout.

A row is refused, with the reason, when it is empty, over 1,024 characters, not a row of SDL's database, has an ID that is not 32 hex digits, a name over 128 characters, a platform other than Windows, a hint or Android field, a field or binding SDL does not know, or a field given twice.

**Paste** keeps the row as yours in PadForge.xml under this device's own ID and hands it to SDL at once, while the engine runs and the device is connected. Otherwise it is kept and used once both are true. Your row wins for that device over every other source except the `SDL_GAMECONTROLLERCONFIG` variable, which SDL keeps above it, and the card says so. The **From** line then reads **Yours, added …**.

The device's rows on its controllers are mapped again from the new layout once SDL reads the device through your row. Profile shortcuts that read the device keep the inputs they were set to.

**Remove Mine** deletes your row, gives SDL back the mapping it had, and puts back the device's rows from before: for the active profile at once, for other profiles the next time each is active.

## Find a Mapping and Guess a Layout

**Find a Mapping…** lists every row PadForge carries, SDL's, the downloaded rows, PadForge's own and yours, with this device's rows first. Search by name, ID or VID:PID, pick a row and click **Apply**. It goes through the same check as Paste and is kept under this device's own ID, which is what a clone with its own IDs needs. With the engine stopped, SDL's own rows are missing from the list.

**Guess a Layout** builds a layout from the device's axes, buttons and hats and shows it as **A guessed layout.** Check it in **Test Inputs** before you click **Apply**. Once applied it is your row like any other.

---

## The Controller Database row

On **Settings**, the **Updates** card holds the **Controller Database** row. Its line counts what PadForge reads, **Built in: … mappings, community copy of …** with the date of the copy the release carries, and after a download adds **Downloaded …: … new rows, … changed.**, or **Downloaded … from …: … new rows.** for an address you set.

**Update Now** downloads the community database from its own repository on GitHub, or from the address set under **Download From**, over HTTPS, only when you click it. PadForge keeps, in PadForge.xml, the rows this release lacks, with the time, the source and a SHA-256, and uses them at once (**In use at once.**).

- A changed row replaces the community copy's row for that device, never SDL's own or PadForge's.
- A new row is used only for a controller that nothing else reads as a gamepad, that is not assigned, and that is not in Force Raw Joystick Mode.
- A downloaded row maps no device's rows again.
- A file is refused whole when it is over 4 MB or under 100 KB, not plain UTF-8 text, or missing more than a fifth of the rows PadForge knows: **PadForge refused the download because it is not the controller database it expects. Nothing changed.**

The row's reset removes every downloaded row and puts the community's address back. A new release of PadForge drops the downloaded rows it now carries itself.

### Download From

Under the row's **More**, **Download From** shows the address **Update Now** downloads from: the community's own file on GitHub unless you set another. **Change…** takes another HTTPS address, such as a mirror of the community's file when GitHub is out of reach. The address must start with `https://`, name a host and use the default port, with no user name or password, and every redirect must stay on that host, three at most. From an address that is not the community's, PadForge keeps new rows only: a file from elsewhere carries no date that tells a newer row from an older one, so a changed row from there could roll back a row this release carries. The row's line then names the host the rows came from. **Use the Community’s File** puts the community's address back, and so do the row's reset and **Reset to Defaults**. Only you can change the address: no assistant, script or plugin can.

### Your Rows

Under the row's **More**, **Your Rows** lists how many rows of yours PadForge keeps. **Add Rows From a File or the Clipboard…** takes many rows at once, for many devices, through the same check as Paste, and keeps each as yours under its own ID. **Clear** removes them all and gives each device back its reading. Your rows win for their devices over every other source and stay through the download's reset. PadForge keeps up to 512.

See also: [Devices](devices.md), [SDL3 Integration](https://padforge.org/docs/reference/sdl3-integration/).

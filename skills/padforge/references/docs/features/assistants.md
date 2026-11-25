<!-- https://padforge.org/docs/features/assistants/ as PadForge's skill carries it, from padforge.org's wiki/features/assistants.md. -->
# Assistants

*An assistant that speaks the Model Context Protocol (MCP) reads PadForge's state, changes settings at the level you allow, and points at places in PadForge in its own words.*

An assistant works through [Automation](automation.md), at the level set there. It reaches PadForge through PadForge's own tools and never by driving the screen: PadForge's instructions to it forbid screenshots to decide clicks, simulated keys or mouse, and turning Automation on or raising its level. When you say "show me", it shows you.

<!-- SCREENSHOT: extras-automation-assistants (the Assistants card with two rows, one added and connected) -->

---

## Add an assistant

The **Assistants** card on the Automation page lists each assistant program on this PC whose maker documents its setup on Windows: nine programs, found by their package, their command on the PATH or their install folder. PadForge checks again each time the page shows. Each row has the program's own icon, its state and one button.

| State | Button | Meaning |
|---|---|---|
| **Not added.** | **Add** | PadForge is not in this program's settings. |
| **Added.** | **Remove** | PadForge is in its settings, with its skill. |
| **Added. Connected now.** | **Remove** | It is connected to PadForge now. |
| **Added. It has not connected since. An assistant that was open sees PadForge after it is closed and opened again.** | **Remove** | Close the program and open it again. |
| **Added for another PadForge. Update points it at this one.** | **Update** | Its entry starts a PadForge.exe in another folder. |
| **Added. Its PadForge skill is older than this PadForge.** | **Update** | |
| **Added, without its PadForge skill.** | **Update** | |
| **Added. Its PadForge skill comes from a newer PadForge.** | **Remove** | |

A row says **Connected now.** when the program behind a connection is that row's program, as Windows names it. A command npm installed is read for the program it runs, so its row sees it connect too. **It has not connected since** shows for a program added on its row since PadForge started that has not connected after.

**Add** writes PadForge's entry into the program's settings and puts PadForge's skill where that program reads skills. Where the maker gives the program its own add command, PadForge runs that command. Otherwise it edits the program's settings file, under its own key. A packaged app's real data folder is found and used. Everything is written with your own rights, as you, even while PadForge runs as administrator. **Add** is also your consent for that assistant, for the program its row found, an npm-installed one included: it is allowed at **Change Settings**, capped by the level on the page, and no question card asks. **Remove** takes out the entry and the skill and forgets the consent.

The menu button beside each row's button holds:

- **Open Its Settings File**, which opens the program's settings file, or its folder.
- **Save the Skill as a ZIP…**, for the one program that takes skills only as an upload. The row then says where in that program to upload it.
- **Remove**, while the button says **Update**.

While Automation is off, the first row adds: **Added assistants reach PadForge once Automation is on.**

### Any other assistant

Under the card's **More**, **Other Assistants** has two buttons, each confirmed with **Copied.**:

- **Copy Settings** copies the entry for an MCP settings file: `{"mcpServers":{"padforge":{"command":"<path to PadForge.exe>","args":["--mcp"]}}}`.
- **Copy a Message** copies a short text for any chat that says how to connect to PadForge and what to ask first.

An assistant added this way asks on the question card the first time it connects.

---

## How it starts

An assistant runs `PadForge.exe --mcp`, a small helper with no window and no engine that serves MCP over standard input and output and talks to the running PadForge through its pipe. It runs with the assistant's rights.

When a call needs PadForge, PadForge is closed and Automation is on in PadForge.xml, the helper starts PadForge minimized through the normal start, at most once every 90 seconds, and the call waits up to a minute for it. Windows may ask for PadForge's rights. With Automation off it starts nothing, and its **status** tool tells the assistant to ask you to turn Automation on.

---

## What an assistant can do

PadForge's catalog holds every setting, button, card, section, page, dialog, menu item, device, virtual controller, profile, macro and plugin, by a stable ID, with the words the page and screen readers use. Search reads the same catalog. Six tools reach every entry, each at its level and by the entry's class (free, asks first, or yours alone):

| Tool | Level | What it does |
|---|---|---|
| **find** | Read | Finds places by words or by kind, each with its ID, words, place, value, range or choices, whether it is on and shown, and its class. 15 results by default, up to 100, and it says how many it left out. |
| **read** | Read | Reads one place: a setting, or a page, card, section, dialog, device, controller or plugin with every label, value, status line, list row and button under it. It changes nothing on screen. |
| **where** | Read | Says what you see: the window's state, the page and section, the open dialogs, a Windows dialog waiting for you, the controller, device and profile picked, and the pointer's step. |
| **show** | Read | Points at a place, or walks you through several. See below. |
| **set** | Change Settings | Changes a setting, text box, list or choice through its page control, or opens a section. It refuses a value out of range and a control the page has turned off. |
| **press** | Change Settings | Presses a button, menu item or an open dialog's button. A press that opens a dialog answers with the dialog's text and buttons. |

Beside them, typed tools cover profiles, virtual controllers, devices, mappings, macros, a problem recording, a program's own devices, undo and named values for macro triggers: 37 tools in all, under a budget of 39. `PadForge.exe --tools` lists them with their levels.

A Windows file chooser or message box that PadForge cannot read is reported as waiting for you, and what was asked carries on once you answer it.

## The guided pointer

**show** rings a place and puts the assistant's words beside it in a callout, headed by its name. The words are cleaned of control characters and cut at 300. A step moves on when you click the ringed place (the default, and the callout says **Click it to go on.**), after a number of seconds from 1 to 60, or at the assistant's next call. With several steps it shows **Step 2 of 5.**, up to 20 steps.

With a walk, the pointer rings each click on the way: the sidebar entry, the controller page's section and its part, the extra's tile, the Settings group, each closed card and **More**, and the row the place is on, such as another device's row or a mapping row. When the Devices page's search box or one of its segments hides that row, or the mapping list's search box or scope does, the pointer rings that first, and the walk goes on once the row shows. You do the clicking: a walk opens, picks and empties nothing for you. A click you already made is skipped.

One callout shows at a time. It stays until you click elsewhere, press a key outside the ringed place, press **Close**, the next step comes, or ten minutes pass. Shift, Ctrl, Alt, the Windows keys and Print Screen leave it up, so a screenshot keeps it. It is never placed over an **Allow** or **Don’t Allow** button, and it cannot point at a place that is yours alone.

When PadForge's window is behind another, the question card asks **… wants to show you …**, with **Not Now** and **Show**. Only your click on **Show** brings the window forward. No program can raise it.

---

## The skill

PadForge's skill tells an assistant what PadForge is and how to work with it: the tools, the mapping model, a description of every page generated from the catalog, and the links to these docs pages. It lives in the repository at [skills/padforge](https://github.com/hifihedgehog/PadForge/tree/main/skills/padforge) and travels inside PadForge.exe. **Add** installs it, MCP's Skills extension serves it, and a row says when an installed copy is older than PadForge.

| Command | What it does |
|---|---|
| `PadForge.exe --skill [name]` | Prints the skill, or one of its files. |
| `PadForge.exe --describe <folder>` | Writes the description of every page, as Markdown and JSON. |
| `PadForge.exe --install skill <program or folder>` | Installs the skill, with `--remove` or `--check`. |
| `PadForge.exe --tools [--json]` | Lists the tools with their levels and arguments. |
| `PadForge.exe --call <tool> [json] [--json]` | Calls one tool through the same gates, and asks on the question card when it needs consent. |

The [Command Line](../reference/command-line.md) page has every switch and exit code.

---

## Assistants on the web

An assistant that runs on a website connects from its maker's servers, so it needs a public address that reaches your PC. PadForge gives it one through a tunnel program you already have, and lets a new web assistant in only while you are watching.

### Turn it on

Under the Assistants card's **More**, turn on **Reach PadForge From the Web**. PadForge asks first:

> **Reach PadForge from the web?** PadForge starts a helper without administrator rights that listens on this PC only, and the tunnel program you pick gives it a public address. A web assistant gets in only after you allow its sign-in on PadForge’s question card, and it works at the level above.

**Turn On** starts the helper, `PadForge.exe --web`, with your ordinary rights, no engine and no window. It listens on `127.0.0.1` only, on the port it used last or one Windows reports free, and checks every request's Host and Origin. PadForge refuses any web helper it did not start itself. The helper waits while Automation is off, starts again after 2, 10 and 30 seconds if it stops by itself, and ends with PadForge. A web assistant reaches no file on the PC: it sends and takes a profile as content.

### The Assistants on the Web card

The card shows under Assistants while the switch is on.

| Row | What it does |
|---|---|
| **Tunnel** | **None**, each tunnel program found on this PC, or **An Address I Paste**. Picking one starts it, with your rights, inside the helper's job. Picking another, or turning the switch off, stops it. |
| **Address** | The public address to give the assistant, ending in `/mcp`, with **Copy** and a button that opens one web assistant's own add page with the address filled in. |
| **Accept a New Web Assistant** | **Start Accepting** opens the door for five minutes: **Accepting until 21:40.** **Stop Accepting** closes it. One allowed sign-in closes it too. |
| **Allowed on the Web** | Each web assistant you allowed, with where its sign-in returned, when it was allowed and last used, and **Revoke**. |

PadForge finds four tunnel programs, on the PATH or in their usual install folders, and downloads none:

| Program | Keeps its address |
|---|---|
| cloudflared | No. A quick tunnel takes a new address each run. |
| devtunnel | No. |
| ngrok | Yes. |
| Tailscale Funnel | Yes. |

When the address changes, the assistant has to be added again with the new one. To use a tunnel that keeps its address, such as a named Cloudflare tunnel or a persistent dev tunnel, run it yourself to `http://127.0.0.1:<port>` (the card gives the port), pick **An Address I Paste** and paste its https address.

One maker's own tunnel runs PadForge's stdio command directly, with no address or sign-in here. Its row has **Copy Command**, which copies `"<path to PadForge.exe>" --mcp` for that tunnel's setup.

### Signing in

The helper runs its own OAuth 2.1 sign-in: authorization code with PKCE, client metadata documents, registration for older hosts, and tokens that work only for this address.

1. Click **Start Accepting**.
2. Add the address in the web assistant and sign in.
3. The sign-in page in your browser shows a six-digit code. PadForge's question card shows **Allow … to work with PadForge from the web?** with a code of its own.
4. Click **Allow** only when the two codes match.

Outside the five minutes a sign-in is refused, and nothing asks you. A sign-in page lasts three minutes. A web assistant is named by the address its sign-in returns to, never by the name it gives, which the card shows only as **It calls itself …**. A sign-in that returns to a program on this PC carries a warning, since any program here could claim that address.

PadForge keeps up to eight web assistants. Their tokens are kept in PadForge.xml as SHA-256 hashes, never the tokens themselves. An access token lasts an hour, and a refresh token 30 days, replaced on every use. **Revoke** removes the assistant and closes its connection at once. While a web assistant is connected, the [connected mark](automation.md#the-connected-mark) shows a globe.

---

## Related pages

- [Automation](automation.md): the level, consent and Activity.
- [Command Line](../reference/command-line.md): `--mcp`, `--web`, `--tools` and `--call`.

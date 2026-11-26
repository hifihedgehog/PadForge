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

**Add** writes PadForge's entry into the program's settings and puts PadForge's skill where that program reads skills. Where the maker gives the program its own add command, PadForge runs that command. Otherwise it edits the program's settings file, under its own key. For a packaged app, the settings file it reads is the one written: its own copy in its package's data folder once that exists, else the documented file. Everything is written with your own rights, as you, even while PadForge runs as administrator. **Add** is also your consent for that assistant, for the program its row found, an npm-installed one included: it is allowed at **Change Settings**, capped by the level on the page, and no question card asks. **Remove** takes out the entry and the skill and forgets the consent. Some programs read one place: GitHub Copilot CLI, VS Code and Visual Studio read one skills folder, GitHub Copilot CLI and VS Code one settings file, and Codex and Zed one skills folder. When another program found here still has PadForge added and reads the same place, that place stays, and the row names the program. Remove on that program as well takes it out.

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
| **Tunnel** | **None**, each tunnel program found on this PC, **ChatGPT Secure MCP Tunnel**, or **An Address I Paste**. Picking one starts it, with your rights, inside the helper's job. Picking another, or turning the switch off, stops it. |
| **Address** | The public address to give the assistant, ending in `/mcp`, with **Copy** and a button that opens one web assistant's own add page with the address filled in. |
| **Accept a New Web Assistant** | **Start Accepting** opens the door for five minutes: **Accepting until 21:40.** **Stop Accepting** closes it. One allowed sign-in closes it too. |
| **Allowed on the Web** | Each web assistant you allowed, with where its sign-in returned, when it was allowed and last used, its **Reach** and **Revoke**. **Reach** holds it to the devices you pick, as [a program's reach](automation.md#a-programs-reach) does. |

PadForge finds four tunnel programs, on the PATH or in their usual install folders, and downloads none of them:

| Program | Keeps its address |
|---|---|
| cloudflared | No. A quick tunnel takes a new address each run. |
| devtunnel | No. |
| ngrok | Yes. |
| Tailscale Funnel | Yes. |

When the address changes, the assistant has to be added again with the new one. To use a tunnel that keeps its address, such as a named Cloudflare tunnel or a persistent dev tunnel, run it yourself to `http://127.0.0.1:<port>` (the card gives the port), pick **An Address I Paste** and paste its https address.

### ChatGPT Secure MCP Tunnel

**ChatGPT Secure MCP Tunnel** in the **Tunnel** list connects ChatGPT through OpenAI's own tunnel program, tunnel-client, with no public address and no sign-in here. PadForge uses a copy on the PATH, or the one it downloaded before. Otherwise it downloads the latest Windows release from OpenAI's tunnel-client repository on GitHub into its protected folder, and uses it only when its SHA-256 matches the release's own list of sums.

While it is picked, two boxes take the place of the address and the sign-in rows:

| Box | What it takes |
|---|---|
| **Runtime Key** | An OpenAI API key, masked. **Make One** opens OpenAI's API keys page. Make it **Restricted, with Tunnels Read and Use.** |
| **Tunnel ID** | The tunnel's ID. **Make One** opens OpenAI's tunnel management. |

With both filled, the tunnel starts with your rights and runs `PadForge.exe --mcp`. The line under **Tunnel** says when OpenAI reports the tunnel ready. Under the ID, **Then add this tunnel in ChatGPT.** sits beside **Open ChatGPT**. The tunnel starts again each time PadForge starts.

The web helper encrypts the key for your Windows account, and PadForge.xml keeps only the encrypted copy. An empty box forgets it. tunnel-client gets the key and the ID in its environment, never on its command line. Picking the tunnel is your consent for the copy of PadForge it starts, and ChatGPT works at the level on the page, from your OpenAI organization alone.

### Signing in

The helper runs its own OAuth 2.1 sign-in: authorization code with PKCE, client metadata documents, registration for older hosts, and tokens that work only for this address.

1. Click **Start Accepting**.
2. Add the address in the web assistant and sign in.
3. The sign-in page in your browser shows a six-digit code. PadForge's question card shows **Allow … to work with PadForge from the web?** with a code of its own.
4. Click **Allow** only when the two codes match.

Outside the five minutes a sign-in is refused, and nothing asks you. A sign-in page lasts three minutes. A web assistant is named by the address its sign-in returns to, never by the name it gives, which the card shows only as **It calls itself …**. A sign-in that returns to a program on this PC carries a warning, since any program here could claim that address.

PadForge keeps up to eight web assistants. Their tokens are kept in PadForge.xml as SHA-256 hashes, never the tokens themselves. An access token lasts an hour, and a refresh token 30 days, replaced on every use. **Revoke** removes the assistant and closes its connection at once. While a web assistant is connected, the [connected mark](automation.md#the-connected-mark) shows a globe.

---

## Assistants on this network

An assistant on another PC in your home reaches PadForge over your network with a key you make here, with no tunnel program and no outside account.

### Turn it on

Under the Assistants card's **More**, turn on **Reach PadForge From This Network**. PadForge asks first:

> **Reach PadForge from this network?** PadForge’s helper, which runs without administrator rights, listens on this PC’s network addresses, and PadForge lets its port in on private networks only. An assistant on another PC gets in only with a key you make here. Calls and their key cross the network unencrypted.

**Turn On** starts the same helper as the web route, `PadForge.exe --web`, with your ordinary rights. It listens at a port PadForge picks once, from 1024 to 49151, outside the ranges Windows reserves and away from ports other programs use. With its own rights, PadForge reserves that address for your Windows account alone and adds a firewall rule, **PadForge Assistants on This Network**, that lets the port in on private networks only. Both go when you turn the row off or change the port. While Automation is off, the helper waits, and the row says so.

| Row | What it shows |
|---|---|
| **Address** | `http://<address>:<port>/mcp` for each private address of this PC, each with **Copy**. |
| **Port** | The port, with **Change…**, which takes another from 1024 to 49151. Each assistant's saved address names the port, so add each one again after a change. |

Under the port: **Calls and their key cross this network unencrypted. Windows lets them in on private networks only.** When another program holds the port, the row says **Port … is in use by another program.** PadForge never moves to another port by itself, since the reservation, the rule and every assistant's saved address name it.

### Add a network assistant

**Add a Network Assistant…** asks for a name, such as the other PC's, and a level up to the one on the page. **Make Key** makes its key and shows it once. PadForge keeps only the key's SHA-256 hash, so copy it then. **For** picks what to paste:

| For | What to paste |
|---|---|
| **Claude Code** | `claude mcp add --transport http padforge http://<address>:<port>/mcp --header "Authorization: Bearer <key>"`, run in a terminal on the other PC. |
| **Any Other Host** | The address and the `Authorization: Bearer <key>` header, added as an MCP server over HTTP in that program's settings. |

Making the key is your consent, so the assistant's first call asks nothing. PadForge keeps up to eight keys. **Assistants on This Network** lists each with its level, when its key was made and last used, its **Reach** and **Revoke**. **Revoke** closes its connection and refuses its next call.

Every call from another PC needs a valid key, and must name this PC by one of its private addresses, its computer name or its `.local` name. A call from a web page is refused. Keys are the only way in on this route, and signing in stays with the web route.

An assistant on this network is named by its key's name and the address it called from, as **Office PC on this network from 192.168.1.30**, on the question card, in **Waiting for You**, in **Activity** and in the connected mark, which shows a network sign while one is connected.

---

## Answer from another device

A change that asks can be answered from a phone or another PC on the same network, so an assistant on another PC does not wait out its question while you are away from this one. The row **Answer From Another Device** shows under the Assistants card's **More** while a web assistant is allowed or a network key exists.

**Pair a Device…** opens a window with a QR code for five minutes. Windows hides that window from screen capture, so a program that reads the screen cannot pair itself, and the window shows no code when Windows will not hide it. Scan the code with the device's camera app, and its page opens already paired. On a device without a camera, open the address the window gives and type the six-digit code. A code works once. Five wrong tries close the typed code, and the QR code still works until the five minutes end.

The page is served by PadForge itself at the [Web Controller](../guides/web-controller.md)'s HTTPS address, with the Web Controller's self-signed certificate, on this network only, so the device shows the same one-time certificate warning. While the Web Controller is off, PadForge serves this page alone at that address. The page lists each question waiting, with the words the card shows and its request number, and **Allow** and **Don’t Allow**. An answer names its request and counts once: a replay, or an answer for another request, is refused. A web sign-in and a request to show you a place are answered on this PC only.

The device keeps its key in a secure cookie for 400 days, and PadForge keeps only the key's hash. PadForge keeps up to eight devices. The row lists each, with when it was paired and last answered, and **Remove**.

---

## Related pages

- [Automation](automation.md): the level, consent and Activity.
- [Command Line](../reference/command-line.md): `--mcp`, `--web`, `--tools` and `--call`.

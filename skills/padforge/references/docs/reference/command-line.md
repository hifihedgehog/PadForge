<!-- https://padforge.org/docs/reference/command-line/ as PadForge's skill carries it, from padforge.org's wiki/reference/command-line.md. -->
# Command Line

*Every switch PadForge.exe takes, what each does with and without a running copy, the exit codes, and a PowerShell function that talks to the `PadForge.Control` pipe.*

PadForge runs as one copy. Starting PadForge.exe again with switches hands them to the running copy through the `PadForge.Control` pipe, prints the answer and exits, with no window. That hand-over runs with your ordinary rights, so a launcher gets no UAC prompt while PadForge runs. With no copy running, a switch that can start PadForge starts it, and Windows asks for its rights. Started again with no switch, PadForge shows its window.

A switch that reaches the running copy goes through [Automation](../features/automation.md): it needs **Allow Outside Programs** on, at **Commands** or higher. The switches PadForge is started with apply to the copy they start, with no Automation needed.

---

## Writing switches

- `--name value`, `--name=value`, `/name` and `-name` all work, in any case.
- Switches run in the order given: `PadForge.exe --engine off --profile Racing --engine on`.
- A switch or value PadForge does not know stops everything with exit code 2 and the help text.
- Some switches run alone: `--mcp`, `--web`, `--settings-folder`, `--install`, `--tools`, `--call`, `--skill`, `--describe`, `--plugin-kit` and `--summarize`. Given with another switch, the line is refused with exit code 2. `--json` and `--wait` go with anything.

## The switches

| Switch | What it does | With no copy running |
|---|---|---|
| `--profile <name or id>` | Switches to the profile and holds it, so the foreground watcher leaves it alone until the program releases it or you switch yourself. | Starts PadForge with it, applied once the engine runs |
| `--default-profile` | Releases the hold and goes back to Default. | Starts PadForge |
| `--engine on`, `--engine off` | Starts or stops the input engine. | Starts PadForge, with the engine running or not |
| `--controllers on`, `--controllers off` | Turns every virtual controller on or off. | Starts PadForge that way |
| `--slot <1-16> on`, `--slot <1-16> off` | Turns one virtual controller on or off. | Starts PadForge that way |
| `--minimized`, `--maximized`, `--tray` | Minimizes, maximizes or hides the window to the tray. | Starts PadForge that way |
| `--show` | Shows the window. Works with Automation off. | Starts PadForge |
| `--hide` | Hides the window to the tray. | Exit code 4 |
| `--exit` | Closes PadForge. | Exit code 4 |
| `--state` | Prints one line of JSON: version, engine, profile, window, Automation, recording and each virtual controller. | Exit code 4 |
| `--snapshot` | Takes a diagnostics snapshot, which waits on Home for your review. | Exit code 4 |
| `--record start`, `--record stop`, `--record mark [words]` | Starts, stops or marks a [problem recording](../features/problem-recorder.md). A recording a program stops waits on Home for your review. | `--record start` starts PadForge recording. The others give 4 |
| `--tools [--json]` | Lists the assistant tools with their levels. | Exit code 4 |
| `--call <tool> [json] [--json]` | Calls one assistant tool, through the same gates, and waits on the question card when it needs consent. | Exit code 4 |
| `--skill [name]` | Prints PadForge's skill for assistants, or one of its files. | The same |
| `--describe <folder>` | Writes the description of every page, as Markdown and JSON. | The same |
| `--plugin-kit <folder>` | Writes the [plugin kit](../features/plugins.md#the-plugin-kit). | The same |
| `--summarize <file>` | Prints a problem recording's summary. | The same |
| `--settings-folder` | Opens Explorer with PadForge.xml selected. | The same |
| `--install <what>` | Installs for you, as you. See below. | The same |
| `--mcp` | Serves an assistant over standard input and output. See [Assistants](../features/assistants.md#how-it-starts). | Runs, and starts PadForge when a call needs it and Automation is on |
| `--web` | The web helper, which PadForge starts itself while **Reach PadForge From the Web** is on. PadForge refuses one it did not start. | Exit code 4 |
| `--wait` | Waits until PadForge exits and returns its exit code. | The same |
| `--json` | Gives refusals as one line of JSON on standard error, and `--tools` and `--call` output as JSON. | The same |
| `--version`, `--help` | Prints the version, or this list. `-h` and `/?` also print it. | The same |

A line that holds a switch giving 4 with no copy running gives 4 as a whole.

### `--install`

`--install` writes with your own rights and folders, Documents moved to OneDrive included, even from a prompt with administrator rights. It writes only its own folder and removes files a newer copy no longer has.

| What | Where |
|---|---|
| `--install autohotkey [folder or file]` | `Documents\AutoHotkey\Lib`, so `#Include <PadForge>` works. |
| `--install powershell [folder or file]` | Both PowerShell module folders, so `Import-Module PadForge` works. |
| `--install python [folder or file]` | The user site of each Python found. |
| `--install assistant <program>` | PadForge's entry in an assistant program's settings. |
| `--install skill <program or folder>` | PadForge's skill, in the program's skills folder or a folder you name. |

Add `--remove` to take it out, or `--check` to ask: exit code 0 and **current**, 6 and **older**, or 7 and **not installed**.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Done. |
| 1 | PadForge answered with an error, gave no answer, or the step failed. |
| 2 | A switch or value PadForge does not know, an unknown tool, or arguments it cannot read. |
| 4 | PadForge is not running, or another program holds its pipe. |
| 5 | Automation is off, or its level is too low for this. |
| 6 | `--install … --check`: the installed copy is older. |
| 7 | `--install … --check`: nothing is installed. |

With `--wait`, the code is the running copy's own when it exits.

---

## The `PadForge.Control` pipe

A launcher can skip PadForge.exe and talk to the pipe directly. The pipe serves while Automation is on. Each connection takes one UTF-8 line ending in a newline and answers with one line. A line past 1,024 bytes is cut there. Verbs ignore case and never translate.

| Line | Answer |
|---|---|
| `activate <name or id>` | `ok <name>`, or `error unknown-profile` |
| `deactivate` | `ok default` |
| `query` | `ok <name> pinned` or `ok <name> unpinned`, with `default` when no profile is active |
| `state` | `ok` and one line of JSON |
| `version` | `ok <version> r<build> <commit>` |
| `engine on`, `engine off` | `ok on`, `ok off` |
| `controllers on`, `controllers off` | `ok on`, `ok off` |
| `slot <1-16> on`, `slot <1-16> off` | `ok <n> on`, `ok <n> off` |
| `show`, `hide`, `minimize`, `maximize` | `ok` |
| `exit` | `ok`, then PadForge closes |
| `snapshot` | `ok waiting-for-review`: the snapshot waits on Home for your review. A program may take one a minute, 20 a run, else `error too-soon` or `error limit-reached` |
| `record start`, `record stop`, `record mark [words]` | `ok recording` after a start, or after a mark while recording, `ok waiting-for-review` after a stop, or `error busy`. A program may start one recording every ten seconds |

Other answers: `error unknown-command`, `error bad-argument`, `error empty` for a blank line, `error not-allowed` when the level is too low, `error refused` for a program outside your Windows session or below medium integrity, and `error internal`. PadForge answers every connection, a blank line included.

### Check the pipe is PadForge's

PadForge creates its pipes first, with Administrators as their owner (the system, when PadForge runs as the system). A client should send nothing to a pipe of that name with any other owner, since another program could have taken the name while PadForge was closed. PadForge's own clients and script libraries make this check.

### A PowerShell function

This function makes the check, sends one line and returns the answer. It needs no administrator rights.

```powershell
function Send-PadForge {
    param(
        [Parameter(Mandatory)][string]$Line,
        [string]$Pipe = 'PadForge.Control',
        [int]$TimeoutMs = 2000
    )
    $client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $Pipe,
        [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::None,
        [System.Security.Principal.TokenImpersonationLevel]::Identification)
    try {
        $client.Connect($TimeoutMs)
        # Send nothing unless PadForge made the pipe: its owner is
        # Administrators, or the system when PadForge runs as the system.
        $security = if ($PSVersionTable.PSVersion.Major -ge 6) {
            [System.IO.Pipes.PipesAclExtensions]::GetAccessControl($client)
        } else {
            $client.GetAccessControl()
        }
        $owner = $security.GetOwner([System.Security.Principal.SecurityIdentifier]).Value
        if ($owner -ne 'S-1-5-32-544' -and $owner -ne 'S-1-5-18') {
            throw "Another program holds the pipe $Pipe. Nothing was sent."
        }
        $utf8 = New-Object System.Text.UTF8Encoding($false)
        $writer = New-Object System.IO.StreamWriter($client, $utf8)
        $writer.NewLine = "`n"
        $writer.WriteLine($Line)
        $writer.Flush()
        (New-Object System.IO.StreamReader($client, $utf8)).ReadLine()
    }
    finally {
        $client.Dispose()
    }
}
```

```powershell
Send-PadForge 'activate Racing'    # ok Racing
Send-PadForge 'deactivate'         # ok default
Send-PadForge 'state' | ForEach-Object { $_.Substring(3) | ConvertFrom-Json }
```

`Connect` throws a timeout when the pipe is not there: PadForge is closed, or Automation is off.

For a launcher such as Playnite or LaunchBox, put the function and one call in the before-start and after-exit script actions, or run `PadForge.exe --profile "Racing"` and `PadForge.exe --default-profile`, which do the same and exit with a code. [Profiles](../guides/profiles.md) has the launcher steps.

See also: [External Control Internals](https://padforge.org/docs/reference/external-control-internals/), [Automation](../features/automation.md).

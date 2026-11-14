<#
.SYNOPSIS
  Runtime screen-reader audit of the running PadForge.

.DESCRIPTION
  Attaches to the PadForge window and selects each sidebar page by its
  AutomationId (Nav_Dashboard, Nav_Devices, Nav_Profiles, Nav_Extras,
  Nav_Pad<N>, Nav_Settings, Nav_About). On Extras it opens each extra's page
  through its tile and goes back. On Settings it selects each group, and on a
  controller's page each of the six sections with its parts (Picture and
  List, Macros and Menus). It opens Add a Controller and Search, which sit
  inside the window, and closes each again. In every view it opens each
  ComboBox once so its items exist, opens the fold cards and More rows to
  read what they hold, then walks the UI Automation control view and reports:
    FOCUSABLE_NO_NAME  a keyboard-focusable element with an empty name
    GLYPH_NAME         a name holding a private-use character, U+E000 to U+F8FF
    TYPE_NAME          a name shaped like a .NET type name (PadForge.ViewModels.X),
                       text and links left out, since a credit such as
                       Nefarius.Utilities.DeviceManagement is shown text
    INPUT_NO_NAME      an Edit, ComboBox or Slider without a name
    ITEM_NO_NAME       a Button, ListItem or DataItem without a name
    METER_NO_NAME      a ProgressBar without a name
    SAME_NAME          two focusable elements under one parent with the same
                       name and control type, which a screen reader cannot
                       tell apart. Items of a list are left out: a list reads
                       each item with its place, and two devices of one model
                       are two rows that read alike
    ITEM_EMPTY         an item of a plain list that shows on screen and holds
                       nothing: its content never reached UI Automation
                       (PadForge.Common.ListItemPeers)
  It covers the UI built in C# (the sidebar cards, the Add Controller entry),
  which the static XAML audit in AccessibilityXamlTests cannot see.

  With -Outline it also writes each view as a screen reader meets it, with
  the folds open, into outline-<stamp>\<view>.txt: every element of the
  control view in reading order with its type, its name, its help text and
  the groups and items it sits in. F marks an element that takes keyboard
  focus. A combo box's items and the parts of a slider or a scroll bar are
  left out.

  One pass per verification point. The pass waits on conditions, never on
  fixed pauses: a click waits until UIA shows the new page or tab content, a
  combo box closes once its items exist, and each view is one cached UIA call
  walked in compiled C#. After the first view only the visible page is
  read, since the sidebar does not change.

  Run it elevated. PadForge runs as administrator, and UI Automation does not
  reach into an elevated process from a lower one. Start PadForge first with
  the deploy recipe, since a PadForge started from inside an elevated script
  exits when that script's process tree ends. The window is brought forward
  only when another window is in front, restored only when minimized and
  never resized, because PadForge saves its size and state. Pages, sections,
  parts and groups are selected through their SelectionItem pattern, and
  tiles and the two in-window dialogs through Invoke, as a screen reader
  works them. A click at an element's center is only the fallback for one
  that offers no pattern. Fold cards and More rows are opened and then closed
  again, last first, so the fold state PadForge stores ends as it began.
  Nothing is typed and no value is changed, so no setting changes.

  Reports go to OutDir (default %LOCALAPPDATA%\PadForgeA11y), never beside
  PadForge.exe. Exit code: the number of findings, -1 when PadForge is not
  running, -2 on an error, which the report names with its line.

.EXAMPLE
  Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','tools\a11y_uia_audit.ps1','-Axe'
#>
param(
    [string]$OutDir = (Join-Path $env:LOCALAPPDATA 'PadForgeA11y'),
    # Page tags to audit (Dashboard, Devices, Profiles, Extras, Pad1, Settings,
    # About). Default: every page in the sidebar.
    [string[]]$Pages,
    # Leave out each extra's page, the Settings groups and the controller
    # page's sections: the pages as they open only.
    [switch]$NoTabs,
    [switch]$NoCombos,
    # Leave the fold cards and More rows as they are.
    [switch]$NoFolds,
    # Leave out Add a Controller and Search.
    [switch]$NoDialogs,
    # Also write each view in reading order (see the description).
    [switch]$Outline,
    # Also scan each page and tab with the Axe.Windows CLI, built from
    # microsoft/axe-windows src/CLI. Its exit code is 0 for no errors, 1 for
    # errors and 2 for a failed scan (ReturnValueChooser.cs).
    [switch]$Axe,
    # Also scan each extra's page, Settings group and controller section with
    # Axe. Each scan reads the whole process and takes about 20 s, so by
    # default Axe covers the window and the pages, and the UIA walk alone
    # covers the rest.
    [switch]$AxeTabs,
    [string]$AxeCli = (Join-Path $env:LOCALAPPDATA 'PadForgeStudio\tooling\axe-cli\AxeWindowsCLI.exe')
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$txt = Join-Path $OutDir "uia-audit-$stamp.txt"
$json = Join-Path $OutDir "uia-audit-$stamp.json"
$lines = New-Object System.Collections.Generic.List[string]
function Note([string]$m) { $lines.Add($m); Write-Host $m }
function Save { [System.IO.File]::WriteAllLines($txt, $lines, (New-Object System.Text.UTF8Encoding($true))) }
# The script runs in an elevated window that closes on exit, so an error goes
# into the report, with its line.
trap { Note "ERROR at line $($_.InvocationInfo.ScriptLineNumber): $($_.Exception.Message)"; Save; exit -2 }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class A11yWin {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr h);
}
'@
# The rules and the walk run here rather than in a PowerShell loop over every
# element. C# 5, the language Add-Type compiles in Windows PowerShell 5.1.
Add-Type -ReferencedAssemblies UIAutomationClient, UIAutomationTypes, WindowsBase @'
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows.Automation;

public sealed class A11yFinding {
    public string Page; public string Rule; public string Type; public string Name; public string Aid;
    public string Class; public string Help; public bool Offscreen; public string Path; public string Rid;
}

public sealed class A11yView {
    public int Count;
    public List<A11yFinding> Findings = new List<A11yFinding>();
    public List<AutomationElement> Combos = new List<AutomationElement>();
    public List<AutomationElement> NavItems = new List<AutomationElement>();
    // The radio buttons a page switches its content with: a controller's
    // sections and parts, the Settings groups.
    public List<AutomationElement> Choices = new List<AutomationElement>();
    // The Extras catalog's tiles.
    public List<AutomationElement> Tiles = new List<AutomationElement>();
    // The fold cards and More rows that are closed.
    public List<AutomationElement> Folds = new List<AutomationElement>();
    // The view in reading order, one element a line.
    public List<string> Outline = new List<string>();
}

public static class A11yWalk {
    static readonly Regex TypeName = new Regex(@"^[A-Za-z_]\w*(\.[A-Za-z_]\w*)+$");
    static readonly Regex ChoiceId = new Regex(@"^Section_\w+$|^Settings_Group_\w+$|^TabController$|^MappingsTab$|^MacrosTab$|^MenusTab$");

    sealed class Item {
        public AutomationElement El; public string Path; public string ParentAid;
        // The groups and items above the element, and whether it is a part
        // of a combo box, a slider or a scroll bar, which the outline skips.
        public string Groups; public bool Part;
        public Item(AutomationElement el, string path, string parentAid, string groups, bool part) {
            El = el; Path = path; ParentAid = parentAid; Groups = groups; Part = part;
        }
    }

    static string Short(string s, int max) {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ");
        return s.Length <= max ? s : s.Substring(0, max) + "...";
    }

    static bool HasGlyph(string s) {
        foreach (char ch in s) if (ch >= '\uE000' && ch <= '\uF8FF') return true;
        return false;
    }

    static bool Selects(AutomationElement el) {
        object can = el.GetCachedPropertyValue(AutomationElement.IsSelectionItemPatternAvailableProperty, true);
        return can is bool && (bool)can;
    }

    static bool IsClosedFold(AutomationElement el) {
        object can = el.GetCachedPropertyValue(AutomationElement.IsExpandCollapsePatternAvailableProperty, true);
        if (!(can is bool) || !(bool)can) return false;
        object state = el.GetCachedPropertyValue(ExpandCollapsePattern.ExpandCollapseStateProperty, true);
        return state is ExpandCollapseState && (ExpandCollapseState)state == ExpandCollapseState.Collapsed;
    }

    public static CacheRequest Request() {
        var cr = new CacheRequest();
        cr.Add(AutomationElement.NameProperty);
        cr.Add(AutomationElement.ControlTypeProperty);
        cr.Add(AutomationElement.AutomationIdProperty);
        cr.Add(AutomationElement.ClassNameProperty);
        cr.Add(AutomationElement.IsKeyboardFocusableProperty);
        cr.Add(AutomationElement.IsOffscreenProperty);
        cr.Add(AutomationElement.IsEnabledProperty);
        cr.Add(AutomationElement.HelpTextProperty);
        cr.Add(AutomationElement.RuntimeIdProperty);
        cr.Add(AutomationElement.IsExpandCollapsePatternAvailableProperty);
        cr.Add(ExpandCollapsePattern.ExpandCollapseStateProperty);
        cr.Add(AutomationElement.IsSelectionItemPatternAvailableProperty);
        cr.Add(AutomationElement.BoundingRectangleProperty);
        cr.TreeScope = TreeScope.Subtree;
        cr.TreeFilter = Automation.ControlViewCondition;
        cr.AutomationElementMode = AutomationElementMode.Full;
        return cr;
    }

    public static A11yView Walk(AutomationElement live, string page) {
        AutomationElement root = live.GetUpdatedCache(Request());
        var v = new A11yView();
        var stack = new Stack<Item>();
        stack.Push(new Item(root, "", "", "", false));
        while (stack.Count > 0) {
            Item it = stack.Pop();
            AutomationElement el = it.El;
            v.Count++;
            AutomationElement.AutomationElementInformation c = el.Cached;
            string name = c.Name ?? "";
            string type = c.ControlType == null ? "" : c.ControlType.ProgrammaticName.Replace("ControlType.", "");
            string aid = c.AutomationId ?? "";
            bool empty = string.IsNullOrWhiteSpace(name);

            var rules = new List<string>();
            if (c.IsKeyboardFocusable && empty) rules.Add("FOCUSABLE_NO_NAME");
            if (!empty && HasGlyph(name)) rules.Add("GLYPH_NAME");
            // Text and links show their own words, and a dotted name there is
            // a credit (Nefarius.Utilities.DeviceManagement), not a type.
            if (!empty && type != "Text" && type != "Hyperlink" && TypeName.IsMatch(name)) rules.Add("TYPE_NAME");
            if (empty && (type == "Edit" || type == "ComboBox" || type == "Slider")) rules.Add("INPUT_NO_NAME");
            if (empty && (type == "Button" || type == "ListItem" || type == "DataItem")) rules.Add("ITEM_NO_NAME");
            if (empty && type == "ProgressBar" && !c.IsOffscreen) rules.Add("METER_NO_NAME");
            AutomationElementCollection kids = el.CachedChildren;
            // A plain list's item that shows and holds nothing: a card is
            // never empty, so its content did not reach UI Automation.
            if (type == "DataItem" && (c.ClassName ?? "") == "ItemsControlItem" && !c.IsOffscreen
                && (kids == null || kids.Count == 0) && !c.BoundingRectangle.IsEmpty
                && c.BoundingRectangle.Width > 40 && c.BoundingRectangle.Height > 20) rules.Add("ITEM_EMPTY");
            if (rules.Count > 0) {
                int[] rid = (int[])el.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty);
                string ridText = rid == null ? "" : string.Join(".", rid);
                foreach (string r in rules) {
                    var f = new A11yFinding();
                    f.Page = page; f.Rule = r; f.Type = type; f.Name = name; f.Aid = aid; f.Class = c.ClassName ?? "";
                    f.Help = c.HelpText ?? ""; f.Offscreen = c.IsOffscreen; f.Path = it.Path; f.Rid = ridText;
                    v.Findings.Add(f);
                }
            }

            if (type == "ComboBox" && c.IsEnabled && !c.IsOffscreen) v.Combos.Add(el);
            // A sidebar entry is a ListItem of NavItemsControl. It was a
            // DataItem while wpf-ui's navigation view drew the sidebar.
            // An item of the sidebar that selects nothing is no page: the
            // word Controllers over the slots is an item of the list too.
            if ((type == "ListItem" || type == "DataItem") && aid.StartsWith("Nav_", StringComparison.Ordinal) && Selects(el)) v.NavItems.Add(el);
            if (type == "RadioButton" && ChoiceId.IsMatch(aid) && c.IsEnabled) v.Choices.Add(el);
            if (type == "Button" && aid.StartsWith("Extras_Open_", StringComparison.Ordinal)) v.Tiles.Add(el);
            // A fold card and a More row are Expanders, which read as groups
            // that expand. A group box is a group too, and expands nothing.
            if (type == "Group" && c.IsEnabled && IsClosedFold(el)) v.Folds.Add(el);

            string label = !empty ? name : (aid.Length > 0 ? "#" + aid : (c.ClassName ?? ""));
            string childPath = it.Path.Length > 0 ? it.Path + " > " + label : label;

            // The outline: groups and panes name where an element sits, and
            // every other element is a line.
            bool holder = type == "Group" || type == "Pane" || type == "Window";
            if (!it.Part && !holder) {
                string state = (c.IsEnabled ? "" : " (disabled)") + (c.IsOffscreen ? " (off screen)" : "");
                string help = !string.IsNullOrEmpty(c.HelpText) && c.HelpText != name ? "  help='" + Short(c.HelpText, 70) + "'" : "";
                v.Outline.Add(string.Format("{0} {1,-11} '{2}'{3}{4}   [{5}]", c.IsKeyboardFocusable ? "F" : "-", type, Short(name, 110), help, state, it.Groups));
            }
            string groups = it.Groups;
            if ((holder || type == "ListItem" || type == "DataItem" || type == "TabItem" || type == "TreeItem") && (!empty || aid.Length > 0)) {
                string step = !empty ? Short(name, 40) : "#" + aid;
                groups = groups.Length > 0 ? groups + " > " + step : step;
            }
            bool part = it.Part || type == "ComboBox" || type == "Slider" || type == "ScrollBar";

            if (kids == null) continue;
            // Two focusable elements of one parent that read alike. An
            // item of a list reads with its place in the list.
            var alike = new Dictionary<string, int>();
            foreach (AutomationElement kid in kids) {
                AutomationElement.AutomationElementInformation k = kid.Cached;
                if (!k.IsKeyboardFocusable || k.IsOffscreen || string.IsNullOrWhiteSpace(k.Name) || k.ControlType == null) continue;
                if (k.ControlType == ControlType.ListItem || k.ControlType == ControlType.DataItem || k.ControlType == ControlType.TreeItem || k.ControlType == ControlType.TabItem) continue;
                string key = k.ControlType.ProgrammaticName + "|" + k.Name;
                int n; alike.TryGetValue(key, out n); alike[key] = n + 1;
            }
            foreach (AutomationElement kid in kids) {
                AutomationElement.AutomationElementInformation k = kid.Cached;
                if (!k.IsKeyboardFocusable || k.IsOffscreen || string.IsNullOrWhiteSpace(k.Name) || k.ControlType == null) continue;
                int n;
                if (!alike.TryGetValue(k.ControlType.ProgrammaticName + "|" + k.Name, out n) || n < 2) continue;
                int[] rid = (int[])kid.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty);
                var f = new A11yFinding();
                f.Page = page; f.Rule = "SAME_NAME"; f.Type = k.ControlType.ProgrammaticName.Replace("ControlType.", ""); f.Name = k.Name;
                f.Aid = k.AutomationId ?? ""; f.Class = k.ClassName ?? ""; f.Help = k.HelpText ?? ""; f.Offscreen = false;
                f.Path = childPath; f.Rid = rid == null ? "" : string.Join(".", rid);
                v.Findings.Add(f);
            }
            for (int i = kids.Count - 1; i >= 0; i--) stack.Push(new Item(kids[i], childPath, aid, groups, part));
        }
        return v;
    }
}
'@

$AE = [System.Windows.Automation.AutomationElement]
$Walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$SIP = [System.Windows.Automation.SelectionItemPattern]::Pattern
$ECP = [System.Windows.Automation.ExpandCollapsePattern]::Pattern
$INV = [System.Windows.Automation.InvokePattern]::Pattern

$pf = Get-Process PadForge -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $pf) { Note 'PadForge is not running. Start it with the deploy recipe first.'; Save; exit -1 }
# The main window is the largest of PadForge's top-level windows.
# Process.MainWindowHandle is the first visible unowned window in Z order,
# which can be a topmost overlay with three elements.
$win = $null
$area = 0
$own = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $pf.Id)
foreach ($top in $AE::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $own)) {
    $r = $top.Current.BoundingRectangle
    if (-not $r.IsEmpty -and $r.Width * $r.Height -gt $area) { $area = $r.Width * $r.Height; $win = $top }
}
if (-not $win) { $win = $AE::FromHandle($pf.MainWindowHandle) }
$hwnd = [IntPtr]$win.Current.NativeWindowHandle
$clock = [Diagnostics.Stopwatch]::StartNew()
Note "PadForge pid=$($pf.Id) hwnd=$hwnd started=$($pf.StartTime.ToString('o'))"
# A modal dialog disables the main window, and a click on a disabled window
# does nothing, so every page would time out. Name the cause and stop.
if (-not [A11yWin]::IsWindowEnabled($hwnd)) { Note 'A dialog is open over the PadForge window. Close it, then run the audit again.'; Save; exit -1 }

# The Alt tap and the attached input queue let SetForegroundWindow past the
# foreground lock (tooling memory, 2026-10-06). Skipped when PadForge is
# already in front. Restore only when minimized: SW_RESTORE would un-maximize.
function Front {
    if ([A11yWin]::GetForegroundWindow() -eq $hwnd) { return $true }
    for ($i = 0; $i -lt 5; $i++) {
        if ([A11yWin]::IsIconic($hwnd)) { [A11yWin]::ShowWindow($hwnd, 9) | Out-Null }
        [A11yWin]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
        [A11yWin]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
        $fg = [A11yWin]::GetWindowThreadProcessId([A11yWin]::GetForegroundWindow(), [IntPtr]::Zero)
        $me = [A11yWin]::GetCurrentThreadId()
        [A11yWin]::AttachThreadInput($me, $fg, $true) | Out-Null
        [A11yWin]::BringWindowToTop($hwnd) | Out-Null
        [A11yWin]::SetForegroundWindow($hwnd) | Out-Null
        [A11yWin]::AttachThreadInput($me, $fg, $false) | Out-Null
        for ($t = 0; $t -lt 16; $t++) {
            if ([A11yWin]::GetForegroundWindow() -eq $hwnd) { return $true }
            Start-Sleep -Milliseconds 25
        }
    }
    return $false
}

# Clicks an element's center. Returns $true, or why it could not: 'no
# rectangle' for an element that is gone or has no size, 'not in front' when
# the window would not come forward.
function Click-Center($el) {
    try { $r = $el.Current.BoundingRectangle } catch { return 'no rectangle' }
    if ($r.Width -le 0 -or $r.Height -le 0) { return 'no rectangle' }
    if (-not (Front)) { return 'not in front' }
    [A11yWin]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) | Out-Null
    [A11yWin]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    [A11yWin]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    return $true
}

# The child of a parent with the given AutomationId, read live, or null. The
# pages sit in one host Grid, one visible at a time, and the Grid has no
# peer, so the visible page is a child of the window (DashboardPageView,
# PadPageView and so on, MainWindow.xaml).
function Find-Child($parent, [string]$aid) {
    try {
        $ch = $Walker.GetFirstChild($parent)
        while ($ch) {
            if ($ch.Current.AutomationId -eq $aid) { return $ch }
            $ch = $Walker.GetNextSibling($ch)
        }
    } catch { }
    return $null
}

# Polls every 30 ms until the condition holds, then allows 150 ms for the
# new content's bindings to fill. Returns the milliseconds waited, or -1 on
# timeout, after which the pass carries on with whatever is shown.
function Wait-For([scriptblock]$cond, [int]$timeoutMs = 4000) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        if (& $cond) { Start-Sleep -Milliseconds 150; return [int]$sw.ElapsedMilliseconds }
        Start-Sleep -Milliseconds 30
    }
    return -1
}

# Opens each combo box until its items exist (a closed WPF ComboBox lists no
# items, since its peer reads them through an items host that is realized
# only when the drop-down opens), then closes it.
function Open-Combos($list) {
    $n = 0
    foreach ($cb in $list) {
        try {
            $p = $cb.GetCurrentPattern($ECP)
            $p.Expand()
            # An item is named as it first takes a size, a moment after it
            # exists, so the wait is for the first item's name.
            $sw = [Diagnostics.Stopwatch]::StartNew()
            while ($sw.ElapsedMilliseconds -lt 600) {
                $item = $Walker.GetFirstChild($cb)
                if ($item -and $item.Current.Name) { break }
                Start-Sleep -Milliseconds 15
            }
            $p.Collapse()
            $n++
        } catch { }
    }
    return $n
}

$seen = @{}
$findings = New-Object System.Collections.Generic.List[object]
# Audits the view under $root: opens its combo boxes, walks it once more if
# any opened, keeps the findings not seen in an earlier view, and scans it
# with Axe when asked. Returns the walk, whose lists name the next targets.
function Audit-View($root, [string]$where, [bool]$scan = $true) {
    $start = $clock.ElapsedMilliseconds
    $v = [A11yWalk]::Walk($root, $where)
    $opened = 0
    if (-not $NoCombos -and $v.Combos.Count -gt 0) {
        $opened = Open-Combos $v.Combos
        if ($opened -gt 0) { $v = [A11yWalk]::Walk($root, $where) }
    }
    $new = 0
    foreach ($f in $v.Findings) {
        $k = "$($f.Rid)|$($f.Rule)"
        if ($seen.ContainsKey($k)) { continue }
        $seen[$k] = $true; $findings.Add($f); $new++
    }
    Note ("{0}: {1} elements, {2} combo boxes opened, {3} new findings, {4} ms" -f $where, $v.Count, $opened, $new, ($clock.ElapsedMilliseconds - $start))
    if ($scan) { Scan-Axe $where }
    return $v
}

# Writes a walk's outline, when -Outline asks for it.
function Write-Outline($v, [string]$where) {
    if (-not $Outline -or -not $v) { return }
    $dir = Join-Path $OutDir "outline-$stamp"
    New-Item -ItemType Directory -Force $dir | Out-Null
    $file = Join-Path $dir (($where -replace '[^\w\-]+', '_') + '.txt')
    [System.IO.File]::WriteAllLines($file, $v.Outline, (New-Object System.Text.UTF8Encoding($false)))
}

# Scans the window as it stands with the Axe.Windows CLI, when -Axe asks for
# it, and notes its errors by rule.
function Scan-Axe([string]$where) {
    if (-not $Axe) { return }
    $dir = Join-Path $OutDir "axe-$stamp"
    New-Item -ItemType Directory -Force $dir | Out-Null
    $scanId = $where -replace '[^\w\-]+', '_'
    $out = & $AxeCli --processid $pf.Id --outputdirectory $dir --scanid $scanId --verbosity verbose --alwayssavetestfile 2>&1
    $code = $LASTEXITCODE
    $out | Set-Content -Path (Join-Path $dir "$scanId.txt") -Encoding UTF8
    $summary = ($out | Where-Object { "$_" -match 'error.* found|no errors' }) -join ' '
    Note ("  axe {0}: exit {1}, {2}" -f $where, $code, $summary)
    $out | ForEach-Object { if ("$_" -match '^Error \d+: (.+)$') { $Matches[1] } } | Group-Object |
        Sort-Object Count -Descending | ForEach-Object { Note ("    {0} x {1}" -f $_.Count, $_.Name) }
}

# The first element under a root with the given AutomationId, read live, or
# null.
function Find-Under($root, [string]$aid) {
    try {
        return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $aid)))
    } catch { return $null }
}

function Is-Selected($el) {
    try { return [bool]$el.GetCurrentPattern($SIP).Current.IsSelected } catch { return $false }
}

# Selects a sidebar entry, a section, a part or a Settings group by its
# SelectionItem pattern, which the window answers as it answers a click
# (NavItemsControl's peer, and a RadioButton's Checked handler). A click at
# the element's center is the fallback. True once the element reads selected.
function Select-Item($el) {
    try {
        $p = $el.GetCurrentPattern($SIP)
        if (-not $p.Current.IsSelected) { $p.Select() }
        if ((Wait-For { Is-Selected $el } 2000) -ge 0) { return $true }
    } catch { }
    return ((Click-Center $el) -eq $true)
}

# Invokes a tile, a sidebar entry or a button by its Invoke pattern, with a
# click at its center as the fallback.
function Invoke-Item($el) {
    try { $el.GetCurrentPattern($INV).Invoke(); return $true } catch { }
    return ((Click-Center $el) -eq $true)
}

# Audits the view under $root, then what it keeps folded: fold cards and More
# rows are opened a level at a time, since a fold can hold another, and each
# level is walked. Axe scans the view once, with everything open, so it reads
# what the folds hold too. What was opened is closed again, last first, so
# the fold state PadForge stores ends as it began. Returns the last walk.
function Audit-Deep($root, [string]$where, [bool]$scan = $true) {
    if ($NoFolds) { $plain = Audit-View $root $where $scan; Write-Outline $plain $where; return $plain }
    $v = Audit-View $root $where $false
    $openedFolds = New-Object System.Collections.Generic.List[object]
    # The folds close again, last first, on every way out: an error that goes
    # to the trap and Ctrl+C both run the finally before the script ends, and
    # PadForge stores the fold state it is left with.
    try {
        for ($level = 1; $level -le 4 -and $v.Folds.Count -gt 0; $level++) {
            $n = 0
            foreach ($fold in $v.Folds) {
                try { $fold.GetCurrentPattern($ECP).Expand(); $openedFolds.Add($fold); $n++ } catch { }
            }
            if ($n -eq 0) { break }
            Start-Sleep -Milliseconds 300
            $v = Audit-View $root "$where, folds opened ($level)" $false
        }
        if ($scan) { Scan-Axe $where }
        Write-Outline $v $where
    } finally {
        for ($i = $openedFolds.Count - 1; $i -ge 0; $i--) {
            try { $openedFolds[$i].GetCurrentPattern($ECP).Collapse() } catch { }
        }
    }
    return $v
}

# Selects a sidebar page and returns its view, or null. The pages sit in one
# host, one visible at a time, so the visible page is a child of the window:
# DashboardPageView for Home, PadPageView for every controller, and
# <Tag>PageView for the rest (MainWindow.xaml).
function Open-Page([string]$tag) {
    $entry = Find-Under $win "Nav_$tag"
    if (-not $entry) { Note "Nav_${tag}: not found"; return $null }
    if (-not (Select-Item $entry)) { Note "Nav_${tag}: could not be selected"; return $null }
    $viewId = if ($tag -like 'Pad*') { 'PadPageView' } else { "${tag}PageView" }
    $script:pageEl = $null
    $waited = Wait-For { $script:pageEl = Find-Child $win $viewId; $null -ne $script:pageEl }
    if ($waited -lt 0) { Note "${tag}: $viewId did not appear within 4 s"; return $null }
    return $script:pageEl
}

# The whole window once, for the title bar, the sidebar and the page in front.
$first = Audit-View $win 'Window'
Write-Outline $first 'Window'
$tags = @($first.NavItems | ForEach-Object { $_.Cached.AutomationId.Substring(4) } | Where-Object { $_ -ne 'AddController' })
# -File passes "Devices,Pad1" as one string, so names are split on commas.
if ($Pages) { $tags = @($Pages | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
Note "pages: $($tags -join ', ')"

foreach ($tag in $tags) {
    $pageEl = Open-Page $tag
    if (-not $pageEl) { continue }

    if ($tag -eq 'Extras') {
        # The catalog, then each extra's own page through its tile, and back.
        $page = Audit-Deep $pageEl 'Extras'
        if ($NoTabs) { continue }
        $keys = @($page.Tiles | ForEach-Object { $_.Cached.AutomationId.Substring(12) })
        foreach ($key in $keys) {
            $tile = Find-Under $pageEl "Extras_Open_$key"
            if (-not $tile -or -not (Invoke-Item $tile)) { Note "Extras / ${key}: its tile would not open"; continue }
            if ((Wait-For { $null -ne (Find-Under $pageEl "Extras_Back_$key") }) -lt 0) { Note "Extras / ${key}: its page did not open within 4 s"; continue }
            Audit-Deep $pageEl "Extras / $key" $AxeTabs.IsPresent | Out-Null
            $back = Find-Under $pageEl "Extras_Back_$key"
            if ($back) { Invoke-Item $back | Out-Null }
            if ((Wait-For { $null -ne (Find-Under $pageEl "Extras_Open_$key") }) -lt 0) { Open-Page 'Extras' | Out-Null }
        }
        continue
    }

    $page = Audit-Deep $pageEl $tag
    if ($NoTabs) { continue }
    # A controller's page has six sections and Settings five groups, each a
    # radio button of the page's own switch. Two sections have parts: Picture
    # and List under Buttons, Macros and Menus under Macros & Menus.
    $choices = @($page.Choices | Where-Object { $_.Cached.AutomationId -like 'Section_*' -or $_.Cached.AutomationId -like 'Settings_Group_*' })
    if ($choices.Count -eq 0) { continue }
    $open = $choices | Where-Object { Is-Selected $_ } | Select-Object -First 1
    foreach ($choice in $choices) {
        $name = $choice.Cached.Name
        if (-not (Select-Item $choice)) { Note "$tag / ${name}: would not open"; continue }
        Start-Sleep -Milliseconds 400
        $view = Audit-Deep $pageEl "$tag / $name" $AxeTabs.IsPresent
        $parts = @($view.Choices | Where-Object { @('TabController', 'MappingsTab', 'MacrosTab', 'MenusTab') -contains $_.Cached.AutomationId })
        $openPart = $parts | Where-Object { Is-Selected $_ } | Select-Object -First 1
        foreach ($part in $parts) {
            if (Is-Selected $part) { continue }
            if (-not (Select-Item $part)) { Note "$tag / $name / $($part.Cached.Name): would not open"; continue }
            Start-Sleep -Milliseconds 400
            Audit-Deep $pageEl "$tag / $name / $($part.Cached.Name)" $false | Out-Null
        }
        if ($openPart) { Select-Item $openPart | Out-Null }
    }
    if ($open) { Select-Item $open | Out-Null }
}

# Add a Controller and Search open inside the window, and opening either
# changes no setting. Each is audited and closed by its own close button.
if (-not $NoDialogs) {
    $panels = @(
        @{ Name = 'Add a Controller'; Opener = 'Nav_AddController'; Panel = 'AddControllerDialog'; Close = 'AddController_Close' },
        @{ Name = 'Search';           Opener = 'TitleSearchBox';    Panel = 'SearchPanel';         Close = 'SearchCloseChip' }
    )
    foreach ($p in $panels) {
        $opener = Find-Under $win $p.Opener
        if (-not $opener -or -not (Invoke-Item $opener)) { Note "$($p.Name): $($p.Opener) would not open it"; continue }
        $script:panelEl = $null
        if ((Wait-For { $script:panelEl = Find-Under $win $p.Panel; $null -ne $script:panelEl }) -lt 0) { Note "$($p.Name): $($p.Panel) did not appear within 4 s"; continue }
        Write-Outline (Audit-View $script:panelEl $p.Name) $p.Name
        $close = Find-Under $win $p.Close
        if ($close) { Invoke-Item $close | Out-Null } else { Note "$($p.Name): no $($p.Close) to close it with" }
        Wait-For { $null -eq (Find-Under $win $p.Panel) } 2000 | Out-Null
    }
}

$byRule = $findings | Group-Object Rule | Sort-Object Name | ForEach-Object { "{0}={1}" -f $_.Name, $_.Count }
Note ("findings: {0} ({1}), {2:N1} s" -f $findings.Count, ($byRule -join ', '), ($clock.ElapsedMilliseconds / 1000))
foreach ($f in $findings) {
    $label = if ($f.Name) { $f.Name } else { '(no name)' }
    $help = if ($f.Help) { " help='$($f.Help)'" } else { '' }
    $lines.Add(("  [{0}] {1} {2} '{3}' aid={4} class={5}{6}" -f $f.Page, $f.Rule, $f.Type, $label, $f.Aid, $f.Class, $help))
    $lines.Add(("      at {0}" -f $f.Path))
}
$lines.Add("report: $txt")
Save
$findings | ConvertTo-Json -Depth 2 -Compress | Set-Content -Path $json -Encoding UTF8
exit $findings.Count

# vJoy Config Test via PadForge
# Modifies PadForge.xml, launches PadForge, queries vJoyInterface.dll in fresh process.
# Must run elevated (PadForge runs elevated when vJoy installed).

$logFile = 'C:\Users\sonic\GitHub\PadForge\tools\vjoy_config_test_log.txt'
$padForgeExe = 'C:\PadForge\PadForge.exe'
$padForgeXml = 'C:\PadForge\PadForge.xml'
$queryScript = 'C:\Users\sonic\GitHub\PadForge\tools\vjoy_query_device.ps1'
$toolsDir = 'C:\Users\sonic\GitHub\PadForge\tools'

$R = [System.Collections.Generic.List[string]]::new()
$R.Add("=== vJoy Config Test via PadForge $(Get-Date) ===")
$R.Add("")

# NOTE: GetVJDAxisExist always reports 8 axes because vJoy's fixed 97-byte report
# has 16 axis slots (Data or Constant). The DLL doesn't distinguish between them.
# We only verify buttons and POVs, which ARE accurately reported.

$tests = @(
    @{ Label = "Xbox 360 preset (11 btns, 1 POV)";
       Slots = @( ,@{Preset='Xbox360'; Sticks=2; Triggers=2; Buttons=11; Povs=1} ) },

    @{ Label = "Custom minimal (4 btns, 0 POV)";
       Slots = @( ,@{Preset='Custom'; Sticks=1; Triggers=0; Buttons=4; Povs=0} ) },

    @{ Label = "Custom 32 btns, 2 POVs";
       Slots = @( ,@{Preset='Custom'; Sticks=3; Triggers=2; Buttons=32; Povs=2} ) },

    @{ Label = "Custom 64 btns, 4 POVs";
       Slots = @( ,@{Preset='Custom'; Sticks=4; Triggers=0; Buttons=64; Povs=4} ) },

    @{ Label = "Custom 128 btns, 3 POVs";
       Slots = @( ,@{Preset='Custom'; Sticks=2; Triggers=2; Buttons=128; Povs=3} ) },

    @{ Label = "Two vJoy: Xbox360 + Custom 32btn";
       Slots = @( @{Preset='Xbox360'; Sticks=2; Triggers=2; Buttons=11; Povs=1},
                  @{Preset='Custom'; Sticks=1; Triggers=0; Buttons=32; Povs=2} ) },

    @{ Label = "Three vJoy: 11btn + 64btn + 4btn";
       Slots = @( @{Preset='Xbox360'; Sticks=2; Triggers=2; Buttons=11; Povs=1},
                  @{Preset='Custom'; Sticks=4; Triggers=0; Buttons=64; Povs=4},
                  @{Preset='Custom'; Sticks=1; Triggers=0; Buttons=4; Povs=0} ) }
)

function Stop-PadForge {
    $procs = Get-Process -Name PadForge -ErrorAction SilentlyContinue
    if ($procs) {
        $procs | Stop-Process -Force
        # Wait for process to fully exit
        for ($i = 0; $i -lt 10; $i++) {
            Start-Sleep -Seconds 1
            $still = Get-Process -Name PadForge -ErrorAction SilentlyContinue
            if (-not $still) { return }
        }
    }
}

function Set-PadForgeVJoyConfig([array]$slots) {
    [xml]$xml = Get-Content $padForgeXml
    $appSettings = $xml.PadForgeSettings.AppSettings

    $typeNodes = $appSettings.SlotControllerTypes.ChildNodes
    $createdNodes = $appSettings.SlotCreated.ChildNodes

    for ($i = 0; $i -lt 16; $i++) {
        if ($i -lt $slots.Count) {
            $typeNodes[$i].InnerText = '2'       # VJoy = 2
            $createdNodes[$i].InnerText = 'true'
        } else {
            $typeNodes[$i].InnerText = '0'
            $createdNodes[$i].InnerText = 'false'
        }
    }

    $configNodes = $appSettings.VJoyConfigs.SelectNodes('Config')
    for ($i = 0; $i -lt $slots.Count; $i++) {
        $s = $slots[$i]
        $configNodes[$i].SetAttribute('Preset', $s.Preset)
        $configNodes[$i].SetAttribute('ThumbstickCount', $s.Sticks.ToString())
        $configNodes[$i].SetAttribute('TriggerCount', $s.Triggers.ToString())
        $configNodes[$i].SetAttribute('ButtonCount', $s.Buttons.ToString())
        $configNodes[$i].SetAttribute('PovCount', $s.Povs.ToString())
    }

    $xml.Save($padForgeXml)
}

function Start-PadForgeAndWait([int]$expectedDevices = 1) {
    Start-Process $padForgeExe
    # Wait for PadForge to start + vJoy devices to become available
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Seconds 1
        $p = Get-Process -Name PadForge -ErrorAction SilentlyContinue
        if (-not $p) { continue }
        if ($i -ge 8) { return $true }
    }
    return (Get-Process -Name PadForge -ErrorAction SilentlyContinue) -ne $null
}

function Query-VJoyDevice([int]$deviceId, [int]$retries = 3) {
    for ($attempt = 0; $attempt -lt $retries; $attempt++) {
        $outFile = "$toolsDir\vjoy_query_result_$deviceId.txt"
        if (Test-Path $outFile) { Remove-Item $outFile -Force }

        $p = Start-Process powershell -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$queryScript`" -DeviceId $deviceId -OutFile `"$outFile`"" -Wait -PassThru -WindowStyle Hidden
        if ($p.ExitCode -ne 0 -and $attempt -lt $retries - 1) {
            Start-Sleep -Seconds 2
            continue
        }

        if (-not (Test-Path $outFile)) {
            if ($attempt -lt $retries - 1) { Start-Sleep -Seconds 2; continue }
            return $null
        }
        $line = Get-Content $outFile -Raw
        if ($line -match 'status=(\d+)\s+axes=(\d+)\s+buttons=(\d+)\s+contPovs=(\d+)') {
            $result = @{
                Status = [int]$matches[1]
                Axes = [int]$matches[2]
                Buttons = [int]$matches[3]
                ContPovs = [int]$matches[4]
            }
            # If status=3 (MISS) and we have retries left, wait and retry
            if ($result.Status -eq 3 -and $attempt -lt $retries - 1) {
                Start-Sleep -Seconds 3
                continue
            }
            return $result
        }
        if ($attempt -lt $retries - 1) { Start-Sleep -Seconds 2 }
    }
    return $null
}

# ── Run tests ──

$pass = 0
$fail = 0

foreach ($test in $tests) {
    $R.Add("--- $($test.Label) ---")

    Stop-PadForge
    Set-PadForgeVJoyConfig $test.Slots
    $R.Add("  Configured $($test.Slots.Count) vJoy slot(s), launching PadForge...")

    $ok = Start-PadForgeAndWait -expectedDevices $test.Slots.Count
    if (-not $ok) {
        $R.Add("  FAIL: PadForge did not start")
        $fail++; $R.Add(""); continue
    }

    # Extra wait for vJoy node restart to complete
    Start-Sleep -Seconds 8

    $allGood = $true
    for ($j = 0; $j -lt $test.Slots.Count; $j++) {
        $slotCfg = $test.Slots[$j]
        $rid = $j + 1

        $dev = Query-VJoyDevice -deviceId $rid -retries 5
        if (-not $dev -or $dev.Status -eq 3) {
            $R.Add("  FAIL Device$rid - not found (MISS)")
            $allGood = $false
            continue
        }

        # Only verify buttons and POVs (axes always report 8 due to vJoy fixed layout)
        $btnsOk = $dev.Buttons -eq $slotCfg.Buttons
        $povOk = $dev.ContPovs -eq $slotCfg.Povs

        if ($btnsOk -and $povOk) {
            $R.Add("  PASS Device$rid btns=$($dev.Buttons)/$($slotCfg.Buttons) povs=$($dev.ContPovs)/$($slotCfg.Povs) (axes=$($dev.Axes))")
        } else {
            $R.Add("  FAIL Device$rid expected(btns=$($slotCfg.Buttons) povs=$($slotCfg.Povs)) actual(btns=$($dev.Buttons) povs=$($dev.ContPovs) axes=$($dev.Axes) status=$($dev.Status))")
            $allGood = $false
        }
    }

    if ($allGood) { $pass++ } else { $fail++ }
    $R.Add("")

    # Write intermediate results so progress is visible
    $R | Out-File $logFile -Encoding utf8 -Force
}

# Cleanup: restore Xbox 360 on slot 0
$R.Add("--- Cleanup ---")
Stop-PadForge

[xml]$xml = Get-Content $padForgeXml
$appSettings = $xml.PadForgeSettings.AppSettings
$appSettings.SlotControllerTypes.ChildNodes[0].InnerText = '0'  # Xbox360
$appSettings.SlotCreated.ChildNodes[0].InnerText = 'true'
for ($i = 1; $i -lt 16; $i++) {
    $appSettings.SlotControllerTypes.ChildNodes[$i].InnerText = '0'
    $appSettings.SlotCreated.ChildNodes[$i].InnerText = 'false'
}
$xml.Save($padForgeXml)

Start-Process $padForgeExe
$R.Add("Restored Xbox 360 default, PadForge restarted")

# Clean up temp files
for ($i = 1; $i -le 16; $i++) {
    $f = "$toolsDir\vjoy_query_result_$i.txt"
    if (Test-Path $f) { Remove-Item $f -Force }
}

$R.Add("")
$R.Add("========================================")
$R.Add("RESULTS: $pass passed, $fail failed out of $($tests.Count) tests")
$R.Add("========================================")

$R | Out-File $logFile -Encoding utf8 -Force
$R | ForEach-Object { Write-Host $_ }

param(
    [Parameter(Mandatory)][int]$AppPid,
    [switch]$CloseApp
)

$ErrorActionPreference = 'Stop'
$pass = 0
$fail = 0
$results = @()
$mainHwnd = $null
$windowDeadline = (Get-Date).AddSeconds(15)
do {
    $mainHwnd = (winapp ui list-windows -a $AppPid --json 2>$null | ConvertFrom-Json |
        Where-Object { $_.title -eq 'PiDesk' -and $_.className -eq 'WinUIDesktopWin32WindowClass' } |
        Select-Object -First 1).hwnd
    if (-not $mainHwnd) { Start-Sleep -Milliseconds 250 }
} while (-not $mainHwnd -and (Get-Date) -lt $windowDeadline)
if (-not $mainHwnd) { throw 'PiDesk main window was not found.' }

function Test-UI {
    param([string]$Name, [scriptblock]$Script)
    try {
        & $Script
        $script:pass++
        $script:results += @{ name = $Name; status = 'PASS' }
    } catch {
        $script:fail++
        $script:results += @{ name = $Name; status = 'FAIL'; detail = "$($_.Exception.Message)" }
    }
}

function Invoke-WinApp([scriptblock]$Command) {
    $output = & $Command 2>&1
    if ($LASTEXITCODE -ne 0) { throw ($output -join "`n") }
    return $output
}

function Get-UIValue([string]$Element) {
    (Invoke-WinApp { winapp ui get-value $Element -w $mainHwnd --json } | ConvertFrom-Json).text
}

function Expand-UIElements($Nodes) {
    foreach ($node in @($Nodes)) {
        $node
        if ($node.children) { Expand-UIElements $node.children }
    }
}

function Get-InspectedElements([string]$Selector = '') {
    $tree = if ($Selector) {
        Invoke-WinApp { winapp ui inspect $Selector -w $mainHwnd --json -d 20 } | ConvertFrom-Json
    } else {
        Invoke-WinApp { winapp ui inspect -w $mainHwnd --json -d 20 } | ConvertFrom-Json
    }
    @(Expand-UIElements ($tree.windows | ForEach-Object elements))
}

function Wait-For([string]$Element, [string]$Value, [int]$Timeout = 5000, [switch]$Contains) {
    if ($Contains) {
        Invoke-WinApp { winapp ui wait-for $Element -w $mainHwnd --value $Value --contains -t $Timeout } | Out-Null
    } else {
        Invoke-WinApp { winapp ui wait-for $Element -w $mainHwnd --value $Value -t $Timeout } | Out-Null
    }
}

function Wait-ForAutomationName([string]$Expected, [int]$Timeout = 5000) {
    $deadline = (Get-Date).AddMilliseconds($Timeout)
    do {
        $matches = (winapp ui search $Expected -w $mainHwnd --json 2>$null | ConvertFrom-Json).matches
        if ($matches | Where-Object name -eq $Expected | Select-Object -First 1) { return }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    throw "Automation name did not appear: $Expected"
}

Test-UI 'Deterministic typed RPC fixture becomes ready' {
    Wait-For StatusText Ready 15000
    $model = Get-UIValue ModelCombo
    if ($model -notlike 'Deterministic fixture*') { throw "Unexpected fixture model: $model" }
}

Test-UI 'Safe Markdown renders links and local paths as literal text' {
    $text = Get-UIValue Content-restored-1
    if ($text -notmatch 'external \(https://example\.invalid/path\)') { throw "External link policy was not rendered: $text" }
    if ($text -notmatch 'local \(C:\\\\private') { throw "Local path policy was not rendered: $text" }
}
Test-UI 'Safe Markdown does not load images or interpret raw HTML' {
    $text = Get-UIValue Content-restored-1
    if ($text -notmatch '\[Image: remote\]') { throw 'Image placeholder is missing.' }
    if ($text -match 'image\.png') { throw 'Image destination was exposed or loaded instead of using the placeholder.' }
    if ($text -notmatch '<img src="https://example\.invalid/tracker\.png">') { throw 'Raw HTML was not retained literally.' }
}
Test-UI 'Safe Markdown retains code blocks and unsupported syntax' {
    $text = Get-UIValue Content-restored-1
    if ($text -notmatch 'Write-Output SAFE-CODE') { throw 'Fenced code content is missing.' }
    if ($text -notmatch '~~unsupported~~') { throw 'Unsupported syntax was not retained.' }
}

Invoke-WinApp { winapp ui set-value PromptBox 'run deterministic activity' -w $mainHwnd } | Out-Null
Invoke-WinApp { winapp ui invoke SendButton -w $mainHwnd } | Out-Null

Test-UI 'Thinking streams and completes' {
    Wait-ForAutomationName 'Thinking, Completed, Inspecting deterministic fixture' 5000
}
Test-UI 'Read tool exposes streaming update before final result' {
    Wait-ForAutomationName 'read, Running, STREAMING-CHECKPOINT' 5000
    Wait-ForAutomationName 'read, Completed, READ-FINAL-SUCCESS' 7000
}
Test-UI 'Edit diff is first-class activity' {
    Wait-ForAutomationName 'edit diff, Completed, -before +after' 5000
}
Test-UI 'Failed tool exposes bounded diagnostic state' {
    Wait-ForAutomationName 'bash, Failed, BUILD-FINAL-ERROR' 5000
}
Test-UI 'First activity run settles' { Wait-For StatusText Ready 5000 }

Test-UI 'Thinking expands and collapses by keyboard' {
    Invoke-WinApp { winapp ui send-keys enter --target Expander-activity-2 -w $mainHwnd --via send-input } | Out-Null
    Invoke-WinApp { winapp ui wait-for Expander-activity-2 -w $mainHwnd -p ExpandCollapseState --value Expanded -t 3000 } | Out-Null
    Invoke-WinApp { winapp ui send-keys enter --target Expander-activity-2 -w $mainHwnd --via send-input } | Out-Null
    Invoke-WinApp { winapp ui wait-for Expander-activity-2 -w $mainHwnd -p ExpandCollapseState --value Collapsed -t 3000 } | Out-Null
}
Test-UI 'Tool expands by keyboard and exposes validated arguments' {
    Invoke-WinApp { winapp ui send-keys enter --target Expander-call-read -w $mainHwnd --via send-input } | Out-Null
    Invoke-WinApp { winapp ui wait-for Expander-call-read -w $mainHwnd -p ExpandCollapseState --value Expanded -t 3000 } | Out-Null
    $arguments = Get-UIValue Arguments-call-read
    if ($arguments -notmatch '"path":"fixture\.txt"') { throw "Validated arguments missing: $arguments" }
}
Test-UI 'Expanded tool output supports keyboard copy' {
    Set-Clipboard -Value 'clipboard-sentinel'
    Invoke-WinApp { winapp ui send-keys 'ctrl+a ctrl+c' --target Detail-call-read -w $mainHwnd --via send-input } | Out-Null
    Start-Sleep -Milliseconds 300
    $copied = Get-Clipboard -Raw
    if ($copied.Trim() -ne 'READ-FINAL-SUCCESS') { throw "Unexpected copied tool output: $copied" }
}

Test-UI 'Role tool state and content automation names are bounded' {
    $elements = Get-InspectedElements
    $items = @($elements | Where-Object { $_.type -eq 'ListItem' -and $_.name -match 'Accepted|Completed|Failed|Running|Streaming|Pending' })
    if ($items.Count -lt 7) { throw "Expected activity list items, found $($items.Count)." }
    foreach ($item in $items) {
        if ([string]::IsNullOrWhiteSpace($item.name) -or $item.name.Length -gt 190) {
            throw "Unbounded or empty automation name: '$($item.name)'"
        }
        if ($item.name -notmatch 'Accepted|Completed|Failed|Running|Streaming|Pending') {
            throw "Automation name lacks state: '$($item.name)'"
        }
    }
}

Invoke-WinApp { winapp ui set-value PromptBox 'run large deterministic output' -w $mainHwnd } | Out-Null
Invoke-WinApp { winapp ui invoke SendButton -w $mainHwnd } | Out-Null
Test-UI 'Ten-thousand-line streaming update remains collapsed and operable' {
    $deadline = (Get-Date).AddSeconds(10)
    $largeItem = $null
    while (-not $largeItem -and (Get-Date) -lt $deadline) {
        $matches = (winapp ui search Item-call-large -w $mainHwnd --json 2>$null | ConvertFrom-Json).matches
        $largeItem = $matches | Where-Object { $_.automationId -eq 'Item-call-large' -and $_.name -like 'bash, Running, large line 1 large line 2*' } | Select-Object -First 1
        if (-not $largeItem) { Start-Sleep -Milliseconds 250 }
    }
    if (-not $largeItem) { throw 'The streaming large-output card did not become operable within 10 seconds.' }
    $elements = Get-InspectedElements Item-call-large
    $textControls = @($elements | Where-Object { $_.type -match 'Text|Document' })
    if ($textControls.Count -gt 4) { throw "Collapsed large output created $($textControls.Count) text controls." }
}
Test-UI 'Stop remains responsive during large output' {
    $started = Get-Date
    Invoke-WinApp { winapp ui invoke StopButton -w $mainHwnd } | Out-Null
    Wait-For StatusText Ready 5000
    $elapsed = ((Get-Date) - $started).TotalSeconds
    if ($elapsed -gt 5) { throw "Stop took $([math]::Round($elapsed, 2)) seconds." }
}
Test-UI 'Complete ten-thousand-line output remains available in one control' {
    Invoke-WinApp { winapp ui send-keys enter --target Expander-call-large -w $mainHwnd --via send-input } | Out-Null
    Invoke-WinApp { winapp ui wait-for Detail-call-large -w $mainHwnd -t 10000 } | Out-Null
    $text = Get-UIValue Detail-call-large
    if ($text -notmatch 'large line 10000$') { throw 'The complete large output is not available after expansion.' }
    $elements = Get-InspectedElements Item-call-large
    $detailControls = @($elements | Where-Object { $_.automationId -eq 'Detail-call-large' })
    if ($detailControls.Count -ne 1) { throw "Expected one complete output control, found $($detailControls.Count)." }
}

$artifactDirectory = Join-Path $PSScriptRoot 'artifacts\milestone1-ui-tests'
New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
Invoke-WinApp { winapp ui screenshot -w $mainHwnd -o (Join-Path $artifactDirectory 'activity.png') } | Out-Null

if ($CloseApp) {
    Test-UI 'Fixture app closes cleanly' {
        Invoke-WinApp { winapp ui invoke Close -w $mainHwnd } | Out-Null
        $deadline = (Get-Date).AddSeconds(15)
        while ((Get-Process -Id $AppPid -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 200
        }
        if (Get-Process -Id $AppPid -ErrorAction SilentlyContinue) { throw 'PiDesk did not exit.' }
    }
}

$results | ConvertTo-Json | Set-Content (Join-Path $artifactDirectory 'test-results.json')
Write-Host "Passed: $pass | Failed: $fail"
$results | Where-Object status -eq 'FAIL' | ForEach-Object { Write-Host "FAIL: $($_.name) - $($_.detail)" -ForegroundColor Red }
if ($fail -gt 0) { exit 1 }

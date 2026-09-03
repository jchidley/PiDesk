param(
    [Parameter(Mandatory)][int]$AppPid,
    [string]$ProjectPath = $PSScriptRoot,
    [switch]$CloseApp
)

$ErrorActionPreference = 'Continue'
$pass = 0
$fail = 0
$results = @()
$mainHwnd = (winapp ui list-windows -a $AppPid --json 2>$null | ConvertFrom-Json |
    Where-Object { $_.title -eq 'PiDesk' -and $_.className -eq 'WinUIDesktopWin32WindowClass' } |
    Select-Object -First 1).hwnd
if (-not $mainHwnd) { throw 'PiDesk main window was not found.' }

function Test-UI {
    param([string]$Name, [scriptblock]$Script)
    try {
        $output = & $Script 2>&1
        if ($LASTEXITCODE -eq 0) {
            $script:pass++
            $script:results += @{ name = $Name; status = 'PASS' }
        } else {
            throw ($output -join "`n")
        }
    } catch {
        $script:fail++
        $script:results += @{ name = $Name; status = 'FAIL'; detail = "$_" }
    }
}

function Get-UIValue([string]$Element) {
    (winapp ui get-value $Element -w $mainHwnd --json 2>$null | ConvertFrom-Json).text
}

Test-UI 'Pi 0.84.4 connects and becomes ready' {
    $version = (pi --version).Trim()
    if ($version -ne '0.84.4') { throw "Expected Pi 0.84.4, found $version" }
    winapp ui wait-for StatusText -w $mainHwnd --value Ready -t 15000
}
Test-UI 'Project picker is available' { winapp ui wait-for FolderButton -w $mainHwnd -t 3000 }
Test-UI 'Model selector is populated' {
    $value = Get-UIValue ModelCombo
    if ([string]::IsNullOrWhiteSpace($value)) { throw 'Model selector is empty' }
}
Test-UI 'Thinking selector is populated' {
    $value = Get-UIValue ThinkingCombo
    if ([string]::IsNullOrWhiteSpace($value)) { throw 'Thinking selector is empty' }
}
Test-UI 'Rapid thinking selection keeps the latest choice' {
    winapp ui send-keys home --target ThinkingCombo -w $mainHwnd | Out-Null
    Start-Sleep -Milliseconds 100
    $first = Get-UIValue ThinkingCombo
    winapp ui send-keys end --target ThinkingCombo -w $mainHwnd | Out-Null
    Start-Sleep 2
    $latest = Get-UIValue ThinkingCombo
    if ([string]::IsNullOrWhiteSpace($latest) -or $latest -eq $first) {
        throw "Latest thinking selection was not retained: '$first' -> '$latest'"
    }
    winapp ui send-keys home --target ThinkingCombo -w $mainHwnd | Out-Null
    Start-Sleep 1
}
Test-UI 'Composer accepts text' { winapp ui set-value PromptBox 'Reply with only the result of 73+19.' -w $mainHwnd }
Test-UI 'Send enables for a non-empty prompt' { winapp ui wait-for SendButton -w $mainHwnd -p IsEnabled --value True -t 3000 }
Test-UI 'Prompt can be sent' { winapp ui invoke SendButton -w $mainHwnd }
Test-UI 'Assistant response is shown' { winapp ui wait-for 92 -w $mainHwnd -t 90000 }
Test-UI 'Agent run settles' { winapp ui wait-for StatusText -w $mainHwnd --value Ready -t 10000 }
Test-UI 'Session usage is updated' {
    $value = Get-UIValue UsageText
    if ($value -eq 'No usage yet' -or [string]::IsNullOrWhiteSpace($value)) { throw "Usage did not update: $value" }
}
Test-UI 'New session applies atomically' {
    winapp ui wait-for NewSessionButton -w $mainHwnd -p IsEnabled --value True -t 5000 | Out-Null
    winapp ui invoke NewSessionButton -w $mainHwnd | Out-Null
    winapp ui wait-for 92 -w $mainHwnd --gone -t 15000 | Out-Null
    winapp ui wait-for StatusText -w $mainHwnd --value Ready -t 15000 | Out-Null
}
Test-UI 'Abort settles without losing responsiveness' {
    winapp ui set-value PromptBox 'Run the PowerShell command Start-Sleep -Seconds 15. Do not skip it. Then reply done.' -w $mainHwnd | Out-Null
    winapp ui invoke SendButton -w $mainHwnd | Out-Null
    winapp ui wait-for StopButton -w $mainHwnd -p IsEnabled --value True -t 30000 | Out-Null
    winapp ui invoke StopButton -w $mainHwnd | Out-Null
    Start-Sleep 2
    winapp ui wait-for StatusText -w $mainHwnd --value Ready -t 30000 | Out-Null
}
Test-UI 'Project replacement starts a new usable process' {
    $oldChild = (Get-CimInstance Win32_Process | Where-Object { $_.ParentProcessId -eq $AppPid -and $_.Name -eq 'node.exe' } | Select-Object -First 1).ProcessId
    winapp ui focus PromptBox -w $mainHwnd | Out-Null
    Start-Sleep -Milliseconds 300
    winapp ui click FolderButton -w $mainHwnd | Out-Null
    Start-Sleep 2
    $picker = winapp ui list-windows --json 2>$null | ConvertFrom-Json |
        Where-Object { $_.processName -eq 'PickerHost' -and $_.title -eq 'Select Folder' } |
        Select-Object -Last 1
    if (-not $picker) { throw 'Folder picker did not open' }
    winapp ui set-value 1152 $ProjectPath -w $picker.hwnd | Out-Null
    Start-Sleep -Milliseconds 500
    winapp ui invoke 'Use this project' -w $picker.hwnd | Out-Null
    winapp ui wait-for StatusText -w $mainHwnd --value 'Preparing project…' -t 5000 | Out-Null
    winapp ui wait-for StatusText -w $mainHwnd --value Ready -t 30000 | Out-Null
    winapp ui wait-for NewSessionButton -w $mainHwnd -p IsEnabled --value True -t 5000 | Out-Null
    $newChild = (Get-CimInstance Win32_Process | Where-Object { $_.ParentProcessId -eq $AppPid -and $_.Name -eq 'node.exe' } | Select-Object -First 1).ProcessId
    if (-not $newChild -or $newChild -eq $oldChild) { throw "Pi process was not replaced: $oldChild -> $newChild" }
}

$elements = (winapp ui inspect -w $mainHwnd --interactive --json 2>$null | ConvertFrom-Json).windows.elements
$appControls = @($elements | Where-Object {
    $_.type -match 'Button|Edit|ComboBox' -and
    $_.name -notmatch 'Minimize|Maximize|Minimise|Maximise|Close|System' -and
    -not $_.isOffscreen
})
$missingIds = @($appControls | Where-Object { -not $_.automationId })
if ($missingIds.Count -eq 0) {
    $pass++
    $results += @{ name = 'Interactive controls have AutomationIds'; status = 'PASS' }
} else {
    $fail++
    $results += @{ name = 'Interactive controls have AutomationIds'; status = 'FAIL'; detail = (($missingIds | ForEach-Object name) -join ', ') }
}

$artifactDirectory = Join-Path $PSScriptRoot 'artifacts\ui-tests'
New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
winapp ui screenshot -w $mainHwnd -o (Join-Path $artifactDirectory 'conversation.png') 2>$null | Out-Null

if ($CloseApp) {
    Test-UI 'Clean shutdown exits PiDesk and its Pi child' {
        $child = (Get-CimInstance Win32_Process | Where-Object { $_.ParentProcessId -eq $AppPid -and $_.Name -eq 'node.exe' } | Select-Object -First 1).ProcessId
        winapp ui invoke Close -w $mainHwnd | Out-Null
        $deadline = (Get-Date).AddSeconds(15)
        while ((Get-Process -Id $AppPid -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 200
        }
        if (Get-Process -Id $AppPid -ErrorAction SilentlyContinue) { throw 'PiDesk did not exit' }
        if ($child -and (Get-Process -Id $child -ErrorAction SilentlyContinue)) { throw 'Pi child did not exit' }
    }
}

$results | ConvertTo-Json | Set-Content (Join-Path $artifactDirectory 'test-results.json')
Write-Host "Passed: $pass | Failed: $fail"
$results | Where-Object status -eq 'FAIL' | ForEach-Object { Write-Host "FAIL: $($_.name) - $($_.detail)" -ForegroundColor Red }
if ($fail -gt 0) { exit 1 }

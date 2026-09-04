param(
    [Parameter(Mandatory)][int]$AppPid,
    [switch]$CloseApp
)

$ErrorActionPreference = 'Stop'
$pass = 0; $fail = 0; $results = @()
$mainHwnd = $null
$windowDeadline = (Get-Date).AddSeconds(15)
do {
    $mainHwnd = (winapp ui list-windows -a $AppPid --json 2>$null | ConvertFrom-Json |
        Where-Object { $_.title -eq 'PiDesk' -and $_.className -eq 'WinUIDesktopWin32WindowClass' } |
        Select-Object -First 1).hwnd
    if (-not $mainHwnd) { Start-Sleep -Milliseconds 250 }
} while (-not $mainHwnd -and (Get-Date) -lt $windowDeadline)
if (-not $mainHwnd) { throw 'PiDesk main window was not found.' }

function Invoke-WinApp([scriptblock]$Command) {
    $output = & $Command 2>&1
    if ($LASTEXITCODE -ne 0) { throw ($output -join "`n") }
    return $output
}
function Test-UI([string]$Name, [scriptblock]$Script) {
    try { & $Script; $script:pass++; $script:results += @{ name = $Name; status = 'PASS' } }
    catch { $script:fail++; $script:results += @{ name = $Name; status = 'FAIL'; detail = $_.Exception.Message } }
}
function Get-Value([string]$Id) {
    (Invoke-WinApp { winapp ui get-value $Id -w $mainHwnd --json } | ConvertFrom-Json).text
}
function Wait-Value([string]$Id, [string]$Value, [int]$Timeout = 10000) {
    $deadline = (Get-Date).AddMilliseconds($Timeout)
    do {
        try { if ((Get-Value $Id) -eq $Value) { return } } catch { }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    $actual = try { Get-Value $Id } catch { '<not found>' }
    throw "'$Id' did not reach '$Value' (actual: '$actual')."
}
function Wait-Name([string]$Name, [int]$Timeout = 10000) {
    $deadline = (Get-Date).AddMilliseconds($Timeout)
    do {
        $match = (winapp ui search $Name -w $mainHwnd --json 2>$null | ConvertFrom-Json).matches |
            Where-Object { $_.name -like "*$Name*" } | Select-Object -First 1
        if ($match) { return }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    throw "Accessible content did not appear: $Name"
}
function Select-Backend([string]$Name) {
    Invoke-WinApp { winapp ui invoke BackendCombo -w $mainHwnd } | Out-Null
    Start-Sleep -Milliseconds 300
    $item = (winapp ui search $Name -w $mainHwnd --json 2>$null | ConvertFrom-Json).matches |
        Where-Object { $_.type -eq 'ListItem' -and $_.name -eq $Name } | Select-Object -First 1
    if (-not $item) { throw "Backend item was not found: $Name" }
    Invoke-WinApp { winapp ui click $item.selector -w $mainHwnd } | Out-Null
}
function Select-NextBackendWithKeyboard {
    Invoke-WinApp { winapp ui send-keys home --target BackendCombo -w $mainHwnd --via send-input } | Out-Null
    Invoke-WinApp { winapp ui send-keys down --target BackendCombo -w $mainHwnd --via send-input } | Out-Null
}

Test-UI 'Backend selector is accessible and defaults to Windows' {
    Wait-Value StatusText Ready 15000
    Wait-Value BackendCombo Windows
    $element = (winapp ui search BackendCombo -w $mainHwnd --json 2>$null | ConvertFrom-Json).matches |
        Where-Object automationId -eq 'BackendCombo' | Select-Object -First 1
    if ($element.name -ne 'Pi backend') { throw "Unexpected accessible name: '$($element.name)'" }
    if ($element.type -ne 'ComboBox') { throw "Unexpected control type: '$($element.type)'" }
}

Test-UI 'Keyboard selection confirms replacement and authoritative conversation' {
    Select-NextBackendWithKeyboard
    Wait-Value BackendCombo 'Fixture WSL' 15000
    Wait-Value SessionText 'Fixture WSL fixture' 15000
    Wait-Name 'CONFIRMED-BACKEND: Fixture WSL' 15000
}

Test-UI 'Failed replacement preserves all confirmed visible state' {
    $project = Get-Value ProjectPathText
    Invoke-WinApp { winapp ui set-value PromptBox 'draft survives backend failure' -w $mainHwnd } | Out-Null
    Wait-Value PromptBox 'draft survives backend failure'

    Select-Backend 'Unavailable WSL'

    Wait-Value BackendCombo 'Fixture WSL' 15000
    Wait-Value ProjectPathText $project
    Wait-Value PromptBox 'draft survives backend failure'
    Wait-Name 'CONFIRMED-BACKEND: Fixture WSL' 5000
    $errorText = Get-Value StatusText
    if ($errorText -ne 'Action needed') { throw "Failure was not surfaced: $errorText" }
}

$artifactDirectory = Join-Path $PSScriptRoot 'artifacts\backend-ui-tests'
New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
Invoke-WinApp { winapp ui screenshot -w $mainHwnd -o (Join-Path $artifactDirectory 'backend-selector.png') } | Out-Null

if ($CloseApp) {
    Test-UI 'Fixture app closes cleanly' {
        Invoke-WinApp { winapp ui invoke Close -w $mainHwnd } | Out-Null
        $deadline = (Get-Date).AddSeconds(15)
        while ((Get-Process -Id $AppPid -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
        if (Get-Process -Id $AppPid -ErrorAction SilentlyContinue) { throw 'PiDesk did not exit.' }
    }
}

$results | ConvertTo-Json | Set-Content (Join-Path $artifactDirectory 'test-results.json')
Write-Host "Passed: $pass | Failed: $fail"
$results | Where-Object status -eq 'FAIL' | ForEach-Object { Write-Host "FAIL: $($_.name) - $($_.detail)" -ForegroundColor Red }
if ($fail -gt 0) { exit 1 }

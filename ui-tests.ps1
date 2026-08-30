param([Parameter(Mandatory)][int]$AppPid)

$ErrorActionPreference = 'Continue'
$pass = 0
$fail = 0
$results = @()

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

Test-UI 'Pi connects and becomes ready' { winapp ui wait-for StatusText -a $AppPid --value Ready -t 15000 }
Test-UI 'Project picker is available' { winapp ui wait-for FolderButton -a $AppPid -t 3000 }
Test-UI 'Model selector is populated' {
    $value = (winapp ui get-value ModelCombo -a $AppPid --json | ConvertFrom-Json).text
    if ([string]::IsNullOrWhiteSpace($value)) { throw 'Model selector is empty' }
}
Test-UI 'Thinking selector is populated' {
    $value = (winapp ui get-value ThinkingCombo -a $AppPid --json | ConvertFrom-Json).text
    if ([string]::IsNullOrWhiteSpace($value)) { throw 'Thinking selector is empty' }
}
Test-UI 'Composer accepts text' { winapp ui set-value PromptBox 'Reply with only the result of 73+19.' -a $AppPid }
Test-UI 'Send enables for a non-empty prompt' { winapp ui wait-for SendButton -a $AppPid -p IsEnabled --value True -t 3000 }
Test-UI 'Prompt can be sent' { winapp ui invoke SendButton -a $AppPid }
Test-UI 'Assistant response is shown' { winapp ui wait-for 92 -a $AppPid -t 90000 }
Test-UI 'Agent run settles' { winapp ui wait-for StatusText -a $AppPid --value Ready -t 10000 }
Test-UI 'Session usage is updated' {
    $value = (winapp ui get-value UsageText -a $AppPid --json | ConvertFrom-Json).text
    if ($value -eq 'No usage yet' -or [string]::IsNullOrWhiteSpace($value)) { throw "Usage did not update: $value" }
}

$elements = (winapp ui inspect -a $AppPid --interactive --json 2>$null | ConvertFrom-Json).windows.elements
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
winapp ui screenshot -a $AppPid -o (Join-Path $artifactDirectory 'conversation.png') 2>$null | Out-Null
$results | ConvertTo-Json | Set-Content (Join-Path $artifactDirectory 'test-results.json')
Write-Host "Passed: $pass | Failed: $fail"
$results | Where-Object status -eq 'FAIL' | ForEach-Object { Write-Host "FAIL: $($_.name) - $($_.detail)" -ForegroundColor Red }
if ($fail -gt 0) { exit 1 }

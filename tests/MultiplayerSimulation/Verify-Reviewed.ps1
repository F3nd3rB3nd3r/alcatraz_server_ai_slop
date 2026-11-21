param([switch]$NoRestore)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$project = Join-Path $PSScriptRoot 'MultiplayerSimulation.csproj'
$output = Join-Path $repository ('artifacts\review-verification-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $output)
$report = Join-Path $output 'full.json'
[string[]]$restoreArguments = if ($NoRestore) { @('--no-restore') } else { @() }

Push-Location $repository
try {
    # The complete correctness command must still expose the three documented
    # exploratory observations. Never accept an arbitrary nonzero test result.
    $ErrorActionPreference = 'Continue'
    dotnet run --project $project @restoreArguments -- --output $report *> (Join-Path $output 'full.log')
    $fullExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if (!(Test-Path -LiteralPath $report)) { throw 'The harness produced no report; inspect full.log.' }
    $observations = @((Get-Content -LiteralPath $report -Raw | ConvertFrom-Json).observations)
    $errors = @($observations | Where-Object { $_.Error })
    $failures = @($observations | Where-Object { !$_.InvariantPassed } | ForEach-Object Name | Sort-Object)
    $expected = @('final-fragment-arrives-first','cancel-party-membership','url-registration-order') | Sort-Object
    if ($fullExit -ne 1 -or $observations.Count -ne 281 -or $errors.Count -ne 0 -or
        ($failures -join '|') -ne ($expected -join '|')) {
        throw "Unexpected full result: exit=$fullExit, total=$($observations.Count), errors=$($errors.Count), failures=$($failures -join ', '). Inspect $output"
    }

    # The writer-failure containment control intentionally writes to stderr.
    # Check the suite exit and complete summary rather than stderr presence.
    $ErrorActionPreference = 'Continue'
    dotnet run --project $project --no-build --no-restore -- --verify-diagnostics *> (Join-Path $output 'diagnostics.log')
    $diagnosticsExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $diagnostics = Get-Content -LiteralPath (Join-Path $output 'diagnostics.log')
    if ($diagnosticsExit -ne 0 -or @($diagnostics | Where-Object { $_ -like 'PASS *' }).Count -ne 40 -or
        !($diagnostics | Where-Object { $_ -like 'Diagnostics checks: 0 failures.*' })) {
        throw "Diagnostics verification did not complete all 40 checks. Inspect $output"
    }
    Write-Output 'PASS: 278 corrected/control scenarios and 40 diagnostics checks; zero harness errors.'
    Write-Output ('Three unchanged exploratory observations remain visible: ' + ($expected -join ', '))
    Write-Output "Reports: $output"
} finally {
    Pop-Location
}

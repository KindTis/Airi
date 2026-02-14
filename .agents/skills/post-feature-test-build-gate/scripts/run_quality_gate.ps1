param(
    [string]$SolutionPath = "Airi.sln",
    [string]$TestProjectPath = "tests/Airi.Tests/Airi.Tests.csproj",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$SkipRestore,
    [switch]$CollectCoverage
)

$ErrorActionPreference = "Stop"

function Invoke-Step {
    param(
        [string]$Name,
        [scriptblock]$Action
    )

    Write-Host "==> $Name"
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "Step failed: $Name (exit code: $LASTEXITCODE)"
    }
}

if (-not $SkipRestore) {
    Invoke-Step -Name "Restore" -Action {
        dotnet restore
    }
}

Invoke-Step -Name "Build" -Action {
    dotnet build $SolutionPath -c $Configuration
}

if ($CollectCoverage) {
    Invoke-Step -Name "Test with coverage" -Action {
        dotnet test $TestProjectPath -c $Configuration --collect:"XPlat Code Coverage"
    }
}
else {
    Invoke-Step -Name "Test" -Action {
        dotnet test $TestProjectPath -c $Configuration
    }
}

Write-Host "==> Quality gate completed successfully."

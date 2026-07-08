param(
    [string]$ProjectRoot = "C:\Users\Srdjan\Documents\EU Projets",
    [int]$MinScore = 4
)

$ErrorActionPreference = "Stop"
$project = Join-Path $ProjectRoot "src\EuCallTracker"

dotnet run --no-restore --project $project -- update
dotnet run --no-restore --project $project -- report --open-only --min-score $MinScore

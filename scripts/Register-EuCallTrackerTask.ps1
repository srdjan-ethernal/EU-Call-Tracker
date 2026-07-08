param(
    [string]$ProjectRoot = "C:\Users\Srdjan\Documents\EU Projets",
    [string]$TaskName = "EU Call Tracker Daily Update",
    [string]$At = "09:00"
)

$scriptPath = Join-Path $ProjectRoot "scripts\Update-EuCalls.ps1"
$action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`""
$trigger = New-ScheduledTaskTrigger -Daily -At $At

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Description "Daily update of EU calls for SMEs from Serbia." -Force

#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Crea la tarea programada "MevaRT Refresh" en el Task Scheduler.
.DESCRIPTION
    La tarea ejecuta refresh.bat dos veces al dia (por defecto 06:30 y 13:30)
    usando la cuenta SYSTEM. Los logs se guardan en scripts\refresh.log.
.EXAMPLE
    .\create-task.ps1
.EXAMPLE
    .\create-task.ps1 -MorningTime "07:00" -AfternoonTime "14:00"
#>
param(
    [string]$MorningTime   = "06:30",
    [string]$AfternoonTime = "13:30",
    [string]$ScriptsPath   = $PSScriptRoot
)

$ErrorActionPreference = "Stop"
$taskName = "MevaRT Refresh"
$batPath  = "$ScriptsPath\refresh.bat"

if (-not (Test-Path $batPath)) {
    throw "No se encontro refresh.bat en $ScriptsPath"
}

$logPath = "$ScriptsPath\refresh.log"
$action  = New-ScheduledTaskAction `
    -Execute  "cmd.exe" `
    -Argument "/c `"$batPath`" >> `"$logPath`" 2>&1"

$triggers = @(
    (New-ScheduledTaskTrigger -Daily -At $MorningTime),
    (New-ScheduledTaskTrigger -Daily -At $AfternoonTime)
)

$settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit (New-TimeSpan -Hours 2) `
    -MultipleInstances IgnoreNew `
    -StartWhenAvailable

$principal = New-ScheduledTaskPrincipal `
    -UserId    "SYSTEM" `
    -LogonType ServiceAccount `
    -RunLevel  Highest

Register-ScheduledTask `
    -TaskName  $taskName `
    -Action    $action `
    -Trigger   $triggers `
    -Settings  $settings `
    -Principal $principal `
    -Force | Out-Null

$task = Get-ScheduledTask -TaskName $taskName
Write-Host ""
Write-Host "Tarea '$taskName' registrada correctamente." -ForegroundColor Green
Write-Host "  Horarios: $MorningTime y $AfternoonTime (diario)" -ForegroundColor Green
Write-Host "  Log:      $logPath" -ForegroundColor Green
Write-Host ""
Write-Host "Para ejecutar manualmente ahora:" -ForegroundColor Yellow
Write-Host "  Start-ScheduledTask -TaskName '$taskName'" -ForegroundColor Yellow

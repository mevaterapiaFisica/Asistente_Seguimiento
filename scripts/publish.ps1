#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Publica los ultimos cambios del codigo al servicio Windows MevaRT.
.DESCRIPTION
    Detiene el servicio, ejecuta dotnet publish en Release y reinicia el servicio.
    No toca variables de entorno ni reinstala el servicio.
    Publica tanto el web como AriaRunner a sus respectivos subdirectorios.
.PARAMETER PublishPath
    Directorio de destino raiz (debe coincidir con el binPath del servicio).
#>
param(
    [string]$PublishPath = "C:\MevaRT"
)

$ErrorActionPreference = "Stop"
$serviceName       = "MevaRT"
$projectRoot       = Split-Path $PSScriptRoot -Parent
$webProject        = "$projectRoot\Meva.Rt.Web\Meva.Rt.Web.csproj"
$ariaRunnerProject = "$projectRoot\Meva.Rt.AriaRunner\Meva.Rt.AriaRunner.csproj"
$ariaRunnerPath    = "$PublishPath\AriaRunner"

# ── 1. Detener servicio ──────────────────────────────────────────────────────
$svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne 'Stopped') {
    Write-Host "Deteniendo servicio $serviceName..." -ForegroundColor Cyan
    Stop-Service $serviceName -Force
    Start-Sleep -Seconds 3
    Write-Host "   Detenido."
} else {
    Write-Host "Servicio $serviceName ya estaba detenido." -ForegroundColor Yellow
}

# ── 2. Publicar web ──────────────────────────────────────────────────────────
Write-Host "Publicando web en $PublishPath ..." -ForegroundColor Cyan
dotnet publish $webProject -c Release -o $PublishPath --self-contained false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (web) fallo con codigo $LASTEXITCODE" }
Write-Host "   Web publicado."

# ── 3. Publicar AriaRunner ───────────────────────────────────────────────────
Write-Host "Publicando AriaRunner en $ariaRunnerPath ..." -ForegroundColor Cyan
dotnet publish $ariaRunnerProject -c Release -f net9.0-windows -o $ariaRunnerPath --self-contained false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (AriaRunner) fallo con codigo $LASTEXITCODE" }
Write-Host "   AriaRunner publicado."

# ── 4. Reiniciar servicio ────────────────────────────────────────────────────
Write-Host "Iniciando servicio $serviceName..." -ForegroundColor Cyan
Start-Service $serviceName
$svc = Get-Service -Name $serviceName
Write-Host ""
Write-Host "Listo. Servicio '$serviceName' en estado: $($svc.Status)" -ForegroundColor Green
Write-Host "AriaRunner: $ariaRunnerPath\AriaRunner.exe" -ForegroundColor Green

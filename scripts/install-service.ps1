#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Instala o reinstala el servicio Windows MevaRT.
.DESCRIPTION
    Publica la aplicacion, instala browsers de Playwright, crea el servicio
    Windows y configura las variables de entorno necesarias en el registro.
.EXAMPLE
    .\install-service.ps1 -SitraMedUser "usuario" -SitraMedPassword "clave"
.EXAMPLE
    .\install-service.ps1 -SitraMedUser "usuario" -SitraMedPassword "clave" -PublishPath "D:\MevaRT"
#>
param(
    [Parameter(Mandatory)][string]$SitraMedUser,
    [Parameter(Mandatory)][string]$SitraMedPassword,
    [string]$PublishPath      = "C:\MevaRT",
    [string]$BrowsersPath     = "C:\PlaywrightBrowsers",
    [string]$ServicePort      = "5062"
)

$ErrorActionPreference = "Stop"
$serviceName       = "MevaRT"
$projectRoot       = Split-Path $PSScriptRoot -Parent
$webProject        = "$projectRoot\Meva.Rt.Web\Meva.Rt.Web.csproj"
$ariaRunnerProject = "$projectRoot\Meva.Rt.AriaRunner\Meva.Rt.AriaRunner.csproj"
$ariaRunnerPath    = "$PublishPath\AriaRunner"

Write-Host "=== 0/5 Deteniendo servicio si esta corriendo ===" -ForegroundColor Cyan
$svcPre = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($svcPre -and $svcPre.Status -ne 'Stopped') {
    Stop-Service $serviceName -Force
    Start-Sleep -Seconds 3
    Write-Host "   Servicio detenido."
}

Write-Host "=== 1/5 Publicando aplicacion ===" -ForegroundColor Cyan
dotnet publish $webProject -c Release -o $PublishPath --self-contained false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (web) fallo con codigo $LASTEXITCODE" }
dotnet publish $ariaRunnerProject -c Release -f net9.0-windows -o $ariaRunnerPath --self-contained false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (AriaRunner) fallo con codigo $LASTEXITCODE" }
Write-Host "   Web y AriaRunner publicados."

Write-Host "=== 2/5 Instalando browsers de Playwright ===" -ForegroundColor Cyan
$env:PLAYWRIGHT_BROWSERS_PATH = $BrowsersPath
$playwrightPs1 = "$PublishPath\playwright.ps1"
if (Test-Path $playwrightPs1) {
    & pwsh -File $playwrightPs1 install chromium
    if ($LASTEXITCODE -ne 0) { Write-Warning "playwright install termino con codigo $LASTEXITCODE" }
} else {
    Write-Warning "playwright.ps1 no encontrado en $PublishPath — instalar browsers manualmente."
}

Write-Host "=== 3/5 Creando directorio de datos ===" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path "$PublishPath\data" | Out-Null
Write-Host "   Directorio: $PublishPath\data"

Write-Host "=== 4/5 Instalando servicio Windows ===" -ForegroundColor Cyan
$svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne 'Stopped') {
        Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 3
    }
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 2
}
sc.exe create $serviceName binPath= "`"$PublishPath\Meva.Rt.Web.exe`"" start= auto DisplayName= "Meva RT Web"
if ($LASTEXITCODE -ne 0) { throw "sc.exe create fallo con codigo $LASTEXITCODE" }
sc.exe description $serviceName "Servidor web Meva RT: scraping SitraMed y ARIA, UI intranet" | Out-Null

Write-Host "=== 5/5 Configurando variables de entorno del servicio ===" -ForegroundColor Cyan
$regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
[string[]]$envVars = @(
    "ASPNETCORE_URLS=http://0.0.0.0:$ServicePort",
    "ASPNETCORE_ENVIRONMENT=Production",
    "MEVA_SITRAMED_USER=$SitraMedUser",
    "MEVA_SITRAMED_PASSWORD=$SitraMedPassword",
    "MEVA_SITRAMED_NO_FALLBACK=true",
    "MEVA_DATA_DIR=$PublishPath\data",
    "MEVA_ARIA_RUNNER_EXE=$ariaRunnerPath\AriaRunner.exe",
    "PLAYWRIGHT_BROWSERS_PATH=$BrowsersPath"
)
Set-ItemProperty -Path $regPath -Name Environment -Value $envVars

Write-Host "   Iniciando servicio..."
Start-Service $serviceName
$svc = Get-Service -Name $serviceName
Write-Host ""
Write-Host "Servicio '$serviceName' instalado y en estado: $($svc.Status)" -ForegroundColor Green
Write-Host "Acceder via: http://localhost:$ServicePort  o  http://<ip-maquina>:$ServicePort" -ForegroundColor Green
Write-Host ""
Write-Host "Para actualizar solo las credenciales sin reinstalar:" -ForegroundColor Yellow
Write-Host "  Editar HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName\Environment" -ForegroundColor Yellow
Write-Host "  Luego: Restart-Service $serviceName" -ForegroundColor Yellow

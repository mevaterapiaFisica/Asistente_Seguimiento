# Abre el puerto "viewer" (solo Frecuencia de imagenes, solo lectura) a la red local.
# Ejecutar como Administrador en la PC donde corre el servicio.
# Para cerrarlo: Remove-NetFirewallRule -DisplayName "MevaRT Viewer"
param([int]$Port = 5063)
Remove-NetFirewallRule -DisplayName "MevaRT Viewer" -ErrorAction SilentlyContinue
New-NetFirewallRule -DisplayName "MevaRT Viewer" -Direction Inbound -Protocol TCP -LocalPort $Port `
    -Profile Domain,Private -Action Allow | Out-Null
Write-Host "Puerto $Port abierto. Link: http://<ip-servidor>:$Port/   (por equipo: ?equipo=Equipo3)" -ForegroundColor Green

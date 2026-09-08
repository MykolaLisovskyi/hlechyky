<#
.SYNOPSIS
  Глечики — launcher. build | start | stop | restart | status | logs

  start   — liquidsoap (Docker) + сервер + Caddy у фоні (логи в logs\server.log, logs\caddy.log)
  build   — зібрати Release у build\ (start робить це сам, якщо build\ порожній)
  stop    — зупинити Caddy, сервер і liquidsoap
  restart — перезібрати і перезапустити сервер; Caddy не чіпає (слухачі не відвалюються)
  status  — що працює
  logs    — хвіст логу сервера

  У копії для розробки (нема D:\radio\radio.ps1) Icecast підіймається разом із liquidsoap з liquidsoap\docker-compose.dev.yml,
  а без tools\caddy\caddy.exe крок Caddy пропускається. Дивись CONTRIBUTING.md.
#>
param([ValidateSet('build', 'start', 'stop', 'restart', 'status', 'logs', 'autostart')][string]$Cmd = 'status')

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$Build = Join-Path $Root 'build'
$Dll = Join-Path $Build 'Hlechyky.dll'
$Log = Join-Path $Root 'logs\server.log'
$PidFile = Join-Path $Root 'data\server.pid'
$Caddy = Join-Path $Root 'tools\caddy\caddy.exe'
$Caddyfile = Join-Path $Root 'Caddyfile'
$CaddyPidFile = Join-Path $Root 'data\caddy.pid'
$env:HLECHYKY_ROOT = $Root   # читають і сервер, і Caddyfile ({$HLECHYKY_ROOT})
# Icecast власника живе окремо (D:\radio). Нема того скрипта (копія для розробки) — Icecast іде з docker-compose.dev.yml разом із liquidsoap.
$OwnerIcecast = 'D:\radio\radio.ps1'
$ComposeArgs = if (Test-Path $OwnerIcecast) { @() } else { @('-f', 'docker-compose.dev.yml') }

function Get-ProcessFromPidFile([string]$File, [string]$Name) {
    if (-not (Test-Path $File)) { return $null }
    $p = Get-Process -Id (Get-Content $File) -ErrorAction SilentlyContinue
    if ($p -and $p.ProcessName -eq $Name) { return $p }
    Remove-Item $File -ErrorAction SilentlyContinue
    return $null
}

function Get-Server { Get-ProcessFromPidFile $PidFile 'dotnet' }
function Get-Caddy { Get-ProcessFromPidFile $CaddyPidFile 'caddy' }

function Invoke-Build {
    Write-Host 'Збираю Release…'
    dotnet publish (Join-Path $Root 'src\Hlechyky\Hlechyky.csproj') -c Release -o $Build --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish впав' }
}

function Test-Icecast {
    try { Invoke-WebRequest -UseBasicParsing -TimeoutSec 3 'http://127.0.0.1:8000/status-json.xsl' | Out-Null; return $true } catch { return $false }
}

function Ensure-Icecast {
    if (Test-Icecast) { return }
    if (-not (Test-Path $OwnerIcecast)) { return }   # копія для розробки: Icecast підніме docker-compose.dev.yml
    Write-Host 'Icecast не працює, піднімаю через D:\radio\radio.ps1 start (Docker Desktop може стартувати хвилину-дві)…'
    powershell -NoProfile -ExecutionPolicy Bypass -File $OwnerIcecast start | Out-Null
    for ($i = 0; $i -lt 60; $i++) { if (Test-Icecast) { Write-Host 'Icecast піднявся'; return }; Start-Sleep 3 }
    throw 'Icecast так і не піднявся, дивись D:\radio\logs'
}

function Start-Liquidsoap {
    Ensure-Icecast
    Push-Location (Join-Path $Root 'liquidsoap')
    try { docker compose @ComposeArgs up -d } finally { Pop-Location }
}

function Install-Autostart {
    $vbs = Join-Path $Root 'Hlechyky.vbs'
    $ps1 = Join-Path $Root 'start.ps1'
    @(
        'Set sh = CreateObject("WScript.Shell")'
        "sh.Run ""powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """"$ps1"""" start"", 0, False"
    ) | Set-Content $vbs -Encoding ASCII
    $startup = [Environment]::GetFolderPath('Startup')
    Copy-Item $vbs (Join-Path $startup 'Hlechyky.vbs') -Force
    Write-Host "Автозапуск встановлено: $startup\Hlechyky.vbs (стартує через хвилину після входу в Windows)"
}

function Start-Server {
    if (Get-Server) { Write-Host 'Сервер уже працює'; return }
    if (-not (Test-Path $Dll)) { Invoke-Build }
    New-Item -ItemType Directory -Force (Join-Path $Root 'logs'), (Join-Path $Root 'data') | Out-Null
    $p = Start-Process -FilePath 'dotnet' -ArgumentList "`"$Dll`"" -WorkingDirectory $Root `
        -RedirectStandardOutput $Log -RedirectStandardError (Join-Path $Root 'logs\server.err.log') -WindowStyle Hidden -PassThru
    Set-Content $PidFile $p.Id
    Write-Host "Сервер запущено (pid $($p.Id)), лог: $Log"
}

function Stop-Server {
    $p = Get-Server
    if ($p) { Stop-Process -Id $p.Id -Force; Remove-Item $PidFile -ErrorAction SilentlyContinue; Write-Host 'Сервер зупинено' }
    else { Write-Host 'Сервер не працював' }
}

# Caddy: https://hlechyky.pp.ua → сервер :8080, /radio.mp3 → Icecast :8000. Сертифікат Let's Encrypt бере сам (потрібні порти 80/443 ззовні).
function Start-Caddy {
    if (Get-Caddy) { Write-Host 'Caddy уже працює'; return }
    if (-not (Test-Path $Caddy)) { Write-Host "Caddy пропускаю: нема $Caddy (локально він не потрібен; для проду качати https://caddyserver.com/api/download?os=windows&arch=amd64)"; return }
    New-Item -ItemType Directory -Force (Join-Path $Root 'logs'), (Join-Path $Root 'data\caddy') | Out-Null
    # caddy пише службові рядки в stderr; з ErrorActionPreference=Stop PowerShell вважав би їх помилкою
    $prevEap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    $check = & $Caddy validate --config $Caddyfile --adapter caddyfile 2>&1 | ForEach-Object { "$_" }
    $ErrorActionPreference = $prevEap
    if ($LASTEXITCODE -ne 0) { $check | Write-Host; throw 'Caddyfile не проходить перевірку' }
    $p = Start-Process -FilePath $Caddy -ArgumentList 'run', '--config', "`"$Caddyfile`"", '--adapter', 'caddyfile' -WorkingDirectory $Root `
        -RedirectStandardOutput (Join-Path $Root 'logs\caddy.out.log') -RedirectStandardError (Join-Path $Root 'logs\caddy.err.log') -WindowStyle Hidden -PassThru
    Set-Content $CaddyPidFile $p.Id
    Write-Host "Caddy запущено (pid $($p.Id)), лог: logs\caddy.log"
}

function Stop-Caddy {
    $p = Get-Caddy
    if ($p) { Stop-Process -Id $p.Id -Force; Remove-Item $CaddyPidFile -ErrorAction SilentlyContinue; Write-Host 'Caddy зупинено' }
    else { Write-Host 'Caddy не працював' }
}

switch ($Cmd) {
    'build'   { Invoke-Build }
    'start'   { Start-Liquidsoap; Start-Server; Start-Caddy }
    'stop'    { Stop-Caddy; Stop-Server; Push-Location (Join-Path $Root 'liquidsoap'); try { docker compose @ComposeArgs stop } finally { Pop-Location } }
    'restart' { Stop-Server; Invoke-Build; Start-Liquidsoap; Start-Server; Start-Caddy }
    'status'  {
        $p = Get-Server
        Write-Host ("Сервер:     " + $(if ($p) { "працює (pid $($p.Id))" } else { 'зупинений' }))
        $c = Get-Caddy
        Write-Host ("Caddy:      " + $(if ($c) { "працює (pid $($c.Id)), https://hlechyky.pp.ua" } else { 'зупинений' }))
        $liq = docker ps --filter 'name=hlechyky-liq' --format '{{.Status}}'
        Write-Host ("liquidsoap: " + $(if ($liq) { $liq } else { 'зупинений' }))
        $ice = docker ps --filter 'name=icecast' --format '{{.Names}}: {{.Status}}'
        $iceHint = if (Test-Path $OwnerIcecast) { 'D:\radio\radio.ps1 start' } else { 'docker compose -f liquidsoap\docker-compose.dev.yml up -d' }
        Write-Host ("Icecast:    " + $(if ($ice) { $ice } else { "зупинений ($iceHint)" }))
    }
    'logs'    { Get-Content $Log -Tail 40 }
    'autostart' { Install-Autostart }
}

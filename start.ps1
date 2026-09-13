<#
.SYNOPSIS
  Глечики — launcher. build | start | stop | restart | status | logs | autostart | watchdog

  start     — liquidsoap (Docker) + сервер + Caddy у фоні (логи в logs\server.log, logs\caddy.log); знімає автонагляд з паузи
  build     — зібрати Release у build\ (start робить це сам, якщо build\ порожній)
  stop      — зупинити Caddy, сервер і liquidsoap; автонагляд стає на паузу, доки не буде start
  restart   — перезібрати і перезапустити сервер; Caddy не чіпає (слухачі не відвалюються)
  status    — що працює
  logs      — хвіст логу сервера
  autostart — завдання «Hlechyky» у Планувальнику: при вході у Windows і щохвилини запускає watchdog
  watchdog  — одна перевірка: піднімає те, що впало (сервер, Caddy, liquidsoap, наглядач D:\radio), завислий сервер
              перезапускає. Пише в logs\watchdog.log лише тоді, коли щось робить

  У копії для розробки (нема D:\radio\radio.ps1) Icecast підіймається разом із liquidsoap з liquidsoap\docker-compose.dev.yml,
  а без tools\caddy\caddy.exe крок Caddy пропускається. Дивись CONTRIBUTING.md.
#>
param([ValidateSet('build', 'start', 'stop', 'restart', 'status', 'logs', 'autostart', 'watchdog')][string]$Cmd = 'status')

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$Build = Join-Path $Root 'build'
$Dll = Join-Path $Build 'Hlechyky.dll'
$Log = Join-Path $Root 'logs\server.log'
$ErrLog = Join-Path $Root 'logs\server.err.log'
$PidFile = Join-Path $Root 'data\server.pid'
$Caddy = Join-Path $Root 'tools\caddy\caddy.exe'
$Caddyfile = Join-Path $Root 'Caddyfile'
$CaddyPidFile = Join-Path $Root 'data\caddy.pid'
$StopFlag = Join-Path $Root 'data\stopped.flag'
$WatchState = Join-Path $Root 'data\watchdog.json'
$WatchLog = Join-Path $Root 'logs\watchdog.log'
$DeployLock = Join-Path $Root 'data\deploy.lock'
$TaskName = 'Hlechyky'
$Vbs = Join-Path $Root 'Hlechyky.vbs'
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

# Процес, що слухає порт, якщо це саме той, кого чекаємо (pid-файл загубився, а процес живий).
function Find-Listener([int]$Port, [string]$Name) {
    $c = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $c) { return $null }
    $p = Get-Process -Id $c.OwningProcess -ErrorAction SilentlyContinue
    if ($p -and $p.ProcessName -eq $Name) { return $p }
    return $null
}

function Invoke-Build {
    Write-Host 'Збираю Release…'
    dotnet publish (Join-Path $Root 'src\Hlechyky\Hlechyky.csproj') -c Release -o $Build --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish впав' }
    # Позначка для deploy.ps1: з якого коміту зібрано те, що зараз лежить у build\
    New-Item -ItemType Directory -Force (Join-Path $Root 'data') | Out-Null
    try { Set-Content (Join-Path $Root 'data\built.sha') (git -C $Root rev-parse HEAD) -Encoding ASCII } catch { }
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

# Автозапуск і автонагляд — завдання в Планувальнику, як у LeBot: при вході у Windows і далі щохвилини.
# Кожен запуск — коротка перевірка start.ps1 watchdog, тож окремий вічний процес-наглядач не потрібен (і сам не впаде).
function Install-Autostart {
    $ps1 = Join-Path $Root 'start.ps1'
    # wscript ховає вікно повністю (сам powershell -WindowStyle Hidden блимав би консоллю щохвилини) і не чекає на скрипт
    @(
        'Set sh = CreateObject("WScript.Shell")'
        "sh.Run ""powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """"$ps1"""" watchdog"", 0, False"
    ) | Set-Content $Vbs -Encoding ASCII
    $user = "$env:USERDOMAIN\$env:USERNAME"
    $action = New-ScheduledTaskAction -Execute 'wscript.exe' -Argument "//B //Nologo `"$Vbs`"" -WorkingDirectory $Root
    $logon = New-ScheduledTaskTrigger -AtLogOn -User $user
    $every = New-ScheduledTaskTrigger -Once -At (Get-Date).Date -RepetitionInterval (New-TimeSpan -Minutes 1)
    $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $logon, $every -Settings $settings -Principal $principal `
        -Description "Глечики: піднімає сайт після входу у Windows і щохвилини перевіряє, чи ніщо не впало ($ps1 watchdog)" -Force | Out-Null
    # Старий автозапуск (Hlechyky.vbs в автозавантаженні) робив start і змагався б з автонаглядом
    $legacy = Join-Path ([Environment]::GetFolderPath('Startup')) 'Hlechyky.vbs'
    if (Test-Path $legacy) { Remove-Item $legacy -Force }
    Write-Host "Автозапуск і автонагляд встановлено: завдання «$TaskName» у Планувальнику (при вході і щохвилини), лог дій: $WatchLog"
}

function Start-Server {
    if (Get-Server) { Write-Host 'Сервер уже працює'; return }
    if (-not (Test-Path $Dll)) { Invoke-Build }
    New-Item -ItemType Directory -Force (Join-Path $Root 'logs'), (Join-Path $Root 'data') | Out-Null
    # Попередній лог лишаємо поруч: після падіння причина саме там, а новий запуск перезаписав би файл
    foreach ($f in $Log, $ErrLog) {
        try { if ((Test-Path $f) -and (Get-Item $f).Length -gt 0) { Move-Item $f ($f -replace '\.log$', '.prev.log') -Force } } catch { }
    }
    $p = Start-Process -FilePath 'dotnet' -ArgumentList "`"$Dll`"" -WorkingDirectory $Root `
        -RedirectStandardOutput $Log -RedirectStandardError $ErrLog -WindowStyle Hidden -PassThru
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

# ---------- автонагляд ----------

# Один start/stop/restart/watchdog за раз: інакше автонагляд підняв би старий сервер посеред restart, поки publish
# переписує build\. М'ютекс звільняється сам, навіть якщо процес убили (тоді наступний отримає AbandonedMutexException — це теж «моє»).
function Enter-Launcher([int]$WaitSeconds) {
    $script:mutex = New-Object Threading.Mutex($false, 'Local\Hlechyky-launcher')
    try { return $script:mutex.WaitOne([TimeSpan]::FromSeconds($WaitSeconds)) }
    catch { if ($_.Exception.GetBaseException() -is [Threading.AbandonedMutexException]) { return $true }; throw }
}

function Write-Watch([string]$Message) {
    if ((Test-Path $WatchLog) -and (Get-Item $WatchLog).Length -gt 1MB) { Move-Item $WatchLog ($WatchLog -replace '\.log$', '.prev.log') -Force }
    Add-Content $WatchLog ('{0}  {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message) -Encoding UTF8
}

function Read-WatchState {
    $d = [ordered]@{ lastRun = 0; httpFails = 0; iceMisses = 0; mountMisses = 0; radioMisses = 0; liqRestartAt = 0; serverStarts = @() }
    $s = $null
    try { if (Test-Path $WatchState) { $s = Get-Content $WatchState -Raw | ConvertFrom-Json } } catch { }
    if ($s) { foreach ($k in @($d.Keys)) { if ($null -ne $s.$k) { $d[$k] = $s.$k } } }
    return $d
}

function Test-DeployRunning {
    if (-not (Test-Path $DeployLock)) { return $false }
    # deploy.ps1 тримає файл відкритим без спільного доступу; відкрився — значить, лишився від убитого деплою
    try { [IO.File]::Open($DeployLock, 'Open', 'Read', 'ReadWrite').Dispose(); return $false } catch { return $true }
}

# docker при напівживому Docker Desktop може висіти вічно, а автонагляд не має права зависнути разом із ним (тримав би м'ютекс).
function Invoke-Docker([string[]]$Arguments, [int]$TimeoutSec = 120) {
    $out = Join-Path $Root 'data\watchdog.docker.out'
    $err = Join-Path $Root 'data\watchdog.docker.err'
    $p = Start-Process -FilePath 'docker' -ArgumentList $Arguments -WorkingDirectory (Join-Path $Root 'liquidsoap') -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $out -RedirectStandardError $err
    $null = $p.Handle   # без цього ExitCode лишається порожнім
    if (-not $p.WaitForExit($TimeoutSec * 1000)) {
        try { $p.Kill() } catch { }
        return [pscustomobject]@{ Ok = $false; Out = "docker $($Arguments -join ' ') не відповів за $TimeoutSec с" }
    }
    $text = ((Get-Content $out -Raw -ErrorAction SilentlyContinue) + (Get-Content $err -Raw -ErrorAction SilentlyContinue))
    return [pscustomobject]@{ Ok = ($p.ExitCode -eq 0); Out = "$text".Trim() }
}

function Get-RadioSupervisor {
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe' OR Name='pwsh.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like '*radio.ps1*run*' } | Select-Object -First 1
}

# Docker Desktop та Icecast на проді стереже наглядач D:\radio (radio.ps1 run, щопівхвилини) — наша справа, щоб він сам був живий.
# liquidsoap стережемо самі: контейнер не працює — compose up; працює, але /radio.mp3 в Icecast нема 5 хв — docker restart.
function Watch-Radio($st, [long]$now) {
    if (Test-Path $OwnerIcecast) {
        if (Get-RadioSupervisor) { $st.radioMisses = 0 }
        else {
            $st.radioMisses++
            # після входу у Windows його запускає SpotifyRadio.vbs з автозавантаження: дамо кілька хвилин, щоб не було двох наглядачів
            if ($st.radioMisses -ge 3) {
                Write-Watch 'наглядач D:\radio (radio.ps1 run) не працює — запускаю'
                Start-Process -FilePath 'powershell' -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', "`"$OwnerIcecast`"", 'run' -WindowStyle Hidden
                $st.radioMisses = 0
            }
        }
    }

    $status = try { (Invoke-WebRequest -UseBasicParsing -TimeoutSec 5 'http://127.0.0.1:8000/status-json.xsl').Content } catch { $null }
    if ($null -eq $status) {
        $st.iceMisses++
        $st.mountMisses = 0
        if (Test-Path $OwnerIcecast) {
            if ($st.iceMisses -eq 5) { Write-Watch 'Icecast не відповідає вже 5 хв; його піднімає D:\radio\radio.ps1 run, дивись D:\radio\logs\launcher.log' }
        }
        elseif ($st.iceMisses -ge 2) {   # копія для розробки: Icecast у docker-compose.dev.yml разом із liquidsoap
            Write-Watch 'Icecast не відповідає — docker compose up -d'
            $r = Invoke-Docker (@('compose') + $ComposeArgs + @('up', '-d'))
            if (-not $r.Ok) { Write-Watch "  не вийшло: $($r.Out)" }
        }
        return
    }
    $st.iceMisses = 0
    if ($status -match 'listenurl"\s*:\s*"[^"]*/radio\.mp3"') { $st.mountMisses = 0; return }

    $st.mountMisses++
    if ($st.mountMisses -lt 2) { return }   # хвилинна дірка буває, коли liquidsoap сам перепідключається
    $r = Invoke-Docker @('ps', '-q', '--filter', 'name=^hlechyky-liq$', '--filter', 'status=running')
    if (-not $r.Ok) { Write-Watch "liquidsoap: docker не відповідає ($($r.Out))"; return }
    if (-not $r.Out) {
        Write-Watch 'liquidsoap не працює — docker compose up -d'
        $r = Invoke-Docker (@('compose') + $ComposeArgs + @('up', '-d'))
        if (-not $r.Ok) { Write-Watch "  не вийшло: $($r.Out)" }
        return
    }
    if ($st.mountMisses -ge 5 -and $now - [long]$st.liqRestartAt -ge 900) {
        Write-Watch "liquidsoap працює, але /radio.mp3 в Icecast нема вже $($st.mountMisses) хв — docker restart hlechyky-liq"
        $r = Invoke-Docker @('restart', 'hlechyky-liq')
        if (-not $r.Ok) { Write-Watch "  не вийшло: $($r.Out)" }
        $st.liqRestartAt = $now
        $st.mountMisses = 0
    }
}

function Watch-Server($st, [long]$now) {
    $p = Get-Server
    if (-not $p) {
        $p = Find-Listener 8080 'dotnet'
        if ($p) { Set-Content $PidFile $p.Id; Write-Watch "сервер працював без data\server.pid (pid $($p.Id)) — підхопив" }
    }
    if ($p) {
        try { Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 'http://127.0.0.1:8080/api/me' | Out-Null; $st.httpFails = 0; return }
        catch { $st.httpFails++ }
        if ($st.httpFails -lt 3) { return }
    }

    # Падає раз у раз — не молотимо щохвилини: після 5 запусків за пів години наступна спроба не раніше ніж за 10 хв
    $recent = @(@($st.serverStarts) | Where-Object { $now - [long]$_ -lt 1800 })
    $last = if ($recent.Count) { [long]($recent | Measure-Object -Maximum).Maximum } else { 0 }
    if ($recent.Count -ge 5 -and $now - $last -lt 600) { return }

    if ($p) {
        Write-Watch "сервер (pid $($p.Id)) живий, але $($st.httpFails) хв не відповідає на /api/me — перезапускаю"
        Stop-Server | Out-Null
    }
    else { Write-Watch 'сервер не працює — запускаю (попередній лог: logs\server.prev.log)' }
    $st.httpFails = 0
    Start-Server | Out-Null
    $st.serverStarts = @($recent) + $now
    if ($st.serverStarts.Count -ge 5) { Write-Watch "  сервер запускався $($st.serverStarts.Count) разів за пів години — далі пробую раз на 10 хв, дивись logs\server.prev.log" }
}

function Watch-Caddy {
    if (-not (Test-Path $Caddy)) { return }
    if (Get-Caddy) { return }
    $p = Find-Listener 443 'caddy'
    if ($p) { Set-Content $CaddyPidFile $p.Id; Write-Watch "Caddy працював без data\caddy.pid (pid $($p.Id)) — підхопив"; return }
    Write-Watch 'Caddy не працює — запускаю'
    Start-Caddy | Out-Null
}

function Invoke-Watchdog {
    New-Item -ItemType Directory -Force (Join-Path $Root 'logs'), (Join-Path $Root 'data') | Out-Null
    $st = Read-WatchState
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $st.lastRun = $now
    try {
        if (Test-Path $StopFlag) { return }      # зупинили руками (stop) — чекаємо start
        if (Test-DeployRunning) { return }       # deploy.ps1 сам перезапускає сервер і сам відкочується
        try { Watch-Radio $st $now } catch { Write-Watch "помилка в перевірці Icecast/liquidsoap: $_" }
        try { Watch-Server $st $now } catch { Write-Watch "помилка в перевірці сервера: $_" }
        try { Watch-Caddy } catch { Write-Watch "помилка в перевірці Caddy: $_" }
    }
    finally { $st | ConvertTo-Json -Compress | Set-Content $WatchState -Encoding ASCII }
}

# ---------- команди ----------

$locked = $false
if ($Cmd -in 'start', 'stop', 'restart', 'watchdog') {
    $wait = if ($Cmd -eq 'watchdog') { 0 } else { 600 }
    $locked = Enter-Launcher $wait
    if (-not $locked) {
        if ($Cmd -eq 'watchdog') { exit 0 }   # попередня перевірка ще йде
        throw 'Інший start.ps1 (restart, деплой чи автонагляд) працює вже 10 хв, спробуй пізніше'
    }
}

try {
    switch ($Cmd) {
        'build'   { Invoke-Build }
        'start'   { Remove-Item $StopFlag -ErrorAction SilentlyContinue; Start-Liquidsoap; Start-Server; Start-Caddy }
        'stop'    {
            New-Item -ItemType Directory -Force (Join-Path $Root 'data') | Out-Null
            Set-Content $StopFlag (Get-Date -Format 's')
            Stop-Caddy; Stop-Server; Push-Location (Join-Path $Root 'liquidsoap'); try { docker compose @ComposeArgs stop } finally { Pop-Location }
            Write-Host 'Автонагляд на паузі, доки не буде start'
        }
        'restart' { Remove-Item $StopFlag -ErrorAction SilentlyContinue; Stop-Server; Invoke-Build; Start-Liquidsoap; Start-Server; Start-Caddy }
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
            $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
            $last = [long](Read-WatchState).lastRun
            $watch = if (-not $task) { 'не встановлено (start.ps1 autostart)' }
                elseif ($task.State -eq 'Disabled') { 'завдання в Планувальнику вимкнене' }
                elseif (Test-Path $StopFlag) { 'на паузі після stop (start знімає)' }
                elseif ($last) { "працює, остання перевірка $([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() - $last) с тому, лог: logs\watchdog.log" }
                else { 'встановлено, ще не перевіряв' }
            Write-Host ("Нагляд:     " + $watch)
        }
        'logs'      { Get-Content $Log -Tail 40 }
        'autostart' { Install-Autostart }
        'watchdog'  { Invoke-Watchdog }
    }
}
finally { if ($locked) { $script:mutex.ReleaseMutex() } }

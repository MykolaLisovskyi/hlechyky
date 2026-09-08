<#
.SYNOPSIS
  Глечики — перший запуск після git clone. Качає yt-dlp і ffmpeg у tools\, створює appsettings.Local.json і liquidsoap\.env
  з випадковими ключами (ті два файли в .gitignore). Запускати можна скільки завгодно: те, що вже є, не чіпає.

  powershell -ExecutionPolicy Bypass -File setup.ps1
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # без прогрес-бару Invoke-WebRequest у PowerShell 5 качає в рази швидше
$Root = $PSScriptRoot

function New-Key([int]$Bytes) {
    $b = New-Object byte[] $Bytes
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
    ($b | ForEach-Object { $_.ToString('x2') }) -join ''
}

# 1. Інструменти: yt-dlp.exe, ffmpeg.exe, ffprobe.exe у tools\yt-dlp\
$yt = Join-Path $Root 'tools\yt-dlp'
New-Item -ItemType Directory -Force $yt | Out-Null
if (Test-Path (Join-Path $yt 'yt-dlp.exe')) { Write-Host 'yt-dlp.exe вже є' }
else {
    Write-Host 'Качаю yt-dlp.exe…'
    Invoke-WebRequest -UseBasicParsing 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe' -OutFile (Join-Path $yt 'yt-dlp.exe')
}
if ((Test-Path (Join-Path $yt 'ffmpeg.exe')) -and (Test-Path (Join-Path $yt 'ffprobe.exe'))) { Write-Host 'ffmpeg.exe і ffprobe.exe вже є' }
else {
    Write-Host 'Качаю ffmpeg (збірка yt-dlp, ~100 МБ)…'
    $zip = Join-Path $env:TEMP 'hlechyky-ffmpeg.zip'
    $tmp = Join-Path $env:TEMP 'hlechyky-ffmpeg'
    Invoke-WebRequest -UseBasicParsing 'https://github.com/yt-dlp/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip' -OutFile $zip
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    Expand-Archive $zip $tmp
    Get-ChildItem $tmp -Recurse -Include ffmpeg.exe, ffprobe.exe | Copy-Item -Destination $yt
    Remove-Item $zip -Force; Remove-Item $tmp -Recurse -Force
}

# 2. Секрети: appsettings.Local.json і liquidsoap\.env. Ключ liquidsoap-callback має збігатися в обох файлах.
$local = Join-Path $Root 'appsettings.Local.json'
$envFile = Join-Path $Root 'liquidsoap\.env'
$liqKey = $null
if (Test-Path $local) {
    Write-Host 'appsettings.Local.json вже є'
    $liqKey = (Get-Content $local -Raw | ConvertFrom-Json).Liquidsoap.ApiKey
}
else {
    $liqKey = New-Key 24
    $admin = New-Key 18
    (Get-Content (Join-Path $Root 'appsettings.Local.example.json') -Raw) `
        -replace '<AdminKey>', $admin -replace '<LiquidsoapApiKey>', $liqKey |
        Set-Content $local -Encoding UTF8 -NoNewline
    Write-Host "Створив appsettings.Local.json. Адмінка: http://localhost:8080/?k=$admin"
}
if (Test-Path $envFile) { Write-Host 'liquidsoap\.env вже є' }
else {
    if (-not $liqKey) { $liqKey = New-Key 24; Write-Warning "У appsettings.Local.json порожній Liquidsoap:ApiKey; впиши туди $liqKey" }
    (Get-Content (Join-Path $Root 'liquidsoap\.env.example') -Raw) `
        -replace '<IcecastSourcePassword>', (New-Key 12) -replace '<LiquidsoapApiKey>', $liqKey |
        Set-Content $envFile -Encoding ASCII -NoNewline
    Write-Host 'Створив liquidsoap\.env'
}

Write-Host ''
Write-Host 'Готово. Далі:'
Write-Host '  docker compose -f liquidsoap\docker-compose.dev.yml up -d   # Icecast + liquidsoap (необов''язково)'
Write-Host '  dotnet run --project src\Hlechyky                             # сайт на http://localhost:8080'

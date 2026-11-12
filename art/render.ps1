# Draws art/generate.html in headless Edge and saves each scene as art/photos/<language>/<name>.png.
# Usage: powershell -File art\render.ps1 [-Langs en,es,vi] [-From 1] [-Sheet <png>]
param([string[]]$Langs = @('en', 'es', 'vi'), [int]$From = 1, [string]$Sheet)
Add-Type -AssemblyName System.Drawing
$project = Split-Path (Split-Path $MyInvocation.MyCommand.Path)
$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
$page = ([Uri](Join-Path $project 'art\generate.html')).AbsoluteUri
$profile = Join-Path $env:TEMP 'standup-render-profile'
$names = 'dog01', 'dog02', 'dog03', 'dog04', 'dog05', 'dog06', 'baby01', 'baby02', 'baby03', 'baby04', 'babydog01', 'babydog02',
         'cat01', 'cat02', 'cat03', 'cat04', 'cat05', 'babycat01', 'baby05', 'baby06', 'dog07', 'dog08', 'hamster01', 'penguin01'

foreach ($lang in $Langs) {
    $dir = Join-Path $project "art\photos\$lang"
    New-Item -ItemType Directory -Force $dir | Out-Null
    for ($i = $From; $i -le $names.Count; $i++) {
        $out = Join-Path $dir ($names[$i - 1] + '.png')
        $p = Start-Process -FilePath $edge -PassThru -WindowStyle Hidden -ArgumentList @(
            '--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run',
            "--user-data-dir=`"$profile`"", '--force-device-scale-factor=1',
            '--window-size=1200,900', "--screenshot=`"$out`"", "`"$page#$i-$lang`"")
        if (-not $p.WaitForExit(30000)) { $p.Kill(); "timeout $lang $i" }
    }

    if ($Sheet) {
        # A contact sheet of what was drawn, 4 across, for checking by eye.
        $count = $names.Count - $From + 1
        $rows = [Math]::Ceiling($count / 4)
        $bmp = New-Object System.Drawing.Bitmap 1600, (300 * $rows)
        $g = [System.Drawing.Graphics]::FromImage($bmp); $g.Clear([System.Drawing.Color]::White)
        for ($k = 0; $k -lt $count; $k++) {
            $img = [System.Drawing.Image]::FromFile((Join-Path $dir ($names[$From - 1 + $k] + '.png')))
            $g.DrawImage($img, ($k % 4) * 400, [Math]::Floor($k / 4) * 300, 396, 297); $img.Dispose()
        }
        $bmp.Save(($Sheet -replace '\.png$', "-$lang.png")); $g.Dispose(); $bmp.Dispose()
    }
    "$lang : $((Get-ChildItem $dir *.png).Count) pictures"
}

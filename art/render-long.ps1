# Draws the long-break pictures in art/generate.html with headless Edge and saves them as
# art/long/<language>/<name>.png. They are a separate set from the short-break pictures.
# Usage: powershell -File art\render-long.ps1 [-Langs en] [-Sheet <png>]   (run once per language)
param([string[]]$Langs = @('en', 'es', 'vi'), [string]$Sheet)
Add-Type -AssemblyName System.Drawing
$project = Split-Path (Split-Path $MyInvocation.MyCommand.Path)
$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
$page = ([Uri](Join-Path $project 'art\generate.html')).AbsoluteUri
$profile = Join-Path $env:TEMP 'standup-render-profile'
$names = 'jumping-jacks', 'push-ups', 'side-stretch', 'jogging', 'barbell', 'squats'

foreach ($lang in $Langs) {
    $dir = Join-Path $project "art\long\$lang"
    New-Item -ItemType Directory -Force $dir | Out-Null
    Get-ChildItem $dir -Filter *.gif | Remove-Item   # the earlier animated versions
    for ($n = 1; $n -le $names.Count; $n++) {
        $out = Join-Path $dir ($names[$n - 1] + '.png')
        $p = Start-Process -FilePath $edge -PassThru -WindowStyle Hidden -ArgumentList @(
            '--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run',
            "--user-data-dir=`"$profile`"", '--force-device-scale-factor=1',
            '--window-size=1200,900', "--screenshot=`"$out`"", "`"$page#L$n-$lang`"")
        if (-not $p.WaitForExit(30000)) { $p.Kill(); "timeout $lang L$n" }
    }

    if ($Sheet) {
        # All six, three across, for checking by eye.
        $bmp = New-Object System.Drawing.Bitmap 1200, 600
        $g = [System.Drawing.Graphics]::FromImage($bmp); $g.Clear([System.Drawing.Color]::White)
        for ($k = 0; $k -lt $names.Count; $k++) {
            $img = [System.Drawing.Image]::FromFile((Join-Path $dir ($names[$k] + '.png')))
            $g.DrawImage($img, ($k % 3) * 400, [Math]::Floor($k / 3) * 300, 396, 297); $img.Dispose()
        }
        $bmp.Save(($Sheet -replace '\.png$', "-$lang.png")); $g.Dispose(); $bmp.Dispose()
    }
    "$lang : $((Get-ChildItem $dir *.png).Count) long-break pictures, $([Math]::Round((Get-ChildItem $dir *.png | Measure-Object Length -Sum).Sum / 1KB)) KB"
}

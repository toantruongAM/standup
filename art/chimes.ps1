# Soft bell chimes: rising notes when a break starts, falling notes when it ends.
# Long breaks have their own pair, in a different key, with four slower notes.
# Usage: powershell -File art\chimes.ps1 (writes art\sounds\*.wav)
param([string]$OutDir = (Join-Path (Split-Path $MyInvocation.MyCommand.Path) 'sounds'))
New-Item -ItemType Directory -Force $OutDir | Out-Null
$rate = 44100

function Write-Chime([string]$path, [double[]]$notes, [double]$gap = 0.16, [double]$ring = 1.1) {
    $total = [int](($gap * ($notes.Count - 1) + $ring) * $rate)
    $samples = New-Object double[] $total
    for ($n = 0; $n -lt $notes.Count; $n++) {
        $start = [int]($n * $gap * $rate); $f = $notes[$n]
        for ($i = 0; $i -lt [int]($ring * $rate) -and $start + $i -lt $total; $i++) {
            $t = $i / $rate
            $attack = [Math]::Min(1.0, $t / 0.006)
            $env = $attack * [Math]::Exp(-3.6 * $t)
            # A bell is mostly the fundamental with a little of the octave and a slightly sharp overtone.
            $v = [Math]::Sin(2 * [Math]::PI * $f * $t) + 0.32 * [Math]::Sin(2 * [Math]::PI * 2 * $f * $t) * [Math]::Exp(-2 * $t) + 0.12 * [Math]::Sin(2 * [Math]::PI * 2.76 * $f * $t) * [Math]::Exp(-5 * $t)
            $samples[$start + $i] += 0.24 * $env * $v
        }
    }
    $fade = [int](0.05 * $rate)
    for ($i = 0; $i -lt $fade; $i++) { $samples[$total - 1 - $i] *= $i / $fade }

    $data = New-Object byte[] ($total * 2)
    for ($i = 0; $i -lt $total; $i++) {
        $s = [int16][Math]::Max(-32767, [Math]::Min(32767, [Math]::Round($samples[$i] * 32767)))
        $b = [BitConverter]::GetBytes($s); $data[2*$i] = $b[0]; $data[2*$i+1] = $b[1]
    }
    $ms = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $ms
    $w.Write([Text.Encoding]::ASCII.GetBytes('RIFF')); $w.Write([int](36 + $data.Length)); $w.Write([Text.Encoding]::ASCII.GetBytes('WAVE'))
    $w.Write([Text.Encoding]::ASCII.GetBytes('fmt ')); $w.Write([int]16); $w.Write([int16]1); $w.Write([int16]1); $w.Write([int]$rate); $w.Write([int]($rate * 2)); $w.Write([int16]2); $w.Write([int16]16)
    $w.Write([Text.Encoding]::ASCII.GetBytes('data')); $w.Write([int]$data.Length); $w.Write($data)
    [IO.File]::WriteAllBytes($path, $ms.ToArray())
    $peak = ($samples | Measure-Object -Maximum).Maximum
    "{0}: {1:N2} s, peak {2:N2}, {3:N0} bytes" -f (Split-Path $path -Leaf), ($total / $rate), $peak, $ms.Length
}

# C5 E5 G5 up; G5 E5 C5 down.
Write-Chime (Join-Path $OutDir 'break-start.wav') @(523.25, 659.25, 783.99)
Write-Chime (Join-Path $OutDir 'break-end.wav') @(783.99, 659.25, 523.25)
# Long breaks: A4 C#5 E5 A5 up; A5 E5 C#5 A4 down, a little slower and longer ringing.
Write-Chime (Join-Path $OutDir 'long-break-start.wav') @(440.00, 554.37, 659.25, 880.00) 0.22 1.5
Write-Chime (Join-Path $OutDir 'long-break-end.wav') @(880.00, 659.25, 554.37, 440.00) 0.22 1.5

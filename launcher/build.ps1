# Builds VIctoryLauncher.exe: one Windows executable that carries its UI, WebView2's
# loader and a diagnostics victory.exe (the build VIctory-7900XTX is played and measured
# with: some of its faster ways are only in it), compiled with the .NET Framework 4.8
# compiler that ships with Windows (no SDK needed).
#
#   cargo build --locked --profile diag -p victory --features diagnostics   (in the repository)
#   launcher\build.ps1 [-Victory <path to victory.exe>] [-VictoryRepo <its git checkout>]
#
# Output: launcher\build\VIctoryLauncher.exe
param(
    [string]$Victory = "$PSScriptRoot\..\target\diag\victory.exe",
    [string]$VictoryRepo = "$PSScriptRoot\..",
    [string]$Source = "https://github.com/God489/VIctory-Launcher"
)
$ErrorActionPreference = 'Stop'
$root  = $PSScriptRoot
$csc   = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$fx    = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wv2   = "$root\vendor\webview2"
$build = "$root\build"
$stage = "$build\payload"

if (-not (Test-Path $Victory)) { throw "victory.exe not found at $Victory (cargo build --locked --profile diag -p victory --features diagnostics)" }
New-Item -ItemType Directory -Force $build | Out-Null
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

# ---------- WebView2: Microsoft's package, fetched once (the repository holds no binaries) ----------
$wv2Version = '1.0.4370.26'
$wv2Files = [ordered]@{
    'lib/net462/Microsoft.Web.WebView2.Core.dll'     = '43319abb99f77a74cc7e05c987e8e3dc9029bfd95bb2bbb9eef61c4976998313'
    'lib/net462/Microsoft.Web.WebView2.WinForms.dll' = 'ad7fa76950f997368c17e8cc83cbaa2e75f8f9b4e66ad793ced7db82cb66caec'
    'runtimes/win-x64/native/WebView2Loader.dll'     = '37996d81f0f3a13e93549f74f722fce25d70b00e93e4a470c34b75d7231bfd1d'
    'LICENSE.txt'                                    = $null
}
$missing = $wv2Files.Keys | Where-Object { -not (Test-Path (Join-Path $wv2 (Split-Path $_ -Leaf))) }
if ($missing) {
    New-Item -ItemType Directory -Force $wv2 | Out-Null
    $pkg = "$build\microsoft.web.webview2.$wv2Version.nupkg"
    Write-Host "fetching  Microsoft.Web.WebView2 $wv2Version from nuget.org"
    Invoke-WebRequest -UseBasicParsing "https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2/$wv2Version" -OutFile $pkg
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($pkg)
    try {
        foreach ($name in $wv2Files.Keys) {
            $entry = $zip.Entries | Where-Object { $_.FullName -eq $name } | Select-Object -First 1
            if (-not $entry) { throw "the WebView2 package has no $name" }
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $wv2 (Split-Path $name -Leaf)), $true)
        }
    } finally { $zip.Dispose() }
}
foreach ($name in $wv2Files.Keys) {
    $want = $wv2Files[$name]
    if (-not $want) { continue }
    $got = (Get-FileHash (Join-Path $wv2 (Split-Path $name -Leaf)) -Algorithm SHA256).Hash.ToLower()
    if ($got -ne $want) { throw "$(Split-Path $name -Leaf) is not the one this launcher is built with ($got)" }
}

# ---------- icon: the launcher's mark, drawn at every size ----------
Add-Type -AssemblyName System.Drawing
$ico = "$build\launcher.ico"
function Draw-Mark([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $k = $s / 64.0
    $r = 16 * $k; $x = 2 * $k; $w = 60 * $k
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x, $x, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($x + $w - 2 * $r, $x, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($x + $w - 2 * $r, $x + $w - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc($x, $x + $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $p1 = New-Object System.Drawing.PointF 0, 0
    $p2 = New-Object System.Drawing.PointF $s, $s
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $p1, $p2, ([System.Drawing.Color]::FromArgb(255, 139, 108, 255)), ([System.Drawing.Color]::FromArgb(255, 25, 211, 255))
    $g.FillPath($grad, $path)
    $pts = @(@(15, 17), @(24.5, 17), @(32, 38.5), @(39.5, 17), @(49, 17), @(36.4, 49), @(27.6, 49)) |
        ForEach-Object { New-Object System.Drawing.PointF ($_[0] * $k), ($_[1] * $k) }
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 250, 251, 255))
    $g.FillPolygon($white, [System.Drawing.PointF[]]$pts)
    $g.Dispose()
    return $bmp
}
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = Draw-Mark $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}
$fs = [System.IO.File]::Create($ico)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = $pngs[$i]
    $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$d.Length); $bw.Write([uint32]$offset)
    $offset += $d.Length
}
foreach ($d in $pngs) { $bw.Write($d) }
$bw.Close()
(Draw-Mark 256).Save("$build\launcher.png", [System.Drawing.Imaging.ImageFormat]::Png)

# ---------- what the build carries ----------
function Read-GitValue([string[]]$a) { (& git.exe -C $VictoryRepo @a 2>$null | Select-Object -First 1) }
$info = [ordered]@{
    victoryCommit = Read-GitValue @('rev-parse', '--short=7', 'HEAD')
    baseCommit    = '0a43566'   # avatarmanz/VIctory-7900XTX, as taken
    fixCommit     = Read-GitValue @('log', '-1', '--format=%h', '--', 'crates/victory-shader/src/emit/console_wave_tests.rs')
    built         = (Get-Date -Format 'yyyy-MM-dd')
    source        = $Source
    victorySha256 = (Get-FileHash $Victory -Algorithm SHA256).Hash.ToLower()
}
$info | ConvertTo-Json | Set-Content -Encoding UTF8 "$stage\build-info.json"

Copy-Item $Victory "$stage\victory.exe"
Copy-Item "$wv2\Microsoft.Web.WebView2.Core.dll", "$wv2\Microsoft.Web.WebView2.WinForms.dll", "$wv2\WebView2Loader.dll" $stage
Copy-Item "$wv2\LICENSE.txt" "$stage\WebView2-LICENSE.txt"
$license = if (Test-Path "$root\LICENSE") { "$root\LICENSE" } else { "$root\..\LICENSE" }
Copy-Item $license "$stage\LICENSE.txt"
Copy-Item "$root\web" "$stage\web" -Recurse

$zip = "$build\payload.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, 'Optimal', $false)
$id = (Get-FileHash $zip -Algorithm SHA256).Hash.Substring(0, 16).ToLower()
[System.IO.File]::WriteAllText("$build\payload.id", $id)

# ---------- VIctoryLauncher.exe ----------
$refs = @(
    "/r:$fx\System.dll", "/r:$fx\System.Core.dll", "/r:$fx\System.Drawing.dll", "/r:$fx\System.Windows.Forms.dll",
    "/r:$fx\System.Web.Extensions.dll", "/r:$fx\System.Xml.dll",
    "/r:$fx\System.IO.Compression.dll", "/r:$fx\System.IO.Compression.FileSystem.dll",
    "/r:$wv2\Microsoft.Web.WebView2.Core.dll", "/r:$wv2\Microsoft.Web.WebView2.WinForms.dll"
)
$exe = "$build\VIctoryLauncher.exe"
$o = & $csc /nologo /target:winexe /platform:x64 /optimize+ /langversion:5 "/out:$exe" "/win32icon:$ico" `
    "/win32manifest:$root\src\app.manifest" "/resource:$zip,payload.zip" "/resource:$build\payload.id,payload.id" `
    @refs (Get-ChildItem "$root\src\*.cs" | ForEach-Object FullName)
if ($LASTEXITCODE -ne 0) { $o | Write-Host; throw "VIctoryLauncher.exe failed to compile" }
$o | Where-Object { $_ } | Write-Host
Write-Host ("built     {0}  ({1:N1} MB, payload {2})" -f $exe, ((Get-Item $exe).Length / 1MB), $id)

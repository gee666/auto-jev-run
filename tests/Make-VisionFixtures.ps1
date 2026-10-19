$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$fixtureRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'tests/vision-fixtures'
New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
foreach ($variant in @('red','blue','circle','square','success','error')) {
    $bitmap = [Drawing.Bitmap]::new(640, 384)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([Drawing.Color]::White)
    if ($variant -eq 'red' -or $variant -eq 'blue') {
        $brush = if ($variant -eq 'red') { [Drawing.Brushes]::Red } else { [Drawing.Brushes]::Blue }
        $graphics.FillRectangle($brush, 80, 60, 480, 264)
    } elseif ($variant -eq 'circle') {
        $graphics.FillEllipse([Drawing.Brushes]::DarkGreen, 205, 77, 230, 230)
    } elseif ($variant -eq 'square') {
        $graphics.FillRectangle([Drawing.Brushes]::DarkGreen, 205, 77, 230, 230)
    } else {
        $graphics.Clear([Drawing.Color]::FromArgb(20, 28, 38))
        $font = [Drawing.Font]::new('Consolas', 30, [Drawing.FontStyle]::Bold)
        $small = [Drawing.Font]::new('Consolas', 22)
        $graphics.DrawString('BUILD RESULT', $small, [Drawing.Brushes]::White, 35, 35)
        $message = if ($variant -eq 'success') { 'SUCCESS' } else { 'ERROR' }
        $brush = if ($variant -eq 'success') { [Drawing.Brushes]::LightGreen } else { [Drawing.Brushes]::OrangeRed }
        $graphics.DrawString($message, $font, $brush, 35, 130)
        $details = if ($variant -eq 'success') { '24 tests passed' } else { '3 tests failed' }
        $graphics.DrawString($details, $small, [Drawing.Brushes]::White, 35, 220)
        $font.Dispose(); $small.Dispose()
    }
    $bitmap.Save((Join-Path $fixtureRoot ($variant + '.png')), [Drawing.Imaging.ImageFormat]::Png)
    if ($variant -eq 'red') { $bitmap.Save((Join-Path $fixtureRoot 'red.jpg'), [Drawing.Imaging.ImageFormat]::Jpeg) }
    $graphics.Dispose(); $bitmap.Dispose()
}
Write-Output 'Created small synthetic color, shape and build-status images.'

$ffmpegPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'runtime/media/ffmpeg.exe'
& $ffmpegPath -hide_banner -loglevel error -y -i (Join-Path $fixtureRoot 'red.png') (Join-Path $fixtureRoot 'red.webp')
if ($LASTEXITCODE -ne 0) { throw 'WebP fixture generation failed.' }

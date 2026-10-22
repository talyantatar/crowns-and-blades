# rasterize_svg.ps1 - renders the white path of a game-icons.net SVG (512 x 512 viewBox) into a size x size alpha mask.
# Output: raw bytes, one alpha value per pixel, row by row. Used by make_icons.mjs (Windows PowerShell 5.1, WPF).
param(
    [Parameter(Mandatory = $true)][string]$Svg,
    [Parameter(Mandatory = $true)][int]$Size,
    [Parameter(Mandatory = $true)][string]$Out,
    [double]$Margin = 0.08
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase

$text = [IO.File]::ReadAllText($Svg)
# game-icons files: a black background path, then the icon (fill="#fff").
$m = [regex]::Match($text, '<path fill="#fff" d="([^"]+)"')
if (-not $m.Success) { throw "no white path in $Svg" }
$geometry = [System.Windows.Media.Geometry]::Parse('F1 ' + $m.Groups[1].Value).Clone()   # F1 = nonzero, the SVG default; Parse returns a frozen object

$scale = $Size * (1 - 2 * $Margin) / 512
$group = New-Object System.Windows.Media.TransformGroup
$group.Children.Add((New-Object System.Windows.Media.ScaleTransform($scale, $scale)))
$group.Children.Add((New-Object System.Windows.Media.TranslateTransform(($Size * $Margin), ($Size * $Margin))))
$geometry.Transform = $group

$visual = New-Object System.Windows.Media.DrawingVisual
$dc = $visual.RenderOpen()
$dc.DrawGeometry([System.Windows.Media.Brushes]::White, $null, $geometry)
$dc.Close()
$bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($Size, $Size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
$bmp.Render($visual)
$px = New-Object byte[] ($Size * $Size * 4)
$bmp.CopyPixels($px, $Size * 4, 0)
$alpha = New-Object byte[] ($Size * $Size)
for ($i = 0; $i -lt $alpha.Length; $i++) { $alpha[$i] = $px[$i * 4 + 3] }
[IO.File]::WriteAllBytes($Out, $alpha)

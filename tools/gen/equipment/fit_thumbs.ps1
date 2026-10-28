# fit_thumbs.ps1 - fits Bannerlord item thumbnails (PNG with alpha, as the module's "thumbnails" command writes them) into square
# artifact icons for item_icons.mjs: the drawn part (alpha above a threshold) is cropped, scaled to fit Size minus a margin and
# centred. Output: <Out>\<name>.bin per PNG, Size x Size pixels, BGRA bytes row by row, straight (not premultiplied) alpha.
# Windows PowerShell 5.1 (System.Drawing).
param(
    [Parameter(Mandatory = $true)][string]$In,
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$Size = 128,
    [double]$Margin = 0.04
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $Out | Out-Null

foreach ($file in Get-ChildItem $In -Filter *.png) {
    $src = [System.Drawing.Bitmap]::FromFile($file.FullName)
    try {
        $rect = New-Object System.Drawing.Rectangle(0, 0, $src.Width, $src.Height)
        $data = $src.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $bytes = New-Object byte[] ($data.Stride * $src.Height)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
        $src.UnlockBits($data)
        $x0 = $src.Width; $y0 = $src.Height; $x1 = -1; $y1 = -1
        for ($y = 0; $y -lt $src.Height; $y++) {
            $row = $y * $data.Stride
            for ($x = 0; $x -lt $src.Width; $x++) {
                if ($bytes[$row + $x * 4 + 3] -gt 8) {
                    if ($x -lt $x0) { $x0 = $x }; if ($x -gt $x1) { $x1 = $x }
                    if ($y -lt $y0) { $y0 = $y }; if ($y -gt $y1) { $y1 = $y }
                }
            }
        }
        if ($x1 -lt 0) { $x0 = 0; $y0 = 0; $x1 = $src.Width - 1; $y1 = $src.Height - 1 }
        $w = $x1 - $x0 + 1; $h = $y1 - $y0 + 1
        $room = $Size * (1 - 2 * $Margin)
        $scale = [Math]::Min($room / $w, $room / $h)
        $dw = [int][Math]::Round($w * $scale); $dh = [int][Math]::Round($h * $scale)
        $dst = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($dst)
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $destRect = New-Object System.Drawing.Rectangle([int](($Size - $dw) / 2), [int](($Size - $dh) / 2), $dw, $dh)
        $g.DrawImage($src, $destRect, $x0, $y0, $w, $h, [System.Drawing.GraphicsUnit]::Pixel)
        $g.Dispose()
        $outData = $dst.LockBits((New-Object System.Drawing.Rectangle(0, 0, $Size, $Size)), [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $px = New-Object byte[] ($Size * $Size * 4)
        for ($y = 0; $y -lt $Size; $y++) {
            [System.Runtime.InteropServices.Marshal]::Copy([IntPtr]($outData.Scan0.ToInt64() + $y * $outData.Stride), $px, $y * $Size * 4, $Size * 4)
        }
        $dst.UnlockBits($outData)
        $dst.Dispose()
        [IO.File]::WriteAllBytes((Join-Path $Out ($file.BaseName + '.bin')), $px)
    }
    finally {
        $src.Dispose()
    }
}

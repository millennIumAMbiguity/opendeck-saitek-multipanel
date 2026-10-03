# Generates the plugin and action icons, plus docs/icon.png (512 px) for the plugin catalogue.
# Run after changing the drawing code; the output is committed.
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\plugin\io.github.millenniumambiguity.saitekmultipanel.sdPlugin\assets'
New-Item -ItemType Directory -Force $out | Out-Null

foreach ($size in 72, 144, 512) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::FromArgb(24, 24, 28))
    $s = $size / 72.0

    # LCD window with red digits, like the real panel.
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(10, 10, 10))), 8 * $s, 10 * $s, 56 * $s, 26 * $s)
    $font = New-Object System.Drawing.Font 'Consolas', (14 * $s), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $g.DrawString('12500', $font, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 60, 40))), 11 * $s, 13 * $s)

    # Row of AP buttons, two lit.
    for ($i = 0; $i -lt 5; $i++) {
        $color = if ($i -eq 0 -or $i -eq 3) { [System.Drawing.Color]::FromArgb(80, 220, 90) } else { [System.Drawing.Color]::FromArgb(90, 90, 96) }
        $g.FillRectangle((New-Object System.Drawing.SolidBrush $color), (8 + $i * 11.6) * $s, 46 * $s, 9 * $s, 6 * $s)
    }
    $g.Dispose()

    $path = switch ($size) {
        72 { Join-Path $out 'icon.png' }
        144 { Join-Path $out 'icon@2x.png' }
        default { New-Item -ItemType Directory -Force (Join-Path $PSScriptRoot '..\docs') | Out-Null; Join-Path $PSScriptRoot '..\docs\icon.png' }
    }
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# Counter action: dim knob outline so the value title stays readable on top.
foreach ($size in 72, 144) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::FromArgb(20, 20, 24))
    $s = $size / 72.0
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 70, 78)), (4 * $s)
    $g.DrawEllipse($pen, 10 * $s, 10 * $s, 52 * $s, 52 * $s)
    $font = New-Object System.Drawing.Font 'Segoe UI', (11 * $s), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(110, 110, 120))
    $g.DrawString('-', $font, $brush, 2 * $s, 54 * $s)
    $g.DrawString('+', $font, $brush, 58 * $s, 54 * $s)
    $g.Dispose()
    $name = if ($size -eq 72) { 'counter.png' } else { 'counter@2x.png' }
    $bmp.Save((Join-Path $out $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# System monitor action: small bar graph.
foreach ($size in 72, 144) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(20, 20, 24))
    $s = $size / 72.0
    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(70, 90, 78))
    $heights = 14, 26, 18, 34, 22
    for ($i = 0; $i -lt $heights.Count; $i++) {
        $h = $heights[$i]
        $g.FillRectangle($brush, (14 + $i * 9) * $s, (60 - $h) * $s, 6 * $s, $h * $s)
    }
    $g.Dispose()
    $name = if ($size -eq 72) { 'monitor.png' } else { 'monitor@2x.png' }
    $bmp.Save((Join-Path $out $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

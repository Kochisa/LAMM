# Generates the LAMM application icon: a macOS-style squircle with a black background and
# the white wordmark "LAMM".
#
#   src\LocalAIModelManager.App\Assets\LAMM.ico        multi-size icon used by the app,
#                                                      the taskbar, Explorer and the tray
#   docs\assets\lamm-icon-512.png                      large PNG for README / store art
#
# Shape: the corners are quarters of a superellipse (|x|^n + |y|^n = 1) joined to straight
# edges, not circular arcs. That is what makes the curvature ramp smoothly out of the edge
# the way macOS icons do, instead of snapping from an arc to a line. n = 5 with a corner
# radius of 0.32 of the tile reads as a Big Sur style icon.
#
# Everything is drawn from vector outlines (a polygon for the squircle, a GraphicsPath for
# the text) so each size is rendered natively instead of being downscaled.
#
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\make-icon.ps1
#
# NOTE: this file is intentionally ASCII-only. Windows PowerShell 5.1 parses .ps1 files
# as ANSI unless they carry a UTF-8 BOM, so keep all non-ASCII text out of here.

[CmdletBinding()]
param(
    [string]$RepositoryRoot = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# $PSScriptRoot is not reliably populated inside a param() default, so resolve it here.
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent $PSScriptRoot
}

# --- design parameters -------------------------------------------------------
$BackgroundColor = [System.Drawing.Color]::FromArgb(255, 0, 0, 0)
$ForegroundColor = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)
$Wordmark = 'LAMM'
$FontFamilyName = 'Segoe UI'
$CornerRadiusRatio = 0.32     # corner radius as a fraction of the tile edge
$CornerSmoothness = 5.0       # superellipse exponent; 2 = circular arc, 5 = macOS squircle
$WordmarkWidthRatio = 0.72    # ink width as a fraction of the tile edge

function New-SquirclePath {
    param(
        [single]$X,
        [single]$Y,
        [single]$Size,
        [single]$Radius,
        [double]$Exponent,
        [int]$Steps = 128
    )

    $half = $Size / 2
    $inner = $half - $Radius          # distance from the centre to where a corner starts
    $centreX = $X + $half
    $centreY = $Y + $half
    $power = 2.0 / $Exponent
    $points = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'

    # The boundary is walked clockwise from the point where the top edge meets the
    # top-right corner. Each corner is the superellipse quarter
    #   (offsetX/r)^n + (offsetY/r)^n = 1
    # parameterised as offsetX = r*sin(t)^(2/n), offsetY = r*cos(t)^(2/n).
    $corners = @(
        @{ Sx = 1;  Sy = 1;  Forward = $true },   # top-right:    (inner,  half) -> (half,  inner)
        @{ Sx = 1;  Sy = -1; Forward = $false },  # bottom-right: (half,  -inner) -> (inner, -half)
        @{ Sx = -1; Sy = -1; Forward = $true },   # bottom-left:  (-inner, -half) -> (-half, -inner)
        @{ Sx = -1; Sy = 1;  Forward = $false }   # top-left:     (-half,  inner) -> (-inner, half)
    )

    foreach ($corner in $corners) {
        for ($step = 0; $step -le $Steps; $step++) {
            $index = if ($corner.Forward) { $step } else { $Steps - $step }
            $t = [Math]::PI / 2 * $index / $Steps
            $offsetX = $Radius * [Math]::Pow([Math]::Sin($t), $power)
            $offsetY = $Radius * [Math]::Pow([Math]::Cos($t), $power)
            $points.Add((New-Object System.Drawing.PointF(
                ($centreX + $corner.Sx * ($inner + $offsetX)),
                ($centreY + $corner.Sy * ($inner + $offsetY)))))
        }
    }

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddPolygon($points.ToArray())
    return $path
}

function New-WordmarkPath {
    param([string]$Text, [string]$FamilyName, [single]$EmSize)

    # The local must not be called $family: PowerShell variable names are
    # case-insensitive, so that would alias the $FamilyName parameter and New-Object
    # would silently return the string instead of a FontFamily.
    $fontFamily = New-Object System.Drawing.FontFamily($FamilyName)
    try {
        $style = [int][System.Drawing.FontStyle]::Bold
        $format = [System.Drawing.StringFormat]::GenericTypographic
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $origin = New-Object System.Drawing.PointF(0, 0)
        $path.AddString($Text, $fontFamily, $style, $EmSize, $origin, $format)
        return $path
    }
    finally {
        $fontFamily.Dispose()
    }
}

# The wordmark is built once at a reference em size; every target size reuses it, scaled
# so the ink box - not the layout box, which carries ascent and descent padding - ends up
# exactly centred on the tile.
$referencePath = New-WordmarkPath -Text $Wordmark -FamilyName $FontFamilyName -EmSize 100
$referenceBounds = $referencePath.GetBounds()

function New-IconBitmap {
    param([int]$Size)

    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)

        # A hair of inset keeps the antialiased edge from being clipped by the bitmap
        # boundary at small sizes. The tile stays full-bleed: Windows taskbars expect
        # icons to fill their slot, unlike the macOS grid which reserves margin.
        $inset = [single]([Math]::Max(0.5, $Size * 0.005))
        $edge = [single]($Size - (2 * $inset))
        $background = New-SquirclePath -X $inset -Y $inset -Size $edge -Radius ([single]($edge * $CornerRadiusRatio)) -Exponent $CornerSmoothness
        try {
            $brush = New-Object System.Drawing.SolidBrush($BackgroundColor)
            try { $graphics.FillPath($brush, $background) } finally { $brush.Dispose() }
        }
        finally {
            $background.Dispose()
        }

        # Wordmark, scaled and centred by its ink box.
        $targetWidth = [single]($Size * $WordmarkWidthRatio)
        $scale = $targetWidth / [single]$referenceBounds.Width
        $targetHeight = [single]$referenceBounds.Height * $scale
        $offsetX = [single](($Size - $targetWidth) / 2)
        $offsetY = [single](($Size - $targetHeight) / 2)

        $wordmark = $referencePath.Clone()
        try {
            $matrix = New-Object System.Drawing.Drawing2D.Matrix(
                $scale, 0, 0, $scale,
                ($offsetX - ([single]$referenceBounds.X * $scale)),
                ($offsetY - ([single]$referenceBounds.Y * $scale)))
            try {
                $wordmark.Transform($matrix)
                $textBrush = New-Object System.Drawing.SolidBrush($ForegroundColor)
                try { $graphics.FillPath($textBrush, $wordmark) } finally { $textBrush.Dispose() }
            }
            finally {
                $matrix.Dispose()
            }
        }
        finally {
            $wordmark.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
    }

    return $bitmap
}

function ConvertTo-IcoDib {
    param([System.Drawing.Bitmap]$Bitmap)

    # A 32bpp BMP entry: BITMAPINFOHEADER, bottom-up BGRA pixels, then the 1bpp AND mask
    # (all zero - the alpha channel does the work). Small sizes use this instead of PNG
    # because every Windows shell version reads it without question.
    $width = $Bitmap.Width
    $height = $Bitmap.Height

    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)
    try {
        $writer.Write([uint32]40)
        $writer.Write([int32]$width)
        $writer.Write([int32]($height * 2))
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]0)
        $writer.Write([uint32]($width * $height * 4))
        $writer.Write([int32]0)
        $writer.Write([int32]0)
        $writer.Write([uint32]0)
        $writer.Write([uint32]0)

        for ($y = $height - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $width; $x++) {
                $pixel = $Bitmap.GetPixel($x, $y)
                $writer.Write([byte]$pixel.B)
                $writer.Write([byte]$pixel.G)
                $writer.Write([byte]$pixel.R)
                $writer.Write([byte]$pixel.A)
            }
        }

        $maskStride = [int]([Math]::Ceiling($width / 32.0) * 4)
        $mask = New-Object byte[] ($maskStride * $height)
        $writer.Write($mask, 0, $mask.Length)
        $writer.Flush()
        return $stream.ToArray()
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

function ConvertTo-IcoPng {
    param([System.Drawing.Bitmap]$Bitmap)

    $stream = New-Object System.IO.MemoryStream
    try {
        $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return $stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

function Write-IcoFile {
    param([string]$Path, [int[]]$Sizes)

    # Every frame is written as a 32bpp DIB, including 256x256. PNG-compressed frames are
    # smaller, but GDI+ - i.e. System.Drawing.Icon, which is what the tray icon and any
    # .NET consumer uses - cannot read them at all: it throws "Requested range extends past
    # the end of the array". DIB frames are read by GDI+, by the shell and by Explorer, so
    # one format everywhere beats a smaller file.
    $images = @()
    foreach ($size in $Sizes) {
        $bitmap = New-IconBitmap -Size $size
        try {
            $images += [pscustomobject]@{ Size = $size; Bytes = (ConvertTo-IcoDib -Bitmap $bitmap) }
        }
        finally {
            $bitmap.Dispose()
        }
    }

    $directory = Split-Path -Parent $Path
    if (-not (Test-Path $directory)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }

    $stream = [System.IO.File]::Create($Path)
    $writer = New-Object System.IO.BinaryWriter($stream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$images.Count)

        $offset = 6 + (16 * $images.Count)
        foreach ($image in $images) {
            $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$image.Bytes.Length)
            $writer.Write([uint32]$offset)
            $offset += $image.Bytes.Length
        }

        foreach ($image in $images) {
            $writer.Write($image.Bytes, 0, $image.Bytes.Length)
        }
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }

    return (Get-Item $Path).Length
}

# --- output ------------------------------------------------------------------
$iconSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$iconPath = Join-Path $RepositoryRoot 'src\LocalAIModelManager.App\Assets\LAMM.ico'
$pngPath = Join-Path $RepositoryRoot 'docs\assets\lamm-icon-512.png'

$iconBytes = Write-IcoFile -Path $iconPath -Sizes $iconSizes
Write-Host ("icon : {0} ({1} bytes, {2} sizes: {3})" -f $iconPath, $iconBytes, $iconSizes.Count, ($iconSizes -join ', '))

$pngDirectory = Split-Path -Parent $pngPath
if (-not (Test-Path $pngDirectory)) { New-Item -ItemType Directory -Force -Path $pngDirectory | Out-Null }
$large = New-IconBitmap -Size 512
try { $large.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png) } finally { $large.Dispose() }
Write-Host ("png  : {0} ({1} bytes)" -f $pngPath, (Get-Item $pngPath).Length)

$referencePath.Dispose()

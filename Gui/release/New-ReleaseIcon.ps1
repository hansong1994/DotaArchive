param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$OutputPath=[IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath)) | Out-Null
$images=New-Object 'Collections.Generic.List[byte[]]'
$sizes=@(16,32,48,64,128,256)
foreach($size in $sizes){
    $bitmap=New-Object Drawing.Bitmap($size,$size)
    $graphics=[Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([Drawing.Color]::Transparent)
    $scale=$size/64.0
    $graphics.ScaleTransform($scale,$scale)
    $background=New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(32,44,67))
    $accent=New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(87,221,185))
    $white=New-Object Drawing.Pen([Drawing.Color]::FromArgb(245,249,255),4)
    $path=New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc(2,2,20,20,180,90); $path.AddArc(42,2,20,20,270,90)
    $path.AddArc(42,42,20,20,0,90); $path.AddArc(2,42,20,20,90,90); $path.CloseFigure()
    $graphics.FillPath($background,$path)
    $graphics.DrawRectangle($white,15,25,34,25)
    $graphics.FillRectangle($background,27,23,10,10)
    $graphics.DrawLine($white,13,24,51,24)
    $graphics.DrawLine($white,15,24,18,16); $graphics.DrawLine($white,18,16,46,16); $graphics.DrawLine($white,46,16,49,24)
    $graphics.FillRectangle($accent,29,29,6,10)
    $graphics.FillPolygon($accent,[Drawing.PointF[]]@((New-Object Drawing.PointF(23,37)),(New-Object Drawing.PointF(41,37)),(New-Object Drawing.PointF(32,45))))
    $stream=New-Object IO.MemoryStream
    $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png); $images.Add($stream.ToArray())
    $stream.Dispose(); $path.Dispose(); $white.Dispose(); $accent.Dispose(); $background.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$file=[IO.File]::Create($OutputPath); $writer=New-Object IO.BinaryWriter($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset=6+16*$sizes.Count
    for($index=0;$index -lt $sizes.Count;$index++){
        $byteSize=if($sizes[$index] -eq 256){0}else{$sizes[$index]}
        $writer.Write([byte]$byteSize); $writer.Write([byte]$byteSize); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$images[$index].Length); $writer.Write([uint32]$offset)
        $offset+=$images[$index].Length
    }
    foreach($bytes in $images){$writer.Write($bytes)}
} finally {$writer.Dispose(); $file.Dispose()}
Write-Output $OutputPath

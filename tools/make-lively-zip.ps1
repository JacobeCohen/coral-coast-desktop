# Packs the wallpaper folder into CoralCoast-Lively.zip (next to the repo folder) for Lively's "Add wallpaper".
# Right-click this file > Run with PowerShell.
$repo = Split-Path -Parent $PSScriptRoot
$src  = Join-Path $repo 'wallpaper'
$out  = Join-Path (Split-Path -Parent $repo) 'CoralCoast-Lively.zip'
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out }
# LivelyInfo.json must sit at the top of the zip, so pack the folder's contents, not the folder
Compress-Archive -Path (Join-Path $src '*') -DestinationPath $out
Write-Host "Created $out"
Write-Host 'In Lively: + Add wallpaper, pick this zip, then apply Coral Coast in Span mode.'
Read-Host 'Press Enter to close'

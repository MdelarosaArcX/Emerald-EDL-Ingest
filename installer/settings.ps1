# ---------------------------------------------------------------------------
#  The installer's helper for Emerald's settings.json.
#
#    settings.ps1 -Drives                                   list the fixed drives and their room
#    settings.ps1 -Settings <file> -Read                    print the folders already set, name=value
#    settings.ps1 -Settings <file> -High X -Low Y -Data Z   set those folders - only where not set
#
#  Merges rather than writes: a reinstall over a working machine never overwrites a folder the
#  operator has already chosen, and never touches any other setting. Written whole or not at
#  all, through a temporary file, because Emerald reads this file while it runs.
# ---------------------------------------------------------------------------
param(
    [string]$Settings = "",
    [string]$High = "",
    [string]$Low = "",
    [string]$Capture = "",
    [string]$Data = "",
    [switch]$Read,
    [switch]$Drives
)

$ErrorActionPreference = 'Stop'

if ($Drives) {
    Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' |
        Sort-Object FreeSpace -Descending |
        ForEach-Object {
            '    {0}   {1,7:N0} GB free of {2,7:N0} GB   {3}' -f $_.DeviceID, ($_.FreeSpace / 1GB), ($_.Size / 1GB), $_.VolumeName
        }
    exit 0
}

if ($Settings -eq "") { Write-Error "-Settings is required"; exit 2 }

$json = $null
if (Test-Path -LiteralPath $Settings) {
    try { $json = Get-Content -Raw -LiteralPath $Settings | ConvertFrom-Json } catch { $json = $null }
}

if ($Read) {
    foreach ($name in 'captureFolder', 'highResFolder', 'lowResFolder', 'dataFolder') {
        $value = if ($json) { [string]$json.$name } else { "" }
        "$name=$value"
    }
    exit 0
}

# A settings file there but unreadable would be replaced by one holding only these folders, and
# every other choice the operator made would go with it. Better to leave it and say so.
if ((Test-Path -LiteralPath $Settings) -and -not $json) {
    Write-Error "$Settings could not be read; it was left alone."
    exit 1
}

if (-not $json) { $json = New-Object psobject }

function Set-IfMissing([string]$name, [string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) { return }

    $have = $json.PSObject.Properties[$name]
    if ($have -and -not [string]::IsNullOrWhiteSpace([string]$have.Value)) { return }

    if ($have) { $have.Value = $value }
    else { $json | Add-Member -NotePropertyName $name -NotePropertyValue $value }
}

Set-IfMissing 'captureFolder' $Capture
Set-IfMissing 'highResFolder' $High
Set-IfMissing 'lowResFolder' $Low
Set-IfMissing 'dataFolder' $Data

$folder = Split-Path -Parent $Settings
if (-not (Test-Path -LiteralPath $folder)) { New-Item -ItemType Directory -Force -Path $folder | Out-Null }

$text = $json | ConvertTo-Json -Depth 32
$temp = "$Settings.tmp"
[System.IO.File]::WriteAllText($temp, $text, (New-Object System.Text.UTF8Encoding $false))
Move-Item -LiteralPath $temp -Destination $Settings -Force
exit 0

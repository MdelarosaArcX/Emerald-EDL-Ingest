# ---------------------------------------------------------------------------
#  Makes Emerald-Setup.exe: the setup program with the published folder appended.
#
#    pack-setup.ps1 -Dist <published folder> -Stub <Emerald-Setup.exe as built> -Out <setup .exe>
#
#  The result is [stub][zip of the folder][8-byte offset of the zip]["EMSETUP1"], which is what
#  installer\setup\Payload.cs reads. Called by publish.bat.
# ---------------------------------------------------------------------------
param(
    [Parameter(Mandatory = $true)][string]$Dist,
    [Parameter(Mandatory = $true)][string]$Stub,
    [Parameter(Mandatory = $true)][string]$Out
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

# Distinct names throughout: PowerShell's variables ignore case, so a stream called $out
# would quietly replace the path in $Out.
$distPath = (Resolve-Path -LiteralPath $Dist).Path
$stubPath = (Resolve-Path -LiteralPath $Stub).Path
$setupPath = [System.IO.Path]::GetFullPath($Out)

if ($setupPath.StartsWith($distPath.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "The setup file cannot go inside the folder it packs: $setupPath"
}

$zipPath = "$setupPath.zip.tmp"
$partPath = "$setupPath.tmp"
foreach ($f in $zipPath, $partPath) { if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force } }

try {
    # Optimal: the runtime and ffmpeg compress to about 40 % of their size, and this runs once
    # per build, not once per install.
    [System.IO.Compression.ZipFile]::CreateFromDirectory($distPath, $zipPath,
        [System.IO.Compression.CompressionLevel]::Optimal, $false)

    # One handle, written start to finish. Copying the stub and then opening the copy again to
    # append loses the race with the virus scanner, which opens every new .exe as it is closed.
    $stream = New-Object System.IO.FileStream($partPath, [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    try {
        $source = [System.IO.File]::OpenRead($stubPath)
        try { $source.CopyTo($stream, 1MB) } finally { $source.Dispose() }

        $zipOffset = $stream.Length

        $source = [System.IO.File]::OpenRead($zipPath)
        try { $source.CopyTo($stream, 1MB) } finally { $source.Dispose() }

        # [byte[]], because PowerShell joins two byte arrays into an object array.
        [byte[]]$trailer = [BitConverter]::GetBytes([long]$zipOffset) + [System.Text.Encoding]::ASCII.GetBytes('EMSETUP1')
        $stream.Write($trailer, 0, $trailer.Length)
    } finally {
        $stream.Dispose()
    }

    Move-Item -LiteralPath $partPath -Destination $setupPath -Force
} finally {
    # Best effort: a failure above is the error worth seeing, not a leftover temporary file.
    foreach ($f in $zipPath, $partPath) {
        if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue }
    }
}

'{0} ({1:N0} MB)' -f $setupPath, ((Get-Item -LiteralPath $setupPath).Length / 1MB)

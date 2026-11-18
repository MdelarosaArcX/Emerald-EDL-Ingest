# ---------------------------------------------------------------------------
#  The installer's helper for the DELTACAST driver.
#
#    deltacast.ps1 -Source <folder>           install what is in <folder>, when it is newer
#    deltacast.ps1 -Source <folder> -Status   say what is installed and what is bundled; change nothing
#    deltacast.ps1 -Source <folder> -Collect  fill <folder> from THIS machine's DELTACAST (publish.bat)
#
#  <folder> is the installer's deltacast\ folder, filled by publish.bat from the machine it ran on:
#
#    deltacast\driver\     the signed driver package (delta-x300.inf, .sys, .cat), as Windows'
#                          driver store holds it
#    deltacast\system32\   VideoMasterHD*.dll, the library Emerald calls, which DELTACAST's own
#                          setup puts in System32 and the driver package does not carry
#
#  The two are one product and are kept together: installed together when the machine has no
#  DELTACAST or an older one, and left alone together when it has the same or newer. Mixing a
#  library from one release with a driver from another is how a card stops opening.
#
#  The driver is added to Windows' driver store with pnputil, so the order of fitting the card
#  and running this does not matter. A card already fitted gets the driver now. A card fitted
#  later - with the machine switched off, it is a PCIe card - gets it from Windows when the
#  machine next starts, with nothing to run.
#
#  Lines are printed in install.bat's own [ok] / [--] style. Exit 0 unless something failed;
#  3010 when Windows wants a restart before the card can be used.
# ---------------------------------------------------------------------------
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [switch]$Status,
    [switch]$Collect
)

$ErrorActionPreference = 'Stop'

function Version-Of([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $v = (Get-Item -LiteralPath $path).VersionInfo
    return New-Object System.Version $v.FileMajorPart, $v.FileMinorPart, $v.FileBuildPart, $v.FilePrivatePart
}

function Show([System.Version]$v) {
    if (-not $v) { return '' }
    if ($v.Revision -eq 0) { return '{0}.{1}.{2}' -f $v.Major, $v.Minor, $v.Build }
    return $v.ToString()
}

function Cards {
    # Present cards only: Windows remembers every card that was ever fitted.
    @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
        Where-Object { $_.InstanceId -like 'PCI\VEN_1B66*' })
}

# System32 as a 64-bit process sees it. Run from a 32-bit one, Windows would quietly send
# every write here to SysWOW64, where Emerald - 64-bit - would never look.
$system32 = Join-Path $env:SystemRoot 'System32'
if (-not [Environment]::Is64BitProcess) {
    $system32 = Join-Path $env:SystemRoot 'Sysnative'
}

if ($Collect) {
    # The library from System32 and the driver package from Windows' driver store - the copy
    # Windows itself installed from, signed. Only packages of the library's own version: the
    # store keeps every DELTACAST driver this machine has ever had.
    $dll = Join-Path $system32 'VideoMasterHD.dll'
    $want = Version-Of $dll
    if (-not $want) { 'This machine has no DELTACAST driver (no VideoMasterHD.dll in System32).'; exit 2 }

    $store = Join-Path $system32 'DriverStore\FileRepository'
    $packages = @(Get-ChildItem -LiteralPath $store -Directory -Filter 'delta*.inf_amd64_*' -ErrorAction SilentlyContinue |
        Where-Object {
            $sys = Get-ChildItem -LiteralPath $_.FullName -Filter '*.sys' | Select-Object -First 1
            $v = if ($sys) { Version-Of $sys.FullName } else { $null }
            $v -and $v.Major -eq $want.Major -and $v.Minor -eq $want.Minor -and $v.Build -eq $want.Build
        })

    if ($packages.Count -eq 0) {
        "No DELTACAST driver package of version $(Show $want) in $store."
        exit 2
    }

    if (Test-Path -LiteralPath $Source) { Remove-Item -LiteralPath $Source -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Join-Path $Source 'system32') | Out-Null

    Copy-Item -Path (Join-Path $system32 'VideoMasterHD*.dll') -Destination (Join-Path $Source 'system32')

    foreach ($package in $packages) {
        $into = Join-Path $Source "driver\$($package.Name)"
        New-Item -ItemType Directory -Force -Path $into | Out-Null
        Copy-Item -Path (Join-Path $package.FullName '*') -Destination $into -Recurse
    }

    "DELTACAST $(Show $want): $((Get-ChildItem (Join-Path $Source 'system32')).Count) libraries, " +
        "driver $(($packages | ForEach-Object { $_.Name.Split('_')[0] }) -join ', ')"
    exit 0
}

$bundledDll = Join-Path $Source 'system32\VideoMasterHD.dll'
$infs = @(Get-ChildItem -LiteralPath (Join-Path $Source 'driver') -Filter '*.inf' -Recurse -ErrorAction SilentlyContinue)

$bundled = Version-Of $bundledDll
$installed = Version-Of (Join-Path $system32 'VideoMasterHD.dll')

if ($Status) {
    "installed=$(Show $installed)"
    "bundled=$(Show $bundled)"
    "cards=$((Cards | ForEach-Object { $_.FriendlyName + ' (' + $_.Status + ')' }) -join '; ')"
    exit 0
}

if (-not $bundled -or $infs.Count -eq 0) {
    '  [--]   No DELTACAST driver in this installer; nothing installed.'
    exit 0
}

if ($installed -and $installed -ge $bundled) {
    "  [ok]   DELTACAST $(Show $installed) is already installed (this installer carries $(Show $bundled)); left as it is."
    exit 0
}

if ($installed) {
    "  Updating DELTACAST $(Show $installed) to $(Show $bundled) ..."
} else {
    "  Installing DELTACAST $(Show $bundled) ..."
}

# ------------------------------------------------------------------- the library
$failed = $false

foreach ($dll in Get-ChildItem -LiteralPath (Join-Path $Source 'system32') -Filter '*.dll') {
    try {
        Copy-Item -LiteralPath $dll.FullName -Destination (Join-Path $system32 $dll.Name) -Force
    } catch {
        $failed = $true
        "  [--]   Could not copy $($dll.Name) to System32: $($_.Exception.Message)"
        '         Something may be holding it open - another DELTACAST program. Close it and run the installer again.'
    }
}

if (-not $failed) { "  [ok]   VideoMasterHD.dll $(Show $bundled) and its companions copied to System32." }

# -------------------------------------------------------------------- the driver
#
# pnputil is in every Windows 10 and 11. /install puts the driver on any card fitted now;
# without a card it is still added to the driver store, which is what lets the card be
# fitted afterwards.
$pnputil = Join-Path $system32 'pnputil.exe'
$restart = $false

foreach ($inf in $infs) {
    $output = & $pnputil /add-driver "$($inf.FullName)" /install 2>&1
    $code = $LASTEXITCODE

    switch ($code) {
        0     { "  [ok]   DELTACAST driver $($inf.Name) added to Windows." }
        259   { "  [ok]   DELTACAST driver $($inf.Name) added to Windows (no fitted card needed it yet)." }
        3010  { $restart = $true; "  [ok]   DELTACAST driver $($inf.Name) added to Windows. Restart the machine before using the card." }
        default {
            $failed = $true
            "  [--]   Windows did not accept the DELTACAST driver $($inf.Name) (pnputil exit $code):"
            $output | Where-Object { "$_".Trim() } | Select-Object -Last 4 | ForEach-Object { "         $_" }
        }
    }
}

# --------------------------------------------------------------------- the card
$cards = Cards
if ($cards.Count -eq 0) {
    '  [--]   No DELTACAST card is fitted. Switch the machine off, fit the card, and start it:'
    '         Windows installs the driver for it by itself. Nothing needs running again.'
} else {
    foreach ($card in $cards) {
        if ($card.Status -eq 'OK') { "  [ok]   Card: $($card.FriendlyName)" }
        else { $restart = $true; "  [--]   Card: $($card.FriendlyName) is $($card.Status) - restart the machine." }
    }
}

if ($failed) { exit 1 }
if ($restart) { exit 3010 }
exit 0

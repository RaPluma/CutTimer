# Extracts CLIP STUDIO PAINT's application icon into Assets/csp.png.
#
# The icon is CELSYS's property, so it is deliberately NOT stored in this repo
# (see .gitignore). It is pulled from the locally installed CSP instead, which
# means it is only ever present on a machine that actually has CSP -- and it is
# still bundled into the published builds, because those are built here.
#
# Runs automatically before every build via the ExtractCspIcon target in
# CutTimer.csproj. Safe to run by hand too:
#
#     pwsh tools/extract-csp-icon.ps1
#
# Exits 0 whether or not it found CSP: a missing icon is a cosmetic loss, never
# a build failure. The app collapses the icon slot when the file is absent.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $here
$outFile = Join-Path $projectRoot 'Assets\csp.png'

# Prefer the usual install location; fall back to a bounded search so a
# non-standard install still works.
$candidates = New-Object System.Collections.Generic.List[string]
foreach ($root in @('C:\Program Files\CELSYS', 'D:\Program Files\CELSYS',
                    'C:\Program Files (x86)\CELSYS', "${env:ProgramFiles}\CELSYS")) {
    if ($root -and (Test-Path $root)) {
        Get-ChildItem $root -Recurse -Filter 'CLIPStudioPaint.exe' -ErrorAction SilentlyContinue |
            ForEach-Object { $candidates.Add($_.FullName) }
    }
}

if ($candidates.Count -eq 0) {
    Write-Output "  CSP not installed here -- skipping csp.png (app will hide the icon slot)"
    exit 0
}

$csp = $candidates[0]
Write-Output "  extracting icon from: $csp"

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class CutTimerIconExtract {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint PrivateExtractIcons(string file, int index, int cx, int cy,
        IntPtr[] phicon, uint[] piconid, uint nIcons, uint flags);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
}
"@

$handles = New-Object IntPtr[] 1
$ids = New-Object uint32[] 1
$n = [CutTimerIconExtract]::PrivateExtractIcons($csp, 0, 256, 256, $handles, $ids, 1, 0)

if ($n -lt 1 -or $handles[0] -eq [IntPtr]::Zero) {
    Write-Output "  no icon resource found -- skipping"
    exit 0
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outFile) | Out-Null

$icon = [System.Drawing.Icon]::FromHandle($handles[0])
$bmp = $icon.ToBitmap()
$bmp.Save($outFile, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose(); $icon.Dispose()
[void][CutTimerIconExtract]::DestroyIcon($handles[0])

$fi = Get-Item $outFile
Write-Output ("  wrote {0} ({1}x{2}, {3:N0} bytes)" -f $fi.Name, 256, 256, $fi.Length)

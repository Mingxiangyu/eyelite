<#
    which-version.ps1

    Identify which EyeLite build a given EyeLite.exe came from, without compiling
    or running it. Reads the PE file as raw bytes and looks for:

      - managed metadata identifiers (stored as UTF-8, plain ASCII names)
      - user string literals (stored as UTF-16LE)

    A missing identifier proves the build predates the commit that introduced it,
    so absences are the strong signal. Presences only place a lower bound.

    This file is intentionally pure ASCII (Chinese literals are rebuilt from
    code points) so Windows PowerShell 5.1 cannot mis-decode it.

    Usage:
        powershell -ExecutionPolicy Bypass -File tools\which-version.ps1 -Path EyeLite.exe
#>

param(
    [Parameter(Mandatory = $true)]
    [string] $Path
)

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Host "[ERROR] file not found: $Path"
    exit 2
}

$full = (Resolve-Path -LiteralPath $Path).Path
$data = [System.IO.File]::ReadAllBytes($full)
$info = Get-Item -LiteralPath $full

# PE header TimeDateStamp: written by the compiler, survives copying and touching.
# File mtime does not, so this is the better "when was it really built" signal.
function Get-PeBuildTime {
    param([byte[]] $D)
    if ($D.Length -lt 0x40) { return $null }
    $lfanew = [System.BitConverter]::ToInt32($D, 0x3C)
    if ($lfanew -le 0 -or ($lfanew + 12) -ge $D.Length) { return $null }
    if ($D[$lfanew] -ne 0x50 -or $D[$lfanew + 1] -ne 0x45) { return $null }
    $ts = [System.BitConverter]::ToUInt32($D, $lfanew + 8)
    if ($ts -eq 0) { return $null }
    $epoch = [System.DateTime]::SpecifyKind([System.DateTime]::Parse("1970-01-01"), [System.DateTimeKind]::Utc)
    return $epoch.AddSeconds($ts).ToLocalTime()
}

Write-Host ""
Write-Host "EyeLite build probe"
Write-Host "  file      : $full"
Write-Host ("  size      : {0} bytes" -f $info.Length)
Write-Host ("  mtime     : {0}  (can be altered by copying)" -f $info.LastWriteTime)
$peTime = Get-PeBuildTime $data
if ($null -ne $peTime) {
    Write-Host ("  built     : {0}  (PE header, baked at compile time)" -f $peTime)
} else {
    Write-Host "  built     : (not a PE file, or no timestamp)"
}
try {
    $md5 = (Get-FileHash -LiteralPath $full -Algorithm MD5).Hash.ToLower()
    Write-Host "  md5       : $md5"
} catch {
    Write-Host "  md5       : (Get-FileHash unavailable)"
}
Write-Host ""

# ---------------------------------------------------------------- helpers

function Test-Bytes {
    param([byte[]] $Haystack, [byte[]] $Needle)
    if ($null -eq $Needle -or $Needle.Length -eq 0) { return $false }
    $limit = $Haystack.Length - $Needle.Length
    $first = $Needle[0]
    for ($i = 0; $i -le $limit; $i++) {
        if ($Haystack[$i] -ne $first) { continue }
        $ok = $true
        for ($j = 1; $j -lt $Needle.Length; $j++) {
            if ($Haystack[$i + $j] -ne $Needle[$j]) { $ok = $false; break }
        }
        if ($ok) { return $true }
    }
    return $false
}

function Test-Ident {
    param([byte[]] $Data, [string] $Name)
    return Test-Bytes $Data ([System.Text.Encoding]::ASCII.GetBytes($Name))
}

# UTF-16LE literal from an ASCII string, e.g. " (Esc/"
function Get-LiteralPattern {
    param([string] $Ascii)
    $bytes = New-Object System.Collections.Generic.List[byte]
    foreach ($ch in $Ascii.ToCharArray()) {
        $bytes.Add([byte]([int]$ch -band 0xFF))
        $bytes.Add([byte](([int]$ch -shr 8) -band 0xFF))
    }
    return $bytes.ToArray()
}

# UTF-16LE literal built from code points, so this file stays ASCII
function Get-CodePointPattern {
    param([int[]] $Codes)
    $bytes = New-Object System.Collections.Generic.List[byte]
    foreach ($c in $Codes) {
        $bytes.Add([byte]($c -band 0xFF))
        $bytes.Add([byte](($c -shr 8) -band 0xFF))
    }
    return $bytes.ToArray()
}

function Show-Row {
    param([string] $Label, [bool] $Present, [string] $Meaning)
    $mark = "----"
    if ($Present) { $mark = "FOUND" }
    Write-Host ("  [{0}] {1,-26} {2}" -f $mark, $Label, $Meaning)
}

# ---------------------------------------------------------------- metadata

Write-Host "Managed identifiers:"

# Direction matters per row:
#   floor   - FOUND proves the build is AT LEAST that new; absence is the useful signal
#   ceiling - FOUND proves the build is OLDER than the removal; presence is the useful signal
$ids = @(
    @{ Name = "DetectScale";           Meaning = "DPI scaling (Sc())" },
    @{ Name = "SetBackdrop";           Meaning = "ring repaints parent background" },
    @{ Name = "trackPenPomo";          Meaning = "pomodoro colour set" },
    @{ Name = "ShowWithoutActivation"; Meaning = "mask never takes focus" },
    @{ Name = "UpdateStrictText";      Meaning = "dynamic strict-mode caption" },
    @{ Name = "RequestUninstall";      Meaning = "drain fix, keyup keepalive" },
    @{ Name = "drainVk";               Meaning = "drain fix state" },
    @{ Name = "IsDraining";            Meaning = "on-tick drain backstop" },
    @{ Name = "SetWindowPos";          Meaning = "re-assert topmost every tick" },
    @{ Name = "set_KeyPreview";        Meaning = "CEILING: KeyPreview assignment, removed after v1.1.1 tag" }
)

$seen = @{}
foreach ($id in $ids) {
    $hit = Test-Ident $data $id.Name
    $seen[$id.Name] = $hit
    Show-Row $id.Name $hit $id.Meaning
}

# ---------------------------------------------------------------- literals

Write-Host ""
Write-Host "UI literals (UTF-16LE):"

#  skipping button label, renamed in the 2xxx rewrite
$newSkip = Get-LiteralPattern " (Esc/"
#  old label: 4 CJK chars before the rename
$oldSkip = Get-CodePointPattern @(0x8DF3, 0x8FC7, 0x672C, 0x6B21)
#  dynamic strict-mode caption tail: "zhi lan xi tong jian"
$strictTail = Get-CodePointPattern @(0x53EA, 0x62E6, 0x7CFB, 0x7EDF, 0x952E)

$hasNewLabel = Test-Bytes $data $newSkip
$hasOldLabel = Test-Bytes $data $oldSkip
$hasStrictTail = Test-Bytes $data $strictTail

Show-Row "(Esc/ ... button label" $hasNewLabel "current skip button text"
Show-Row "old skip label (4 CJK)" $hasOldLabel "pre-rewrite skip button text"
Show-Row "strict caption tail"     $hasStrictTail "dynamic strict-mode caption"

# ---------------------------------------------------------------- verdict

Write-Host ""
Write-Host "Verdict:"

if (-not $seen["DetectScale"]) {
    Write-Host "  Very old build: predates DPI scaling. Not any v1.1.x release."
} elseif (-not $seen["RequestUninstall"] -or -not $seen["drainVk"]) {
    Write-Host "  Older than the v1.1.1 drain fix (skipped keys can leak as key-repeat"
    Write-Host "  into whatever window had focus). Must be rebuilt before testing v1.1.1."
} else {
    Write-Host "  Contains the drain fix."
    if ($seen["IsDraining"] -and $seen["SetWindowPos"]) {
        Write-Host "  Also has the on-tick backstop and the topmost re-assert: v1.1.1 class."
        if ($seen["set_KeyPreview"]) {
            Write-Host "  Still assigns KeyPreview -> matches tag v1.1.1 (0360834, 2300 lines)."
            Write-Host "  The post-tag cleanup (volatile, dead property removal) is NOT in this build."
        } else {
            Write-Host "  No KeyPreview assignment -> matches the post-tag cleanup"
            Write-Host "  (478fc5f, 2305 lines): volatile + removed dead property."
        }
    } else {
        Write-Host "  Missing the on-tick backstop and/or topmost re-assert: pre-v1.1.1 final."
    }
}

if (-not $seen["UpdateStrictText"]) {
    Write-Host "  Settings page still uses a static strict-mode caption."
}

Write-Host ""
Write-Host "Note: rows are directional. For floor rows a FOUND only proves the build is at"
Write-Host "      least that new, so absence is the signal. For ceiling rows (set_KeyPreview)"
Write-Host "      presence is the signal: it proves the build still has the removed line."
Write-Host "      Chinese needles must be built from code points, never typed by hand:"
Write-Host "      U+00B7 / U+2022 / U+30FB look identical on screen and silently miss."
Write-Host ""

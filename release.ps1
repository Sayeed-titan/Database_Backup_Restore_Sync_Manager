<#
.SYNOPSIS
  One command: test -> bump version -> publish -> installer, into installer\dist\<version>\.

.EXAMPLES
  .\release.ps1                          # patch release   3.0.0 -> 3.0.1
  .\release.ps1 -Bump minor              # new feature set 3.0.1 -> 3.1.0
  .\release.ps1 -Bump major              # big release     3.1.0 -> 4.0.0
  .\release.ps1 -Version 3.2.5           # exact version
  .\release.ps1 -NoBump                  # rebuild the current version (overwrites its folder)
  .\release.ps1 -Notes "Fixed X; added Y"   # goes into CHANGELOG.md + the version folder
  .\release.ps1 -SkipTests               # skip the unit tests (not recommended)

  The version lives in ONE place: <Version> in PgBackupManager.UI.csproj
  (the installer and the title-bar badge both read it).

  Output:
    installer\dist\3.0.1\PgBackupManager-Setup-3.0.1.exe
    installer\dist\3.0.1\SHA256.txt
    installer\dist\3.0.1\release-notes.md
    installer\dist\latest\...              (copy of the newest release)
#>
[CmdletBinding()]
param(
    [ValidateSet('patch', 'minor', 'major')][string]$Bump = 'patch',
    [string]$Version,
    [switch]$NoBump,
    [string]$Notes = '',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Step($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Fail($m) { Write-Host "`nRELEASE FAILED: $m" -ForegroundColor Red; exit 1 }

$csproj = Join-Path $PSScriptRoot 'PgBackupManager.UI\PgBackupManager.UI.csproj'
$iss    = Join-Path $PSScriptRoot 'installer\PgBackupManager.iss'
$dist   = Join-Path $PSScriptRoot 'installer\dist'

# --- find Inno Setup -------------------------------------------------------
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { Fail 'Inno Setup 6 not found (https://jrsoftware.org/isdl.php).' }

# --- decide the version ----------------------------------------------------
$text = [IO.File]::ReadAllText($csproj)
if ($text -notmatch '<Version>(\d+)\.(\d+)\.(\d+)</Version>') { Fail "No <Version>x.y.z</Version> in $csproj" }
$cur = [version]"$($Matches[1]).$($Matches[2]).$($Matches[3])"
if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { Fail "-Version must look like 3.1.0" }
    $new = $Version
} elseif ($NoBump) {
    $new = $cur.ToString()
} else {
    $new = switch ($Bump) {
        'major' { "$($cur.Major + 1).0.0" }
        'minor' { "$($cur.Major).$($cur.Minor + 1).0" }
        default { "$($cur.Major).$($cur.Minor).$($cur.Build + 1)" }
    }
}
Write-Host "Version: $cur -> $new" -ForegroundColor Yellow

$outDir = Join-Path $dist $new
if ((Test-Path $outDir) -and ($new -ne $cur.ToString()) -and -not $Version) { Fail "$outDir already exists." }

# --- tests -----------------------------------------------------------------
if (-not $SkipTests) {
    Step 'Unit tests'
    dotnet test PgBackupManager.Tests -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail 'Tests failed — nothing was changed or built.' }
}

# --- bump (only after tests pass) -----------------------------------------
if ($new -ne $cur.ToString()) {
    Step "Set version $new"
    [IO.File]::WriteAllText($csproj, ($text -replace '<Version>\d+\.\d+\.\d+</Version>', "<Version>$new</Version>"))
}

# --- publish ---------------------------------------------------------------
Step 'Publish (self-contained single file)'
$pub = Join-Path $PSScriptRoot 'PgBackupManager.UI\bin\Release\net8.0-windows\win-x64\publish'
if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
dotnet publish PgBackupManager.UI\PgBackupManager.UI.csproj -c Release -p:PublishProfile=win-x64 --nologo -v q
if ($LASTEXITCODE -ne 0) { Fail 'dotnet publish failed.' }
$exe = Join-Path $pub 'PgBackupManager.UI.exe'
if (-not (Test-Path $exe)) { Fail "Published exe missing: $exe" }
$fileVer = (Get-Item $exe).VersionInfo.ProductVersion
if ($fileVer -notlike "$new*") { Fail "Published exe says version '$fileVer', expected $new." }

# --- installer -------------------------------------------------------------
Step 'Installer'
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Path $outDir | Out-Null
& $iscc /Q "/DMyAppVersion=$new" "/O$outDir" $iss
if ($LASTEXITCODE -ne 0) { Fail 'Inno Setup failed.' }
$setup = Join-Path $outDir "PgBackupManager-Setup-$new.exe"
if (-not (Test-Path $setup)) { Fail "Installer missing: $setup" }

# --- release folder contents ----------------------------------------------
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash
"$hash  $(Split-Path $setup -Leaf)" | Set-Content (Join-Path $outDir 'SHA256.txt') -Encoding ascii
$when = Get-Date -Format 'yyyy-MM-dd HH:mm'
$commit = (git rev-parse --short HEAD 2>$null)
$dirty = if (git status --porcelain 2>$null) { ' (+ uncommitted changes)' } else { '' }
@"
# PgBackupManager $new

- Built: $when
- Git: $commit$dirty
- Installer: PgBackupManager-Setup-$new.exe ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)
- SHA256: $hash

$(if ($Notes) { "## Notes`n`n$Notes" } else { '' })
"@ | Set-Content (Join-Path $outDir 'release-notes.md') -Encoding utf8

# CHANGELOG.md (newest first)
$cl = Join-Path $PSScriptRoot 'CHANGELOG.md'
$entry = "## $new — $(Get-Date -Format 'yyyy-MM-dd')`n`n" + $(if ($Notes) { "$Notes`n`n" } else { "(no notes)`n`n" })
if (Test-Path $cl) {
    $old = [IO.File]::ReadAllText($cl) -replace '^# Changelog\s*', ''
    [IO.File]::WriteAllText($cl, "# Changelog`n`n$entry$old")
} else {
    [IO.File]::WriteAllText($cl, "# Changelog`n`n$entry")
}

# latest\ = copy of the newest release
$latest = Join-Path $dist 'latest'
if (Test-Path $latest) { Remove-Item $latest -Recurse -Force }
Copy-Item $outDir $latest -Recurse

Write-Host "`nDONE  $new" -ForegroundColor Green
Write-Host "  $setup"
Write-Host "  SHA256 $hash"
Write-Host "  (git: nothing committed — commit the version bump + CHANGELOG when you're happy)"

param(
    [Parameter(Mandatory = $true)]
    [string]$FfmpegDirectory
)

$ErrorActionPreference = "Stop"

# The mod ships the gyan.dev "full" build: it adds libsvtav1 (usable AV1 on CPUs and
# pre-RTX-40 GPUs) and libvvenc (experimental H.266/VVC) to the essentials build.
$ffmpegVersion = "9.0.2"
$ffmpegBuildVariant = "full_build"
$ffmpegPackageName = "ffmpeg-$ffmpegVersion-$ffmpegBuildVariant"
$archiveName = "ffmpeg-9.0.2-full_build.7z"
# Pin the exact GitHub release: gyan.dev's ffmpeg-release-full.7z always points at the newest
# build, which would stop matching the SHA-256 below as soon as 9.0.3 is published.
$url = "https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/$archiveName"
$expectedSha256 = "f0e46253c70dfe902bac915dfb4224f0cf1b7c6eeab9da2ccc9a5581f9a71b13"
$sourceCommit = "946fcce07b"
$sourceUrl = "https://github.com/FFmpeg/FFmpeg/tree/$sourceCommit"
$sourceArchiveUrl = "https://github.com/FFmpeg/FFmpeg/archive/$sourceCommit.zip"
$buildPageUrl = "https://www.gyan.dev/ffmpeg/builds/"
$licenseUrl = "https://www.gnu.org/licenses/gpl-3.0.html"

$ffmpegPath = Join-Path $FfmpegDirectory "ffmpeg.exe"

function Get-FfmpegVersionOutput {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    try {
        return ((& $Path -hide_banner -version 2>&1) -join [Environment]::NewLine)
    }
    catch {
        return $null
    }
}

# Older builds of this mod shipped the essentials build. The build must replace those, not
# keep using whatever ffmpeg.exe happens to be there.
function Test-BundledFullBuild {
    param([string]$VersionOutput)

    if ([string]::IsNullOrWhiteSpace($VersionOutput)) {
        return $false
    }

    $firstLine = ($VersionOutput -split "`r?`n")[0]
    return $firstLine -match ("^ffmpeg version " + [regex]::Escape($ffmpegVersion) + "-" + [regex]::Escape($ffmpegBuildVariant))
}

function Write-FfmpegDistributionFiles {
    param([string]$VersionOutput)

    New-Item -ItemType Directory -Force -Path $FfmpegDirectory | Out-Null

    # The notice below points at ThirdParty/FFmpeg/GPL-3.0.txt; keep a copy of the repository
    # license there so the release package carries the GPL text next to the FFmpeg binary.
    $repoLicense = Join-Path (Split-Path $PSScriptRoot -Parent) "LICENSE"
    if (Test-Path -LiteralPath $repoLicense) {
        Copy-Item -LiteralPath $repoLicense -Destination (Join-Path $FfmpegDirectory "GPL-3.0.txt") -Force
    }

    if ([string]::IsNullOrWhiteSpace($VersionOutput)) {
        $VersionOutput = "ffmpeg.exe was not available when this file was generated."
    }

    $buildInfoPath = Join-Path $FfmpegDirectory "FFmpeg-BUILD.txt"
    $sourceInfoPath = Join-Path $FfmpegDirectory "FFmpeg-SOURCE.txt"
    $noticePath = Join-Path $FfmpegDirectory "FFmpeg-NOTICE.txt"

    Set-Content -LiteralPath $buildInfoPath -Encoding UTF8 -Value @"
FFmpeg binary distributed with ADOFAI.EditorTweaks
=================================================

Bundled file:
  ThirdParty/FFmpeg/ffmpeg.exe

Build variant:
  gyan.dev full build (includes libsvtav1, libvvenc, libaom, libx264, libx265, librav1e, ...)

Upstream binary package:
  $ffmpegPackageName ($archiveName)

Download URL:
  $url

Expected SHA-256:
  $expectedSha256

Build page:
  $buildPageUrl

License:
  GNU General Public License version 3 (GPLv3)
  $licenseUrl

The exact executable reports:

$VersionOutput
"@

    Set-Content -LiteralPath $sourceInfoPath -Encoding UTF8 -Value @"
FFmpeg Corresponding Source Information
======================================

This release includes an unmodified FFmpeg executable distributed under the
GNU General Public License version 3 (GPLv3). When you publish a release that
contains ThirdParty/FFmpeg/ffmpeg.exe, you must also provide the machine-readable
Corresponding Source for that FFmpeg executable.

The FFmpeg source revision identified by the upstream build page is:
  $sourceCommit

FFmpeg source URL:
  $sourceUrl

FFmpeg source archive URL:
  $sourceArchiveUrl

Important:
  The bundled gyan.dev full build is a static GPLv3 build. It includes
  external libraries such as libx264, libx265, libaom, libsvtav1, libvvenc
  and librav1e. A compliant source offer should cover FFmpeg and the linked
  GPL/LGPL libraries that form the distributed binary, plus the build
  scripts/configuration needed to rebuild it.

External library source projects:
  libx264:   https://code.videolan.org/videolan/x264
  libx265:   https://bitbucket.org/multicoreware/x265_git
  libaom:    https://aomedia.googlesource.com/aom
  libsvtav1: https://gitlab.com/AOMediaCodec/SVT-AV1
  libvvenc:  https://github.com/fraunhoferhhi/vvenc
  librav1e:  https://github.com/xiph/rav1e
  Build scripts / package sources:
             https://github.com/GyanD/codexffmpeg

Recommended release practice:
  1. Keep this file, FFmpeg-BUILD.txt, FFmpeg-NOTICE.txt, and the GPLv3 text
     in the same release package as ThirdParty/FFmpeg/ffmpeg.exe.
  2. Attach a source archive for the bundled FFmpeg binary to the same public
     release page, or provide a clear no-charge source download next to the
     object-code download.
  3. Do not add license terms that forbid reverse engineering, modification,
     or redistribution of FFmpeg.
"@

    Set-Content -LiteralPath $noticePath -Encoding UTF8 -Value @"
Third-party notice: FFmpeg
==========================

ADOFAI.EditorTweaks distributes FFmpeg as a separate executable:
  ThirdParty/FFmpeg/ffmpeg.exe

FFmpeg is copyright (c) the FFmpeg developers and other contributors.
The bundled Windows build is the gyan.dev full build, distributed under the
GNU General Public License version 3 (GPLv3). A copy of GPLv3 is included in:
  ThirdParty/FFmpeg/GPL-3.0.txt

This full build includes additional libraries such as libsvtav1 (AV1),
libvvenc (H.266/VVC), libaom, libx264, libx265 and librav1e. Their source is
available from the corresponding upstream projects.

ADOFAI.EditorTweaks invokes FFmpeg as an external command-line program. FFmpeg
is not owned by the ADOFAI.EditorTweaks author.

Project:
  https://ffmpeg.org/

Windows build source:
  $buildPageUrl
"@
}

$existingVersion = Get-FfmpegVersionOutput $ffmpegPath
if (Test-BundledFullBuild $existingVersion) {
    Write-Host "FFmpeg $ffmpegVersion $ffmpegBuildVariant already exists: $ffmpegPath"
    Write-FfmpegDistributionFiles $existingVersion
    exit 0
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("ADOFAI.EditorTweaks.ffmpeg." + [System.Guid]::NewGuid().ToString("N"))
$archivePath = Join-Path $tempRoot $archiveName

try {
    New-Item -ItemType Directory -Force -Path $FfmpegDirectory | Out-Null
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null

    if (Test-Path -LiteralPath $ffmpegPath) {
        Write-Host "Replacing the existing FFmpeg with the $ffmpegVersion $ffmpegBuildVariant build."
    }

    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
    Write-Host "Downloading FFmpeg from $url"

    # Invoke-WebRequest is very slow for the ~169 MB full archive; curl.exe ships with
    # Windows 10/11 and the GitHub Actions runners, so prefer it when available.
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($null -ne $curl) {
        & curl.exe -L --fail --retry 3 --retry-delay 2 -o $archivePath $url
        if ($LASTEXITCODE -ne 0) {
            throw "curl.exe failed to download $url (exit code $LASTEXITCODE)."
        }
    }
    else {
        Invoke-WebRequest -Uri $url -OutFile $archivePath -UseBasicParsing -TimeoutSec 600
    }

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    $archiveStream = [System.IO.File]::OpenRead($archivePath)
    try {
        $actualSha256 = ([BitConverter]::ToString($sha256.ComputeHash($archiveStream))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $archiveStream.Dispose()
        $sha256.Dispose()
    }
    if ($actualSha256 -ne $expectedSha256) {
        throw "FFmpeg archive SHA-256 mismatch. Expected $expectedSha256 but got $actualSha256."
    }

    # tar.exe (bsdtar) ships with Windows 10/11 and can read .7z, so no 7-Zip install is needed.
    # Fall back to a 7-Zip installation (the GitHub Actions runners have one) if tar cannot.
    Write-Host "Extracting FFmpeg"
    $extracted = $false
    $tar = Get-Command tar.exe -ErrorAction SilentlyContinue
    if ($null -ne $tar) {
        & tar.exe -xf $archivePath -C $tempRoot
        $extracted = $LASTEXITCODE -eq 0
    }

    if (-not $extracted) {
        $sevenZip = Get-Command 7z.exe -ErrorAction SilentlyContinue
        if ($null -eq $sevenZip) {
            throw "Failed to extract ${archiveName}: neither tar.exe nor 7z.exe could extract it."
        }

        & 7z.exe x $archivePath "-o$tempRoot" -y | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to extract $archiveName with 7z.exe (exit code $LASTEXITCODE)."
        }
    }

    $downloadedFfmpeg = Get-ChildItem -Path $tempRoot -Recurse -Filter "ffmpeg.exe" | Select-Object -First 1
    if ($null -eq $downloadedFfmpeg) {
        throw "ffmpeg.exe was not found in the downloaded archive."
    }

    Copy-Item -LiteralPath $downloadedFfmpeg.FullName -Destination $ffmpegPath -Force
    Write-Host "FFmpeg installed to $ffmpegPath"
    Write-FfmpegDistributionFiles (Get-FfmpegVersionOutput $ffmpegPath)
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

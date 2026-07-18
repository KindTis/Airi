param()

$ErrorActionPreference = 'Stop'
$url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-07-11-13-13/ffmpeg-n8.1.2-22-g94138f6973-win64-lgpl-8.1.zip'
$expectedArchiveHash = '0420a551f5b2c6c2dc9a5ee2bb2e81656539b6b3a3c2acfdafe8fd4abcc8b82f'
$repoRoot = Split-Path -Parent $PSScriptRoot
$destination = Join-Path $repoRoot 'resources\ffmpeg\win-x64'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('AiriFfmpegImport-' + [guid]::NewGuid().ToString('N'))
$archive = Join-Path $temporaryRoot 'ffmpeg.zip'
$extract = Join-Path $temporaryRoot 'extract'

try {
    New-Item -ItemType Directory -Path $extract -Force | Out-Null
    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $archive
    $actualArchiveHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash.ToLowerInvariant()
    if ($actualArchiveHash -ne $expectedArchiveHash) {
        throw "FFmpeg archive SHA-256 mismatch: $actualArchiveHash"
    }

    Expand-Archive -LiteralPath $archive -DestinationPath $extract
    $root = Get-ChildItem -LiteralPath $extract -Directory | Select-Object -First 1
    if ($null -eq $root) { throw 'FFmpeg archive root was not found.' }
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root.FullName 'bin\ffmpeg.exe') -Destination $destination -Force
    Copy-Item -LiteralPath (Join-Path $root.FullName 'bin\ffprobe.exe') -Destination $destination -Force
    Copy-Item -LiteralPath (Join-Path $root.FullName 'LICENSE.txt') -Destination $destination -Force
    $archiveReadme = Join-Path $root.FullName 'README.txt'
    if (Test-Path -LiteralPath $archiveReadme) {
        Copy-Item -LiteralPath $archiveReadme -Destination $destination -Force
    }
    else {
        $readme = @"
FFmpeg preview runtime

Provider: BtbN/FFmpeg-Builds
Release: autobuild-2026-07-11-13-13
Archive: ffmpeg-n8.1.2-22-g94138f6973-win64-lgpl-8.1.zip
Source and build instructions: https://github.com/BtbN/FFmpeg-Builds/tree/autobuild-2026-07-11-13-13
FFmpeg source: https://github.com/FFmpeg/FFmpeg

The selected archive does not include a README file. This deterministic notice records the source locations used for the bundled executables. See LICENSE.txt and BUILD_INFO.md for license and build details.
"@
        [IO.File]::WriteAllText(
            (Join-Path $destination 'README.txt'),
            $readme,
            [Text.UTF8Encoding]::new($false))
    }

    $ffmpeg = Join-Path $destination 'ffmpeg.exe'
    $ffprobe = Join-Path $destination 'ffprobe.exe'
    $ffmpegHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $ffmpeg).Hash.ToLowerInvariant()
    $ffprobeHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $ffprobe).Hash.ToLowerInvariant()
    $version = (& $ffmpeg -version 2>&1) -join "`n"
    if ($version -match '--enable-gpl' -or $version -match '--enable-nonfree') {
        throw 'Selected FFmpeg build enables GPL or nonfree components.'
    }

    $distributionInfo = [regex]::Unescape('\uBC30\uD3EC \uC815\uBCF4')
    $providerLabel = [regex]::Unescape('\uC81C\uACF5\uC790')
    $releaseLabel = [regex]::Unescape('\uB9B4\uB9AC\uC2A4')
    $fileLabel = [regex]::Unescape('\uD30C\uC77C')
    $originLabel = [regex]::Unescape('\uC6D0\uBCF8')
    $archiveLabel = [regex]::Unescape('\uC544\uCE74\uC774\uBE0C')
    $variantLabel = [regex]::Unescape('\uBCC0\uD615')
    $correspondingSourceLabel = [regex]::Unescape('\uB300\uC751 \uC18C\uC2A4')
    $buildInfo = @"
# FFmpeg $distributionInfo

- ${providerLabel}: BtbN/FFmpeg-Builds
- ${releaseLabel}: autobuild-2026-07-11-13-13
- ${fileLabel}: ffmpeg-n8.1.2-22-g94138f6973-win64-lgpl-8.1.zip
- ${originLabel}: $url
- ${archiveLabel} SHA-256: $expectedArchiveHash
- ffmpeg.exe SHA-256: $ffmpegHash
- ffprobe.exe SHA-256: $ffprobeHash
- ${variantLabel}: Windows x64 static LGPL 8.1
- ${correspondingSourceLabel}: https://github.com/BtbN/FFmpeg-Builds/tree/autobuild-2026-07-11-13-13

## ffmpeg -version

~~~text
$version
~~~
"@
    [IO.File]::WriteAllText(
        (Join-Path $destination 'BUILD_INFO.md'),
        $buildInfo,
        [Text.UTF8Encoding]::new($false))
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

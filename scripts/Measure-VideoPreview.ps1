[CmdletBinding(DefaultParameterSetName = 'Dual')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Single')]
    [string]$VideoPath,

    [Parameter(Mandatory = $true, ParameterSetName = 'Dual')]
    [string]$H264Path,

    [Parameter(Mandatory = $true, ParameterSetName = 'Dual')]
    [string]$HevcPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputPath)

$video = $null
$h264 = $null
$hevc = $null
if ($PSCmdlet.ParameterSetName -eq 'Single') {
    $video = [IO.Path]::GetFullPath($VideoPath)
    if (-not [IO.File]::Exists($video)) {
        throw "Video preview input not found: $video"
    }
}
else {
    $h264 = [IO.Path]::GetFullPath($H264Path)
    $hevc = [IO.Path]::GetFullPath($HevcPath)
    if (-not [IO.File]::Exists($h264)) {
        throw "H.264 performance fixture not found: $h264"
    }
    if (-not [IO.File]::Exists($hevc)) {
        throw "HEVC performance fixture not found: $hevc"
    }
}

$cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name)
$gpu = ((Get-CimInstance Win32_VideoController | Select-Object -ExpandProperty Name) -join ', ')
$previous = @{
    Output = $env:AIRI_VIDEO_PREVIEW_PERF_OUTPUT
    Video = $env:AIRI_VIDEO_PREVIEW_VIDEO
    H264 = $env:AIRI_VIDEO_PREVIEW_H264
    Hevc = $env:AIRI_VIDEO_PREVIEW_HEVC
    Configuration = $env:AIRI_VIDEO_PREVIEW_BUILD_CONFIGURATION
    Cpu = $env:AIRI_VIDEO_PREVIEW_CPU
    Gpu = $env:AIRI_VIDEO_PREVIEW_GPU
}

try {
    $env:AIRI_VIDEO_PREVIEW_PERF_OUTPUT = $output
    if ($PSCmdlet.ParameterSetName -eq 'Single') {
        $env:AIRI_VIDEO_PREVIEW_VIDEO = $video
        $env:AIRI_VIDEO_PREVIEW_H264 = $null
        $env:AIRI_VIDEO_PREVIEW_HEVC = $null
    }
    else {
        $env:AIRI_VIDEO_PREVIEW_VIDEO = $null
        $env:AIRI_VIDEO_PREVIEW_H264 = $h264
        $env:AIRI_VIDEO_PREVIEW_HEVC = $hevc
    }
    $env:AIRI_VIDEO_PREVIEW_BUILD_CONFIGURATION = 'Release'
    $env:AIRI_VIDEO_PREVIEW_CPU = $cpu
    $env:AIRI_VIDEO_PREVIEW_GPU = $gpu

    Push-Location $repoRoot
    try {
        dotnet build Airi.sln -c Release
        if ($LASTEXITCODE -ne 0) {
            throw "Release build failed with exit code $LASTEXITCODE."
        }

        dotnet test tests/Airi.Tests/Airi.Tests.csproj -c Release --no-build --filter FullyQualifiedName~VideoPreviewPerformanceHarnessTests
        if ($LASTEXITCODE -ne 0) {
            throw "Video preview performance gate failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}
finally {
    $env:AIRI_VIDEO_PREVIEW_PERF_OUTPUT = $previous.Output
    $env:AIRI_VIDEO_PREVIEW_VIDEO = $previous.Video
    $env:AIRI_VIDEO_PREVIEW_H264 = $previous.H264
    $env:AIRI_VIDEO_PREVIEW_HEVC = $previous.Hevc
    $env:AIRI_VIDEO_PREVIEW_BUILD_CONFIGURATION = $previous.Configuration
    $env:AIRI_VIDEO_PREVIEW_CPU = $previous.Cpu
    $env:AIRI_VIDEO_PREVIEW_GPU = $previous.Gpu
}

Write-Host "Video preview performance report: $output"

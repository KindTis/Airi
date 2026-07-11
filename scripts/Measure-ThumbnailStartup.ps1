param(
    [Parameter(Mandatory = $true)]
    [string]$FixtureRoot,

    [Parameter(Mandatory = $true)]
    [string]$OutputRoot,

    [string[]]$Datasets = @('small', 'medium', 'current', 'stress'),

    [ValidateRange(1, 100)]
    [int]$Iterations = 5,

    [ValidateSet('Baseline', 'After')]
    [string]$Mode = 'After',

    [string]$MachineLabel = 'local-windows'
)

$ErrorActionPreference = 'Stop'
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixtureRootPath = [System.IO.Path]::GetFullPath($FixtureRoot)
$outputRootPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputRoot))
$releaseRoot = Join-Path $repoRoot 'bin/Release/net9.0-windows10.0.26100.0'
$testOutput = Join-Path $repoRoot 'tests/Airi.Tests/bin/Release/net9.0-windows10.0.26100.0'
$iterationBase = [System.IO.Path]::GetFullPath((Join-Path $env:TEMP 'AiriPerf'))

function Write-Utf8NoBom([string]$Path, [string]$Content) {
    $directory = Split-Path -Parent $Path
    if ($directory) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    [System.IO.File]::WriteAllText($Path, $Content, $script:utf8NoBom)
}

function Assert-PathUnder([string]$Root, [string]$Candidate) {
    $normalizedRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $normalizedCandidate = [System.IO.Path]::GetFullPath($Candidate)
    if (-not $normalizedCandidate.StartsWith($normalizedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path '$normalizedCandidate' is outside intended root '$normalizedRoot'."
    }
}

function Copy-TreeWithHardLinks([string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source)) {
        return
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $sourceRoot = [System.IO.Path]::GetFullPath($Source).TrimEnd('\', '/')
    Get-ChildItem -LiteralPath $Source -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($sourceRoot.Length).TrimStart('\', '/')
        $target = Join-Path $Destination $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        try {
            New-Item -ItemType HardLink -Path $target -Target $_.FullName -ErrorAction Stop | Out-Null
        }
        catch {
            Copy-Item -LiteralPath $_.FullName -Destination $target -Force
        }
    }
}

function New-CurrentFixture {
    $datasetRoot = Join-Path $script:fixtureRootPath 'current'
    $templatePath = Join-Path $datasetRoot 'library-template/videos.json'
    $manifestPath = Join-Path $datasetRoot 'manifest.json'
    if ((Test-Path -LiteralPath $templatePath) -and (Test-Path -LiteralPath $manifestPath)) {
        return
    }

    $sourceLibraryPath = Join-Path $script:releaseRoot 'videos.json'
    $sourceCache = Join-Path $script:releaseRoot 'cache'
    if (-not (Test-Path -LiteralPath $sourceLibraryPath)) {
        throw "Current fixture source is missing: $sourceLibraryPath"
    }

    $library = Get-Content -Raw -LiteralPath $sourceLibraryPath | ConvertFrom-Json
    $payloadRoot = Join-Path $datasetRoot 'payload'
    $mediaRoot = Join-Path $payloadRoot 'media'
    $cacheRoot = Join-Path $payloadRoot 'cache'
    New-Item -ItemType Directory -Path $mediaRoot -Force | Out-Null
    Copy-TreeWithHardLinks $sourceCache $cacheRoot

    $stamp = [DateTime]::SpecifyKind([DateTime]::new(2026, 1, 1, 0, 0, 0), [DateTimeKind]::Utc)
    $manifestEntries = [System.Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $library.Videos.Count; $index++) {
        $video = $library.Videos[$index]
        $relativeVideo = './media/{0:D4}.mp4' -f $index
        $mediaPath = Join-Path $mediaRoot ('{0:D4}.mp4' -f $index)
        [System.IO.File]::WriteAllBytes($mediaPath, [byte[]](0))
        [System.IO.File]::SetLastWriteTimeUtc($mediaPath, $stamp)
        [System.IO.File]::SetCreationTimeUtc($mediaPath, $stamp)

        $thumbnailName = [System.IO.Path]::GetFileName([string]$video.Meta.Thumbnail)
        $relativeThumbnail = if ($thumbnailName -and (Test-Path -LiteralPath (Join-Path $cacheRoot $thumbnailName))) {
            './cache/' + $thumbnailName
        }
        else {
            'resources/noimage.jpg'
        }

        $video.Path = $relativeVideo
        $video.SizeBytes = 1
        $video.LastModifiedUtc = $stamp.ToString('O')
        $video.CreatedUtc = $stamp.ToString('O')
        $video.Meta.Thumbnail = $relativeThumbnail
        $manifestEntries.Add([ordered]@{
            path = $relativeVideo
            thumbnailPath = $relativeThumbnail
            length = 1
            lastWriteUtc = $stamp.ToString('O')
        })
    }

    $library.Targets = @([ordered]@{
        Root = './media'
        IncludePatterns = @('*.mp4')
        ExcludePatterns = @()
        LastScanUtc = $stamp.ToString('O')
    })

    Write-Utf8NoBom $templatePath ($library | ConvertTo-Json -Depth 20)
    $manifest = [ordered]@{
        schemaVersion = 1
        dataset = 'current'
        itemCount = $library.Videos.Count
        entries = $manifestEntries
    }
    Write-Utf8NoBom $manifestPath ($manifest | ConvertTo-Json -Depth 10)
}

function New-SyntheticFixture([string]$Dataset, [int]$Count) {
    $datasetRoot = Join-Path $script:fixtureRootPath $Dataset
    $templatePath = Join-Path $datasetRoot 'library-template/videos.json'
    $manifestPath = Join-Path $datasetRoot 'manifest.json'
    if ((Test-Path -LiteralPath $templatePath) -and (Test-Path -LiteralPath $manifestPath)) {
        return
    }

    $mediaRoot = Join-Path $datasetRoot 'payload/media'
    $cacheRoot = Join-Path $datasetRoot 'payload/cache'
    New-Item -ItemType Directory -Path $mediaRoot, $cacheRoot -Force | Out-Null
    $fallback = Join-Path $script:releaseRoot 'resources/noimage.jpg'
    if (-not (Test-Path -LiteralPath $fallback)) {
        throw "Synthetic thumbnail source is missing: $fallback"
    }

    $stamp = [DateTime]::SpecifyKind([DateTime]::new(2026, 1, 1, 0, 0, 0), [DateTimeKind]::Utc)
    $videos = [System.Collections.Generic.List[object]]::new()
    $manifestEntries = [System.Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $Count; $index++) {
        $name = '{0:D4}' -f $index
        $relativeVideo = "./media/$name.mp4"
        $relativeThumbnail = "./cache/$name.jpg"
        $mediaPath = Join-Path $mediaRoot "$name.mp4"
        $thumbnailPath = Join-Path $cacheRoot "$name.jpg"
        [System.IO.File]::WriteAllBytes($mediaPath, [byte[]](0))
        Copy-Item -LiteralPath $fallback -Destination $thumbnailPath -Force
        [System.IO.File]::SetLastWriteTimeUtc($mediaPath, $stamp)
        [System.IO.File]::SetCreationTimeUtc($mediaPath, $stamp)
        [System.IO.File]::SetLastWriteTimeUtc($thumbnailPath, $stamp)

        $videos.Add([ordered]@{
            Path = $relativeVideo
            Meta = [ordered]@{
                Title = "Synthetic $name"
                Date = $null
                Actors = @("Actor $($index % 17)")
                Thumbnail = $relativeThumbnail
                Tags = @()
                Description = ''
            }
            SizeBytes = 1
            LastModifiedUtc = $stamp.ToString('O')
            CreatedUtc = $stamp.ToString('O')
        })
        $manifestEntries.Add([ordered]@{
            path = $relativeVideo
            thumbnailPath = $relativeThumbnail
            length = 1
            lastWriteUtc = $stamp.ToString('O')
        })
    }

    $library = [ordered]@{
        Version = 1
        Targets = @([ordered]@{
            Root = './media'
            IncludePatterns = @('*.mp4')
            ExcludePatterns = @()
            LastScanUtc = $stamp.ToString('O')
        })
        Videos = $videos
    }
    $manifest = [ordered]@{
        schemaVersion = 1
        dataset = $Dataset
        itemCount = $Count
        entries = $manifestEntries
    }
    Write-Utf8NoBom $templatePath ($library | ConvertTo-Json -Depth 20)
    Write-Utf8NoBom $manifestPath ($manifest | ConvertTo-Json -Depth 10)
}

function Assert-Fixture([string]$DatasetRoot, [string]$TemplatePath, [string]$ManifestPath) {
    $library = Get-Content -Raw -LiteralPath $TemplatePath | ConvertFrom-Json
    $manifest = Get-Content -Raw -LiteralPath $ManifestPath | ConvertFrom-Json
    if ([int]$manifest.itemCount -ne $library.Videos.Count -or $manifest.entries.Count -ne $library.Videos.Count) {
        throw "Fixture count mismatch under $DatasetRoot."
    }

    $mediaRoot = Join-Path $DatasetRoot 'payload/media'
    if (@(Get-ChildItem -LiteralPath $mediaRoot -File).Count -ne $library.Videos.Count) {
        throw "Fixture media count mismatch under $DatasetRoot."
    }
    for ($index = 0; $index -lt $manifest.entries.Count; $index++) {
        $entry = $manifest.entries[$index]
        if ([string]$library.Videos[$index].Path -ne [string]$entry.path) {
            throw "Fixture order/path mismatch at index $index under $DatasetRoot."
        }
        $mediaPath = Join-Path $mediaRoot ([System.IO.Path]::GetFileName([string]$entry.path))
        if (-not (Test-Path -LiteralPath $mediaPath) -or (Get-Item -LiteralPath $mediaPath).Length -ne [long]$entry.length) {
            throw "Fixture media metadata mismatch for $($entry.path)."
        }
        if ([string]$entry.thumbnailPath -like './cache/*') {
            $thumbnailPath = Join-Path (Join-Path $DatasetRoot 'payload/cache') ([System.IO.Path]::GetFileName([string]$entry.thumbnailPath))
            if (-not (Test-Path -LiteralPath $thumbnailPath)) {
                throw "Fixture thumbnail is missing: $($entry.thumbnailPath)"
            }
        }
    }
}

function Get-PowerSchemeLabel {
    try {
        $active = (& powercfg /getactivescheme 2>$null | Out-String).Trim()
        if ($active -match '\(([^)]+)\)') {
            return $Matches[1]
        }
    }
    catch {
    }
    return 'unknown'
}

function Get-Median([double[]]$Values) {
    if ($null -eq $Values -or $Values.Count -eq 0) {
        return $null
    }
    $sorted = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) {
        return $sorted[$middle]
    }
    return ($sorted[$middle - 1] + $sorted[$middle]) / 2
}

Set-Location $repoRoot
New-Item -ItemType Directory -Path $fixtureRootPath -Force | Out-Null
New-Item -ItemType Directory -Path $outputRootPath -Force | Out-Null

& dotnet build Airi.sln -c Release --no-restore -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) {
    throw "Release build failed with exit code $LASTEXITCODE."
}

if ($Datasets -contains 'current') {
    New-CurrentFixture
}
if ($Datasets -contains 'small') {
    New-SyntheticFixture 'small' 40
}
if ($Datasets -contains 'medium') {
    New-SyntheticFixture 'medium' 200
}
if ($Datasets -contains 'stress') {
    New-SyntheticFixture 'stress' 1000
}

if (($Datasets -contains 'medium') -and ($Datasets -contains 'stress')) {
    $medium = Get-Content -Raw -LiteralPath (Join-Path $fixtureRootPath 'medium/manifest.json') | ConvertFrom-Json
    $stress = Get-Content -Raw -LiteralPath (Join-Path $fixtureRootPath 'stress/manifest.json') | ConvertFrom-Json
    for ($index = 0; $index -lt $medium.entries.Count; $index++) {
        if ([string]$medium.entries[$index].path -ne [string]$stress.entries[$index].path -or
            [string]$medium.entries[$index].thumbnailPath -ne [string]$stress.entries[$index].thumbnailPath) {
            throw "Medium fixture is not the ordered prefix of stress at index $index."
        }
    }
}

$commitSha = (& git rev-parse HEAD).Trim()
$dirty = [bool](& git status --porcelain)
$powerScheme = Get-PowerSchemeLabel
$rawFiles = [System.Collections.Generic.List[string]]::new()

foreach ($dataset in $Datasets) {
    $datasetRoot = Join-Path $fixtureRootPath $dataset
    $templatePath = Join-Path $datasetRoot 'library-template/videos.json'
    $manifestPath = Join-Path $datasetRoot 'manifest.json'
    $payloadRoot = Join-Path $datasetRoot 'payload'
    if (-not (Test-Path -LiteralPath $templatePath) -or -not (Test-Path -LiteralPath $manifestPath)) {
        throw "Fixture '$dataset' is incomplete under $datasetRoot."
    }
    Assert-Fixture $datasetRoot $templatePath $manifestPath

    for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
        $iterationRoot = Join-Path $iterationBase (Join-Path $dataset $iteration)
        Assert-PathUnder $iterationBase $iterationRoot
        if (Test-Path -LiteralPath $iterationRoot) {
            Remove-Item -LiteralPath $iterationRoot -Recurse -Force
        }

        $binRoot = Join-Path $iterationRoot 'bin'
        $coldRoot = Join-Path $iterationRoot 'library/cold'
        $warmRoot = Join-Path $iterationRoot 'library/warm'
        $stagingRoot = Join-Path $iterationRoot 'staging'
        $resultsRoot = Join-Path $iterationRoot 'results'
        New-Item -ItemType Directory -Path $binRoot, $coldRoot, $warmRoot, $stagingRoot, $resultsRoot -Force | Out-Null
        Copy-Item -Path (Join-Path $testOutput '*') -Destination $binRoot -Recurse -Force
        Copy-TreeWithHardLinks (Join-Path $payloadRoot 'media') (Join-Path $binRoot 'media')
        Copy-TreeWithHardLinks (Join-Path $payloadRoot 'cache') (Join-Path $binRoot 'cache')

        $coldStore = Join-Path $coldRoot 'videos.json'
        $warmStore = Join-Path $warmRoot 'videos.json'
        Copy-Item -LiteralPath $templatePath -Destination $coldStore -Force
        Copy-Item -LiteralPath $templatePath -Destination $warmStore -Force
        $stagingOutput = Join-Path $stagingRoot 'performance.json'

        $env:AIRI_PERF_OUTPUT = $stagingOutput
        $env:AIRI_PERF_ITERATION_ROOT = $iterationRoot
        $env:AIRI_PERF_COLD_STORE = $coldStore
        $env:AIRI_PERF_WARM_STORE = $warmStore
        $env:AIRI_PERF_DATASET = $dataset
        $env:AIRI_PERF_ITERATION = [string]$iteration
        $env:AIRI_PERF_MODE = $Mode
        $env:AIRI_PERF_MACHINE_LABEL = $MachineLabel
        $env:AIRI_PERF_POWER_SCHEME = $powerScheme
        $env:AIRI_PERF_COMMIT_SHA = $commitSha
        $env:AIRI_PERF_DIRTY = $dirty.ToString().ToLowerInvariant()
        $env:AIRI_PERF_MANIFEST_SHA256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $manifestPath).Hash

        Push-Location $binRoot
        try {
            & dotnet vstest .\Airi.Tests.dll `
                --TestCaseFilter:"FullyQualifiedName=Airi.Tests.ThumbnailPerformanceHarnessTests.Measure" `
                --ResultsDirectory:$resultsRoot `
                --logger:"trx;LogFileName=performance.trx"
            if ($LASTEXITCODE -ne 0) {
                throw "Performance test process failed for $dataset iteration $iteration."
            }
        }
        finally {
            Pop-Location
        }

        [xml]$trx = Get-Content -Raw -LiteralPath (Join-Path $resultsRoot 'performance.trx')
        $counters = $trx.SelectSingleNode("//*[local-name()='Counters']")
        if ($null -eq $counters -or [int]$counters.executed -ne 1 -or [int]$counters.passed -ne 1 -or [int]$counters.failed -ne 0) {
            throw "Expected exactly one passing performance test for $dataset iteration $iteration."
        }
        if (-not (Test-Path -LiteralPath $stagingOutput)) {
            throw "Performance test did not create $stagingOutput."
        }

        $datasetOutput = Join-Path $outputRootPath $dataset
        New-Item -ItemType Directory -Path $datasetOutput -Force | Out-Null
        $rawPath = Join-Path $datasetOutput ('iteration-{0:D2}.json' -f $iteration)
        Copy-Item -LiteralPath $stagingOutput -Destination $rawPath -Force
        $rawFiles.Add($rawPath)

        Remove-Item Env:AIRI_PERF_OUTPUT, Env:AIRI_PERF_ITERATION_ROOT, Env:AIRI_PERF_COLD_STORE, Env:AIRI_PERF_WARM_STORE, Env:AIRI_PERF_DATASET, Env:AIRI_PERF_ITERATION, Env:AIRI_PERF_MODE, Env:AIRI_PERF_MACHINE_LABEL, Env:AIRI_PERF_POWER_SCHEME, Env:AIRI_PERF_COMMIT_SHA, Env:AIRI_PERF_DIRTY, Env:AIRI_PERF_MANIFEST_SHA256 -ErrorAction SilentlyContinue
        Assert-PathUnder $iterationBase $iterationRoot
        Remove-Item -LiteralPath $iterationRoot -Recurse -Force
    }
}

$rawDocuments = @($rawFiles | ForEach-Object { Get-Content -Raw -LiteralPath $_ | ConvertFrom-Json })
$datasetSummaries = [ordered]@{}
foreach ($dataset in $Datasets) {
    $documents = @($rawDocuments | Where-Object dataset -eq $dataset)
    $phases = [ordered]@{}
    foreach ($phaseName in @('cold', 'warm')) {
        $phaseValues = @($documents | ForEach-Object { $_.$phaseName })
        $validValues = @($phaseValues | Where-Object valid)
        $hardGatePasses = @($validValues | Where-Object {
            $_.structuralValidation.allPassed -and
            $_.gates.firstCardBeforeAllItems -ne 'Fail' -and
            $_.gates.requestMembership -and
            $_.gates.fileOpenBound -and
            $_.gates.decodeConcurrency -and
            $_.gates.lruInvariant -and
            $_.gates.dispatcherUnder100Milliseconds -and
            $_.gates.nonFallbackSourceBound -and
            $_.gates.ownerSlotBound
        })
        $card = @($validValues | ForEach-Object { [double]$_.timing.markers.VisualFirstMeaningfulCard.elapsedMilliseconds })
        $thumbnail = @($validValues | ForEach-Object { [double]$_.timing.markers.VisualFirstThumbnail.elapsedMilliseconds })
        $firstSteady = @($validValues | ForEach-Object {
            @($_.timing.resourceCheckpoints | Where-Object kind -eq 'FirstSteady')[0]
        })
        $firstSteadyWorkingSet = @($firstSteady | ForEach-Object { [double]$_.workingSetBytes })
        $firstSteadyManagedHeap = @($firstSteady | ForEach-Object { [double]$_.managedHeapBytes })
        $checkpointMaxWorkingSet = @($validValues | ForEach-Object { [double]$_.timing.checkpointMaximum.workingSetBytes })
        $checkpointMaxManagedHeap = @($validValues | ForEach-Object { [double]$_.timing.checkpointMaximum.managedHeapBytes })
        $gc0 = @($validValues | ForEach-Object { [double]$_.timing.gcPhaseDelta.gen0 })
        $gc1 = @($validValues | ForEach-Object { [double]$_.timing.gcPhaseDelta.gen1 })
        $gc2 = @($validValues | ForEach-Object { [double]$_.timing.gcPhaseDelta.gen2 })
        $phases[$phaseName] = [ordered]@{
            validIterations = $validValues.Count
            totalIterations = $phaseValues.Count
            allValid = $validValues.Count -eq $phaseValues.Count
            hardGatePassIterations = $hardGatePasses.Count
            allHardGatesPass = $hardGatePasses.Count -eq $phaseValues.Count
            firstMeaningfulCardMedianMs = Get-Median $card
            firstMeaningfulCardWorstMs = if ($card.Count) { ($card | Measure-Object -Maximum).Maximum } else { $null }
            firstThumbnailMedianMs = Get-Median $thumbnail
            firstThumbnailWorstMs = if ($thumbnail.Count) { ($thumbnail | Measure-Object -Maximum).Maximum } else { $null }
            firstSteadyWorkingSetMedianBytes = Get-Median $firstSteadyWorkingSet
            firstSteadyWorkingSetWorstBytes = if ($firstSteadyWorkingSet.Count) { ($firstSteadyWorkingSet | Measure-Object -Maximum).Maximum } else { $null }
            firstSteadyManagedHeapMedianBytes = Get-Median $firstSteadyManagedHeap
            firstSteadyManagedHeapWorstBytes = if ($firstSteadyManagedHeap.Count) { ($firstSteadyManagedHeap | Measure-Object -Maximum).Maximum } else { $null }
            checkpointMaxWorkingSetMedianBytes = Get-Median $checkpointMaxWorkingSet
            checkpointMaxWorkingSetWorstBytes = if ($checkpointMaxWorkingSet.Count) { ($checkpointMaxWorkingSet | Measure-Object -Maximum).Maximum } else { $null }
            checkpointMaxManagedHeapMedianBytes = Get-Median $checkpointMaxManagedHeap
            checkpointMaxManagedHeapWorstBytes = if ($checkpointMaxManagedHeap.Count) { ($checkpointMaxManagedHeap | Measure-Object -Maximum).Maximum } else { $null }
            gc0PhaseDeltaMedian = Get-Median $gc0
            gc0PhaseDeltaWorst = if ($gc0.Count) { ($gc0 | Measure-Object -Maximum).Maximum } else { $null }
            gc1PhaseDeltaMedian = Get-Median $gc1
            gc1PhaseDeltaWorst = if ($gc1.Count) { ($gc1 | Measure-Object -Maximum).Maximum } else { $null }
            gc2PhaseDeltaMedian = Get-Median $gc2
            gc2PhaseDeltaWorst = if ($gc2.Count) { ($gc2 | Measure-Object -Maximum).Maximum } else { $null }
        }
    }
    $datasetSummaries[$dataset] = $phases
}

$mediumToStress = [ordered]@{}
$mediumStressGuardRow = [ordered]@{}
if ($datasetSummaries.Contains('medium') -and $datasetSummaries.Contains('stress')) {
    foreach ($phaseName in @('cold', 'warm')) {
        $medium = $datasetSummaries['medium'][$phaseName]
        $stress = $datasetSummaries['stress'][$phaseName]
        $mediumToStress[$phaseName] = [ordered]@{
            firstMeaningfulCardMedianDeltaMs = $stress.firstMeaningfulCardMedianMs - $medium.firstMeaningfulCardMedianMs
            firstMeaningfulCardWorstDeltaMs = $stress.firstMeaningfulCardWorstMs - $medium.firstMeaningfulCardWorstMs
            firstThumbnailMedianDeltaMs = $stress.firstThumbnailMedianMs - $medium.firstThumbnailMedianMs
            firstThumbnailWorstDeltaMs = $stress.firstThumbnailWorstMs - $medium.firstThumbnailWorstMs
            firstSteadyWorkingSetMedianDeltaBytes = $stress.firstSteadyWorkingSetMedianBytes - $medium.firstSteadyWorkingSetMedianBytes
            firstSteadyWorkingSetWorstDeltaBytes = $stress.firstSteadyWorkingSetWorstBytes - $medium.firstSteadyWorkingSetWorstBytes
            firstSteadyManagedHeapMedianDeltaBytes = $stress.firstSteadyManagedHeapMedianBytes - $medium.firstSteadyManagedHeapMedianBytes
            firstSteadyManagedHeapWorstDeltaBytes = $stress.firstSteadyManagedHeapWorstBytes - $medium.firstSteadyManagedHeapWorstBytes
            checkpointMaxWorkingSetMedianDeltaBytes = $stress.checkpointMaxWorkingSetMedianBytes - $medium.checkpointMaxWorkingSetMedianBytes
            checkpointMaxWorkingSetWorstDeltaBytes = $stress.checkpointMaxWorkingSetWorstBytes - $medium.checkpointMaxWorkingSetWorstBytes
            checkpointMaxManagedHeapMedianDeltaBytes = $stress.checkpointMaxManagedHeapMedianBytes - $medium.checkpointMaxManagedHeapMedianBytes
            checkpointMaxManagedHeapWorstDeltaBytes = $stress.checkpointMaxManagedHeapWorstBytes - $medium.checkpointMaxManagedHeapWorstBytes
            gc0PhaseDeltaMedianDifference = $stress.gc0PhaseDeltaMedian - $medium.gc0PhaseDeltaMedian
            gc0PhaseDeltaWorstDifference = $stress.gc0PhaseDeltaWorst - $medium.gc0PhaseDeltaWorst
            gc1PhaseDeltaMedianDifference = $stress.gc1PhaseDeltaMedian - $medium.gc1PhaseDeltaMedian
            gc1PhaseDeltaWorstDifference = $stress.gc1PhaseDeltaWorst - $medium.gc1PhaseDeltaWorst
            gc2PhaseDeltaMedianDifference = $stress.gc2PhaseDeltaMedian - $medium.gc2PhaseDeltaMedian
            gc2PhaseDeltaWorstDifference = $stress.gc2PhaseDeltaWorst - $medium.gc2PhaseDeltaWorst
        }

        $pairPasses = 0
        for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
            $mediumDocument = $rawDocuments | Where-Object { $_.dataset -eq 'medium' -and $_.iteration -eq $iteration } | Select-Object -First 1
            $stressDocument = $rawDocuments | Where-Object { $_.dataset -eq 'stress' -and $_.iteration -eq $iteration } | Select-Object -First 1
            $pairPass = $null -ne $mediumDocument -and $null -ne $stressDocument -and
                $mediumDocument.$phaseName.valid -and $stressDocument.$phaseName.valid
            if ($pairPass) {
                foreach ($mediumPosition in @($mediumDocument.$phaseName.structuralValidation.positions)) {
                    $stressPosition = @($stressDocument.$phaseName.structuralValidation.positions | Where-Object name -eq $mediumPosition.name)[0]
                    if ($null -eq $stressPosition -or
                        [Math]::Abs([int]$stressPosition.realizedContainerCount - [int]$mediumPosition.realizedContainerCount) -gt [int]$mediumPosition.columns -or
                        [Math]::Abs([int]$stressPosition.realizedNonFallbackSourceCount - [int]$mediumPosition.realizedNonFallbackSourceCount) -gt [int]$mediumPosition.columns) {
                        $pairPass = $false
                        break
                    }
                }
            }
            if ($pairPass) {
                $pairPasses++
            }
        }
        $mediumStressGuardRow[$phaseName] = [ordered]@{
            passIterations = $pairPasses
            totalIterations = $Iterations
            allPass = $pairPasses -eq $Iterations
        }
    }
}

$allLocalHardGatesPass = $true
foreach ($dataset in $Datasets) {
    foreach ($phaseName in @('cold', 'warm')) {
        if (-not $datasetSummaries[$dataset][$phaseName].allHardGatesPass) {
            $allLocalHardGatesPass = $false
        }
    }
}
$allCrossDatasetHardGatesPass = $mediumStressGuardRow.Count -eq 0 -or
    (@($mediumStressGuardRow.Values | Where-Object { -not $_.allPass }).Count -eq 0)

$summary = [ordered]@{
    schemaVersion = 2
    mode = $Mode
    machineLabel = $MachineLabel
    commitSha = $commitSha
    dirtyWorktree = $dirty
    iterations = $Iterations
    allHardGatesPass = $allLocalHardGatesPass -and $allCrossDatasetHardGatesPass
    datasets = $datasetSummaries
    crossDatasetGates = [ordered]@{
        mediumStressContainerAndSourceGuardRow = $mediumStressGuardRow
    }
    observations = [ordered]@{
        mediumToStress = $mediumToStress
    }
}
Write-Utf8NoBom (Join-Path $outputRootPath 'summary.json') ($summary | ConvertTo-Json -Depth 20)

# Airi Incremental Implementation Roadmap

DevPlan.md and Airi_GapAnalysis.md highlight that the current prototype lacks the planned local library pipeline, MVVM architecture, and UX. The following stages break the work into shippable, testable milestones; at the end of every stage the app must build, run, and demonstrate the new capability without regressing earlier stages.

## Stage 0 - Baseline Stabilisation
- Objectives: Adopt the DevPlan JSON schema placeholders, remove unused scraping paths, ensure the project builds from a clean checkout.
- Key tasks:
  * Replace ad-hoc globals (`VideoItems`, `VideoMetaParser` search) with stub services and include `noimage.jpg` as default thumbnail.
  * Wire `MainWindow` to a temporary in-memory view model that can display placeholder tiles.
  * Strip or guard external HTTP calls so the app runs offline.
- Acceptance: `dotnet build` succeeds; running the app shows a placeholder gallery sourced from stub data; no network traffic is required.
- Tests: `dotnet build`; manual launch via `dotnet run` to confirm UI loads.

## Stage 1 - Domain Models and Local Store
- Objectives: Implement the `TargetFolder`, `VideoItem`, and `VideoMeta` records and the JSON persistence layer described in DevPlan.
- Key tasks:
  * Create `%AppData%/Airi/videos.json` management (`IJsonStore`) with load/save/validation hooks.
  * Add schema versioning and graceful fallback if the file is missing or corrupted.
  * Update the temporary view model to read from the JSON store and surface sample data from `videos.json` for manual editing.
- Acceptance: Fresh install creates a default `videos.json`; editing the file and relaunching reflects changes in the UI; unit tests cover model serialization.
- Tests: `dotnet test` (new suite); manual run verifying JSON round-trip.

## Stage 2 - Scanner and Diff Pipeline
- Objectives: Implement the file system scanner and diff logic to align local folders with the JSON catalogue.
- Key tasks:
  * Build `Scanner` service to enumerate target folders with include/exclude patterns and capture metadata (path, size, mtime).
  * Add diffing logic to detect new, missing, and modified files; surface results in the view model state.
  * Introduce a background startup task (`RunStartupAsync`) that updates the JSON store incrementally.
- Acceptance: Launching triggers a scan of configured folders, updates the catalogue, and marks missing files; UI shows scan progress in a status bar.
- Tests: Integration test for scan/diff against a temporary directory tree; manual run observing status updates.

## Stage 3 - Parse Queue and Metadata Services
- Objectives: Introduce the parsing queue architecture with pluggable metadata sources.
- Key tasks:
  * Implement `ParseQueue` with bounded concurrency and cancellation tokens.
  * Create local metadata parsers (e.g., filename/date extractor) adhering to `IVideoMetaSource`.
  * Persist parser output back to the JSON store and refresh the view model without blocking the UI thread.
- Acceptance: Newly discovered videos enter the queue, receive parsed titles/dates, and appear in the UI with updated metadata; the app remains responsive during parsing.
- Tests: Unit tests for queue scheduling; integration test that enqueues mock files and verifies JSON updates.

## Stage 4 - MVVM Restructure and Actor Filtering
- Objectives: Refactor UI to MVVM, enable actor lists, and align with DevPlan’s layout expectations.
- Key tasks:
  * Introduce dedicated view models (`ShellViewModel`, `VideoGridViewModel`, `ActorListViewModel`) and bind `MainWindow` to them.
  * Populate the left actor list from aggregated metadata with multi-select support and apply filter to the grid.
  * Replace code-behind event handlers with commands and `CollectionViewSource` filtering/sorting.
- Acceptance: Actor selection, title/shot sorting, and random-play command operate from the UI; no business logic remains in code-behind beyond view wiring.
- Tests: UI-level smoke test using `Microsoft.VisualStudio.TestTools.UITesting` or `WinAppDriver`; unit tests for filter logic.

## Stage 5 - Web Metadata Crawlers
- Objectives: Integrate online metadata providers (e.g., `VideoMetaParser`) that can search remote catalogues and enrich local entries.
- Key tasks:
  * Define a crawler plug-in contract (`IWebVideoMetaSource`) that maps a search query or video id to structured metadata.
  * Implement an HTTP-based provider that mirrors the current `VideoMetaParser` behaviour with throttling, user-agent configuration, and robots compliance.
  * Cache downloaded thumbnails and JSON responses, merge the results into `ParseQueue`, and persist them back through `LibraryStore`.
  * Add retry, backoff, and graceful degradation when the remote site fails or rate limits.
- Acceptance: Triggering a metadata fetch populates title/date/actor/thumbnail fields from the web source without blocking the UI; repeated requests reuse cached data when appropriate.
- Tests: Integration test hitting a mocked HTTP server; unit tests for crawler parsing and throttling logic; manual run confirming metadata enrichment.

## Stage 6 - Thumbnail Cache and Media Launch
- Objectives: Implement thumbnail download/cache pipeline and double-click playback behaviour.
- Key tasks:
  * Add `%LocalAppData%/Airi/cache` thumbnail storage with hashing and cleanup policies.
  * Extend metadata parser to schedule thumbnail downloads via HttpClient with timeout/retry.
  * Implement double-click to open the underlying file via `Process.Start` with error handling and user notifications.
- Acceptance: Grid displays cached thumbnails persisted across runs; missing thumbnails fall back to `noimage.jpg`; double-click opens the video in the system player.
- Tests: Unit tests for cache naming/eviction; manual run verifying download and playback.

## Stage 7 - Logging, Error Handling, and Telemetry
- Objectives: Add production-grade diagnostics per DevPlan.
- Key tasks:
  * Integrate Serilog with rolling file sinks, enrich log context with correlation ids.
  * Surface user-friendly error banners/toasts for scan/parse/download failures.
  * Track parser retry counts and expose them in the status area.
- Acceptance: Logs capture scan/parse lifecycle; induced failures show toast notifications without crashing; retries obey configured limits.
- Tests: Automated tests asserting logger calls via dependency injection; manual fault injection (rename folders, disconnect network).

## Stage 8 - Performance Hardening and Release Polish
- Objectives: Validate performance on large libraries and finalise distribution assets.
- Key tasks:
  * Enable virtualization-friendly panels (`VirtualizingWrapPanel` or `ItemsRepeater`) and measure memory/CPU with 10k items.
  * Profile parse/scan pipeline, tune concurrency, and ensure responsive UI under load.
  * Prepare release packaging (MSIX or self-contained installer), update documentation, and finalise DevPlan compliance checklist.
- Acceptance: Test run with synthetic 10k entries stays under target performance envelope; packaged build installs and launches cleanly; documentation reflects final architecture.
- Tests: Load tests with generated dataset; smoke test on a clean VM; checklist review against DevPlan sections.

Follow the stages sequentially; each stage can be merged once the acceptance and test criteria are demonstrably met.

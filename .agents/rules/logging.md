---
trigger: always_on
---

# ParityProof Modern Logging Standards & Aspect Weaving

Adhere to the following conventions when authoring, modifying, or refactoring logging across the ParityProof codebase.

---

## 1. Core Framework & Facade
- **Serilog & AppLogger**: ParityProof standardizes on **Serilog** for high-performance structured logging. Access logger instances via `AppLogger.Logger`, `AppLogger.ForContext<T>()`, or `AppLogger.ForContext(typeof(T))`.
- **No Console/Debug Output**: Never use `Console.WriteLine`, `Debug.WriteLine`, or `Trace.WriteLine`. All output must flow through `AppLogger`.

---

## 2. Structured Message Templates (Never Use `$""` Interpolation)
- **Named Placeholders**: Always use message templates with descriptive tokens. This preserves structured properties in the SQLite database and log aggregators:
  ```csharp
  // Correct
  AppLogger.Logger.Information(
      "Verified media item {FilePath} with size {FileSize} bytes and hash {Hash}",
      file.Path,
      file.FileLength,
      file.HeadHash);

  // Incorrect (AVOID)
  AppLogger.Logger.Information($"Verified media item {file.Path} with size {file.FileLength} bytes");
  ```
- **Performance**: String interpolation causes immediate string allocations and memory pressure even when the target log level is disabled. Message templates are only rendered if the level is active.

---

## 3. Log Levels & Usage Guidelines
| Level | Semantic Meaning & When to Use | Default in Release | Default in Debug |
|---|---|---|---|
| **Verbose / Trace** | Ultra-fine diagnostics, per-chunk transfer details, or low-level I/O metrics. | Disabled | Disabled |
| **Debug** | Internal state transitions, diagnostic execution info, and `[LogMethod]` IL weaving. | Disabled | **Active** |
| **Information** | High-level business milestones (e.g. scan started, copy batch finished, drive detected). | Disabled | Active |
| **Warning** | Recoverable issues, skipped corrupt files, retry attempts, non-fatal cache misses. | **Active** | Active |
| **Error** | Operational failures preventing a task from succeeding, failed file writes, exceptions. | Active | Active |
| **Fatal** | Unrecoverable process crashes, critical SQLite corruption, app termination. | Active | Active |

---

## 4. IL Weaving with `[LogMethod]`
AspectInjector performs compile-time IL weaving via the `[LogMethod]` attribute:
- **Where to Use**: Apply `[LogMethod]` to core service classes or high-value orchestrator methods (e.g. `MediaCopier`, `MultiDestinationVerifier`, `ContentAddressedMatcher`, `FastDirectoryScanner.ScanDirectory`, `SqliteIndexCache`).
- **Where NOT to Use**:
  - **Never** apply to tight inner loops or high-frequency per-chunk/per-byte operations (e.g., `SimdHasher.ComputeXxHash64`, `ChunkReader.ReadNextChunk`).
  - **Never** apply to simple properties or fast accessor getters/setters.
- **Custom Log Level**: `[LogMethod]` defaults to `LogEventLevel.Debug`. You can customize the level per method or class:
  ```csharp
  [LogMethod(Level = LogEventLevel.Information)]
  public async Task<int> CopyMissingFilesAsync(...)
  ```
- **Safe Value Serialization**: The aspect automatically formats and truncates large buffers (e.g., byte arrays display as `<byte[N]>`, strings truncate past 256 characters, collections truncate past 8 elements).
- **Async Execution**: The aspect handles synchronous methods, `Task`, and `Task<T>`, capturing completion duration and results without blocking threads.

---

## 5. Zero-Overhead Level Guards
- When computing complex or expensive arguments for a log message, always wrap the computation in an `IsEnabled` check:
  ```csharp
  if (AppLogger.IsEnabled(LogEventLevel.Debug))
  {
      string complexSummary = BuildDeepAnalysisSummary();
      AppLogger.Logger.Debug("Analysis details: {Summary}", complexSummary);
  }
  ```

---

## 6. Exception Handling & Logging
- **Pass the Exception Object**: Always pass the `Exception` as the first argument to logger methods so the full stack trace is preserved:
  ```csharp
  try
  {
      // ...
  }
  catch (Exception ex)
  {
      AppLogger.Logger.Error(ex, "Failed to copy file {SourcePath} to {DestinationPath}", source, dest);
      throw;
  }
  ```
- Do not log only `ex.Message` without passing `ex`.

---

## 7. Configuration & Command-Line Flags
ParityProof supports standard CLI startup flags:
- `--log-level <Level>` / `-l <Level>`: Set level (`Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`).
- `-v` / `--verbose`: Shortcut to set `Debug` level.
- `--log-file <path>`: Custom rolling log file location.
- `--no-log-file`: Disable file sink.
- `--log-db <path>`: Custom SQLite database location.
- `--no-log-db`: Disable SQLite sink.
- Default sinks write to `{LocalAppData}/ParityProof/logs/parityproof-.log` and `{LocalAppData}/ParityProof/logs.db`.

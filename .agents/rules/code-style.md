---
trigger: always_on
---

# ParityProof Code Style & Modern C# Standards

Adhere to the following conventions when authoring, modifying, or refactoring C# code across the ParityProof workspace.

---

## 1. Type Declarations & `var`
- **Explicit Types Over `var`**: Do not use `var` for local variable declarations. Always use the explicit type (e.g. `string relativePath = "..."`, `int copiedCount = 0`, `MediaFile? file = null`).
- **Target-Typed `new()`**: When the explicit type is already declared on the left-hand side, target-typed `new()` is preferred for constructor invocations to eliminate redundancy:
  ```csharp
  // Correct
  List<MediaFile> missingFiles = new();
  Dictionary<string, MediaFile> indexCache = new();
  Stopwatch stopwatch = new();

  // Avoid
  var missingFiles = new List<MediaFile>();
  List<MediaFile> missingFiles = new List<MediaFile>();
  ```

---

## 2. Namespaces & `using` Directives
- **File-Scoped Namespaces**: Always use file-scoped namespace declarations (`namespace ParityProof.Engine.Matching;`) to save horizontal indentation.
- **`using` Directive Placement & Ordering**: Place `using` directives outside the namespace at the top of the file. Sort `System` and `System.*` directives first, followed alphabetically by other namespaces. Remove unused directives.
- **Global Usings**: Confine global usings to dedicated files (e.g., `GlobalUsings.cs`) for ubiquitous namespaces only.

---

## 3. Braces, Line Width & Layout (Allman Style)
- **Allman Bracing**: Opening braces `{` must always be placed on their own line at the same indentation level as the parent declaration (classes, methods, properties, control flow statements).
- **Line Length Target**: Target line widths under 120 characters. Sensible exceptions apply to long URL strings, regex patterns, or attribute annotations.
- **Whitespace & Formatting**: Clean whitespace and blank lines to separate logical blocks or enhance clarity are encouraged.

---

## 4. Parameter & Invocation Wrapping
- **Multi-Line Signatures & Calls**: When a method signature, constructor definition, or method invocation wraps across lines, place each parameter/argument on its own line indented by 4 spaces:
  ```csharp
  public async Task<int> CopyMissingFilesAsync(
      IReadOnlyList<MediaFile> missingFiles,
      string destinationRootPath,
      IProgress<CopyProgressInfo>? progress = null,
      CancellationToken cancellationToken = default)
  {
      // ...
  }
  ```
- **Fluent / LINQ Chains**: Wrap multi-step LINQ or builder invocations so that each method call starts on a new indented line:
  ```csharp
  List<MediaFile> missing = files
      .Where(file => file.Status == MediaStatus.Missing)
      .OrderBy(file => file.RelativePath)
      .ToList();
  ```
- **Initializers**: Multi-line object and collection initializers should place each property/element on its own line with a trailing comma.

---

## 5. Expression-Bodied Members vs Block Bodies
- **Single-Line Members**: Use expression bodies (`=>`) for single-line properties, getters, indexers, and short single-line helper methods:
  ```csharp
  public bool IsVerified => Status == MediaStatus.Verified;
  public string GetDisplayText() => $"{RelativePath} ({FileLength} bytes)";
  ```
- **Multi-Line Members & Constructors**: Use full block bodies with braces for multi-line methods and all constructors.

---

## 6. Pattern Matching, Switch Expressions & Null Checking
- **Null Checking**: Use `is null` and `is not null` instead of `== null` / `!= null`.
- **Pattern Matching**: Prefer type pattern matching (`if (result is VerificationResultItem item)`) over `as` casting followed by null checks.
- **Switch Expressions**: Prefer switch expressions (`state switch { ... }`) when mapping or returning values over traditional `switch` statements:
  ```csharp
  string statusText = status switch
  {
      OverallSafetyStatus.SafeToFormat => "Safe to Format",
      OverallSafetyStatus.PartiallyBackedUp => "Partially Backed Up",
      _ => "Unsafe to Format"
  };
  ```

---

## 7. Naming, Modifiers & Qualification
- **Private Fields**: Prefix private and internal instance fields with an underscore and use `_camelCase` (e.g. `private readonly IIndexCache _indexCache;`).
- **Constants & Magic Numbers**: Constants (`const`) must use `CAPS_CASE` / `SCREAMING_SNAKE_CASE` (e.g. `public const int DEFAULT_CHUNK_SIZE = 64 * 1024;`, `private const int COPY_BUFFER_SIZE = 2 * 1024 * 1024;`). Avoid magic numbers throughout the codebase unless the meaning is immediately obvious from its context (such as standard math or index boundaries like `0` or `1`). Any non-obvious numeric values (including buffer sizes, chunk lengths, timeouts, bit shifts, and retry thresholds) must be declared as descriptive, all-caps `const` fields.
- **No `this.` Qualifier**: Avoid `this.` qualification unless strictly necessary to disambiguate shadowed identifiers.
- **Explicit Accessibility**: Always explicitly declare accessibility modifiers (`private`, `public`, `protected`, `internal`) on all types and members.
- **`readonly` Modifier**: Apply `readonly` to all fields and properties that are assigned only during declaration or in the constructor.

---

## 8. Modern Types & Primary Constructors
- **Records**: Use `sealed record` or `record struct` for immutable data models, DTOs, and event payloads (e.g. `MediaFile`, `CopyProgressInfo`).
- **Primary Constructors**: Use primary constructors for records/DTOs and concise service classes. Use traditional constructor blocks when initialization requires input validation, defensive copying, or complex setup logic.

---

## 9. High-Performance I/O & Memory Standards
- **Array Pooling & Spans**: For chunk hashing, file transfers, and streaming buffers, rent buffers from `ArrayPool<byte>.Shared` and use `Span<byte>` / `ReadOnlySpan<byte>` slices to minimize allocations on the garbage collector heap. Always return rented buffers in a `finally` block.
- **Direct Chunk I/O**: Prefer `RandomAccess.Read` and `SafeFileHandle` for high-performance direct chunk reads over repeated stream allocations.
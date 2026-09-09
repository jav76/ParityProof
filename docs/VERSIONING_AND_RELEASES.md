# ParityProof Versioning & Release Guide

This document outlines the Semantic Versioning architecture, GitHub Actions automation, Native AOT compilation matrix, and release distribution workflows for **ParityProof**.

---

## 1. Semantic Versioning Specification

ParityProof adheres to [Semantic Versioning 2.0.0](https://semver.org/):

$$\text{v}\mathbf{MAJOR}.\mathbf{MINOR}.\mathbf{PATCH}[-\mathbf{PRERELEASE}]$$

- **MAJOR**: Breaking behavioral changes, fundamental engine rewrites, or major workflow redesigns.
- **MINOR**: Backward-compatible new features (e.g., new file system providers, additional hash algorithms, export formats).
- **PATCH**: Backward-compatible bug fixes, performance optimizations, and security patches.
- **PRERELEASE**: Pre-release milestones denoted with a hyphen, such as `-alpha.1`, `-beta.2`, or `-rc.1`.

### Deterministic Versioning Architecture

1. **Local Development Default**:
   Defined in [Directory.Build.props](file:///home/jaret/Documents/GitHub/ImageVerifier/Directory.Build.props), every build defaults to `1.0.0-dev` if no version is explicitly supplied.
2. **Release Stamping**:
   During CI/CD release builds, the workflow extracts the clean version from the Git tag and stamps MSBuild properties:
   ```bash
   dotnet publish -p:Version="1.2.0-rc.1" -p:SourceRevisionId="abcdef1"
   ```
3. **Runtime Metadata (`BuildInfo`)**:
   [BuildInfo.cs](file:///home/jaret/Documents/GitHub/ImageVerifier/src/ParityProof.Core/Models/BuildInfo.cs) reads the compiled `AssemblyInformationalVersionAttribute`, providing:
   - `BuildInfo.Current.SemVer`: Clean SemVer (e.g. `1.2.0-rc.1` or `1.0.0-dev`).
   - `BuildInfo.Current.CommitSha`: 7-character Git commit SHA.
   - `BuildInfo.Current.DisplayVersion`: Formatted string (`ParityProof v1.2.0-rc.1 (abcdef1)`).
   - `BuildInfo.Current.IsDebug`: Boolean flag for debug configurations.

---

## 2. GitHub Actions Workflows

### Continuous Integration (`ci.yml`)
- **Trigger**: Automatic on `push` to `master`/`main` and any `pull_request`.
- **Jobs**:
  - `test`: Executes the unit test suite on Linux (`ubuntu-latest`) and collects code coverage.
  - `check-format`: Validates C# code style via `dotnet format --verify-no-changes`.
  - `cross-platform-build-check`: Compiles the project across Ubuntu, Windows, and macOS runners.

### Release Automation (`release.yml`)
- **Trigger**: Push of any Git tag matching `v*` (e.g., `v1.0.0`, `v1.0.0-rc.1`) or manual trigger via `workflow_dispatch`.
- **Prerelease Detection**: If the version string contains a hyphen (`-`), the GitHub Release is automatically flagged as a **Pre-release**.
- **Changelog**: Release notes are automatically compiled from merged pull requests and commit history (`generate_release_notes: true`).

---

## 3. Native AOT Build & Packaging Matrix

The release pipeline compiles standalone Native AOT binaries across **7 platform targets**:

| Platform Target | Runner OS | Architecture | Compressed Archive | Native OS Installer |
| :--- | :--- | :--- | :--- | :--- |
| `linux-x64` | `ubuntu-latest` | x64 (AMD64) | `ParityProof-v<VER>-linux-x64.tar.gz` | `parityproof_<VER>_amd64.deb` |
| `linux-arm64` | `ubuntu-24.04-arm` | ARM64 | `ParityProof-v<VER>-linux-arm64.tar.gz` | `parityproof_<VER>_arm64.deb` |
| `win-x64` | `windows-latest` | x64 (64-bit) | `ParityProof-v<VER>-win-x64.zip` | `ParityProof-v<VER>-win-x64.msi` |
| `win-arm64` | `windows-latest` | ARM64 | `ParityProof-v<VER>-win-arm64.zip` | `ParityProof-v<VER>-win-arm64.msi` |
| `win-x86` | `windows-latest` | x86 (32-bit) | `ParityProof-v<VER>-win-x86.zip` | `ParityProof-v<VER>-win-x86.msi` |
| `osx-arm64` | `macos-14` | Apple Silicon | `ParityProof-v<VER>-osx-arm64.tar.gz` | `ParityProof-v<VER>-osx-arm64.dmg` |
| `osx-x64` | `macos-13` | Intel Mac | `ParityProof-v<VER>-osx-x64.tar.gz` | `ParityProof-v<VER>-osx-x64.dmg` |

### Cryptographic Verification
Every release job aggregates all archives and installers, computing a unified `checksums-sha256.txt` manifest attached to the GitHub Release.

---

## 4. How to Cut a Release

### Step 1: Ensure Main Branch is Healthy
Ensure all pull request checks pass in [ci.yml](file:///home/jaret/Documents/GitHub/ImageVerifier/.github/workflows/ci.yml).

### Step 2: Create and Push Git Tag

For a production release:
```bash
git tag v1.0.0
git push origin v1.0.0
```

For a release candidate or pre-release:
```bash
git tag v1.0.0-rc.1
git push origin v1.0.0-rc.1
```

### Step 3: Automated Workflow Execution
The `release.yml` workflow will automatically:
1. Extract `1.0.0` or `1.0.0-rc.1`.
2. Compile Native AOT binaries across all 7 targets in parallel.
3. Generate `.zip`, `.tar.gz`, `.msi`, `.deb`, and `.dmg` assets.
4. Calculate SHA-256 hashes.
5. Create the GitHub Release with attached assets and release notes.

---

## 5. Local Native AOT Publish Commands

To compile Native AOT locally on your development workstation:

```bash
# Linux x64
dotnet publish src/ParityProof.App/ParityProof.App.csproj \
  -c Release \
  -r linux-x64 \
  -p:PublishAot=true \
  -p:Version="1.0.0-custom"

# Windows x64 (from Windows host)
dotnet publish src/ParityProof.App/ParityProof.App.csproj `
  -c Release `
  -r win-x64 `
  -p:PublishAot=true `
  -p:Version="1.0.0-custom"

# macOS ARM64 (from Apple Silicon host)
dotnet publish src/ParityProof.App/ParityProof.App.csproj \
  -c Release \
  -r osx-arm64 \
  -p:PublishAot=true \
  -p:Version="1.0.0-custom"
```

#!/usr/bin/env python3
"""
format-summary.py: Parses dotnet format JSON report and publishes
results to GitHub Actions Job Summary ($GITHUB_STEP_SUMMARY) and annotations.
"""

import json
import os
import sys
from pathlib import Path


def main():
    report_file = sys.argv[1] if len(sys.argv) > 1 else "format-report.json"
    summary_path = os.getenv("GITHUB_STEP_SUMMARY")
    workspace_root = Path.cwd()

    if not os.path.exists(report_file):
        output = [
            "## 🎨 Code Formatting Report",
            "",
            "> ⚠️ **Warning**: Formatting report file was not found. Formatting check may have encountered a build failure.",
            "",
        ]
        write_summary(summary_path, "\n".join(output))
        return

    try:
        with open(report_file, "r", encoding="utf-8") as f:
            data = json.load(f)
    except Exception as ex:
        output = [
            "## 🎨 Code Formatting Report",
            "",
            f"> ⚠️ **Warning**: Failed to parse formatting report JSON: {ex}",
            "",
        ]
        write_summary(summary_path, "\n".join(output))
        return

    if not isinstance(data, list) or len(data) == 0:
        output = [
            "## 🎨 Code Formatting Report",
            "",
            "| Status | Result |",
            "| :--- | :--- |",
            "| 🟢 **Passed** | All files adhere to formatting standards. No changes required. |",
            "",
            "*Verified with `dotnet format ParityProof.slnx --verify-no-changes --severity warn`*",
            "",
        ]
        write_summary(summary_path, "\n".join(output))
        return

    # Count issues and collect details
    files_with_issues = []
    total_violations = 0

    for item in data:
        raw_path = item.get("FilePath") or item.get("FileName", "Unknown")
        try:
            rel_path = Path(raw_path).resolve().relative_to(workspace_root).as_posix()
        except Exception:
            rel_path = Path(raw_path).name

        changes = item.get("FileChanges", [])
        total_violations += len(changes)

        files_with_issues.append({
            "path": rel_path,
            "changes": changes,
        })

        for change in changes:
            line = change.get("LineNumber", 1)
            col = change.get("CharNumber", 1)
            diag_id = change.get("DiagnosticId", "FORMAT")
            desc = change.get("FormatDescription", "Formatting violation")
            # GitHub Actions workflow error command
            print(f"::error file={rel_path},line={line},col={col},title={diag_id}::{desc}")

    # Build markdown summary
    output = [
        "## 🎨 Code Formatting Report",
        "",
        "| Status | Files with Issues | Total Violations |",
        "| :--- | :--- | :--- |",
        f"| 🔴 **Failed** | {len(files_with_issues)} | {total_violations} |",
        "",
        "### Violations Breakdown",
        "",
        "| File | Location | Diagnostic | Description |",
        "| :--- | :--- | :--- | :--- |",
    ]

    for file_info in files_with_issues:
        for change in file_info["changes"]:
            loc = f"`{change.get('LineNumber', 1)}:{change.get('CharNumber', 1)}`"
            diag = f"`{change.get('DiagnosticId', 'FORMAT')}`"
            desc = change.get("FormatDescription", "").replace("|", "\\|")
            file_link = f"`{file_info['path']}`"
            output.append(f"| {file_link} | {loc} | {diag} | {desc} |")

    output.extend([
        "",
        "### 🛠️ How to Fix",
        "Run the following command locally in your terminal to automatically format the solution:",
        "```bash",
        "dotnet format ParityProof.slnx",
        "```",
        "Then commit and push the updated files.",
        "",
    ])

    write_summary(summary_path, "\n".join(output))


def write_summary(summary_path: str | None, content: str):
    if summary_path:
        with open(summary_path, "a", encoding="utf-8") as f:
            f.write(content)
            f.write("\n")
    else:
        print("\n--- [GITHUB_STEP_SUMMARY Preview] ---")
        print(content)
        print("------------------------------------\n")


if __name__ == "__main__":
    main()

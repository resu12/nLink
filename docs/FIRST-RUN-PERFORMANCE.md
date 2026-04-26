# First-Run Performance (Windows)

`nLink` can feel slower on the very first run after install or after extracting a fresh portable build.

## Why first run can be slower

- Windows Defender / antivirus often scans newly written executables, DLLs, `node.exe`, and bundled bridge files on first execution.
- SmartScreen / reputation checks can add one-time startup delay.
- The app and bundled runtime also pay normal first-run costs (JIT, caches, file extraction/initialization).

## What to expect

- First startup can be noticeably slower than later launches.
- First NKN bridge cold start can be slower than warm/reused starts.
- Later runs usually improve once AV and OS caches are warm.

## Best practices (optional)

- Be patient on the first launch before concluding startup is hung.
- If your environment allows it, use an antivirus exclusion for the app install/portable folder and `%LOCALAPPDATA%\\nLink` artifacts/logs.
- Prefer testing performance after one warm-up launch.
- Use Diagnostics copy/export and metrics to compare first cold start vs later runs; see `docs/supportability.md` before sharing evidence.

## Diagnostics note

- `Diagnostics` now includes a one-time first cold bridge start annotation (`bridge_first_cold_start_*`) and metric (`bridge_cold_start_ms`) for awareness only.
- This is diagnostic-only and does not change runtime behavior.

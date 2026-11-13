# Third-party notices

Watcher's own source code is licensed under AGPL-3.0 (see [LICENSE](LICENSE))
or a commercial license (see [COMMERCIAL.md](COMMERCIAL.md)). The components
below are distributed with Watcher or used by it, under their own licenses.

## Bundled in release artifacts

| Component | Where | License |
|---|---|---|
| [FFmpeg](https://ffmpeg.org/) — gyan.dev "essentials" build | Windows agent (`ffmpeg.exe`) | GPL-3.0-or-later |
| [FFmpeg](https://ffmpeg.org/) — FFmpeg-Builds static GPL build | Server Docker image (transcoding) | GPL-3.0-or-later |
| [.NET 8 runtime](https://github.com/dotnet/runtime) | Self-contained agents, server image | MIT |
| [Preact](https://preactjs.com/) | Web console (`wwwroot/vendor`) | MIT |
| [htm](https://github.com/developit/htm) | Web console (`wwwroot/vendor`) | Apache-2.0 |
| [xterm.js](https://xtermjs.org/) | Web console (`wwwroot/vendor`) | MIT |
| [Caddy](https://caddyserver.com/) | Reverse proxy container | Apache-2.0 |

FFmpeg runs as a separate process; Watcher does not link against it. The
FFmpeg source code corresponding to the bundled binaries is available from the
upstream projects: [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) and
[FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds). Exact versions and
checksums are pinned in `tools/fetch-ffmpeg.sh` and
`tools/fetch-ffmpeg-linux.sh`.

## NuGet packages

| Package | License |
|---|---|
| Microsoft.Data.Sqlite, SQLitePCLRaw | MIT (SQLite itself is public domain) |
| Microsoft.Extensions.* | MIT |
| System.Diagnostics.EventLog, System.Drawing.Common, System.ServiceProcess.ServiceController | MIT |
| Novell.Directory.Ldap.NETStandard | MIT |
| Serilog.Extensions.Logging, Serilog.Sinks.Console, Serilog.Sinks.File | Apache-2.0 |

A full SBOM (CycloneDX) for NuGet dependencies and the server image is
attached to every GitHub release.

## Android agent

Uses only the Android SDK and Kotlin standard library (Apache-2.0). Test-only
dependencies (JUnit, AndroidX Test, org.json) are not shipped.

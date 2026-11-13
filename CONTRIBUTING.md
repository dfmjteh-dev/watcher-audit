# Contributing to Watcher

Thanks for your interest! Issues and pull requests are welcome in English or
Russian.

## Reporting bugs

Open an issue with:

- server and agent versions (web console → Settings → Server; host page);
- platform (Windows build, Linux distribution, Android version);
- what you expected and what happened, plus relevant log excerpts.

Do not attach recordings, screenshots or logs that contain personal data of
your users. For security vulnerabilities see [SECURITY.md](SECURITY.md).

## Pull requests

1. Discuss larger changes in an issue first.
2. Branch from `main`, keep the change focused.
3. Add tests for new logic; update the documentation in the same PR when
   config keys, endpoints or the database schema change; add an entry to
   `CHANGELOG.md` under `[Unreleased]`.
4. Make sure the tests below pass.
5. Agree to the [Contributor License Agreement](CLA.md) — tick the box in the
   pull request template. Without it the PR cannot be merged, because Watcher
   is also offered under a commercial license.

## Building from source

Requirements: .NET 8 SDK; Node.js 20+ only for the web UI end-to-end tests;
JDK 17 and Android SDK (compileSdk 35) only for the Android agent.

```bash
dotnet build Watcher.sln -c Release
dotnet test Watcher.sln -c Release          # Windows-only projects are skipped on Linux

# Web UI end-to-end tests (Playwright starts the built server itself)
dotnet build src/Watcher.Server -c Release
cd tests/e2e && npm ci && npx playwright install chromium
E2E_CONFIGURATION=Release npx playwright test

# Android agent
cd android && ./gradlew testDebugUnitTest assembleDebug
```

The web UI needs no build step: `src/Watcher.Server/wwwroot` is plain ES modules.
Installer bundles are produced by `make-server-bundle.sh`; see `install-server.sh --help`.

## Translations

The web console strings are in `src/Watcher.Server/wwwroot/i18n.en.js` (keyed by
the Russian source string). A missing translation is reported by the e2e test
`tests/e2e/specs/i18n.spec.js`.

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
3. Follow [docs/development.md](docs/development.md): tests for new logic,
   documentation updated in the same PR when config keys, endpoints or the
   database schema change, an entry in `CHANGELOG.md` under `[Unreleased]`.
4. Make sure `dotnet test Watcher.sln` passes.
5. Agree to the [Contributor License Agreement](CLA.md) — tick the box in the
   pull request template. Without it the PR cannot be merged, because Watcher
   is also offered under a commercial license.

## Translations

The web console strings are in `src/Watcher.Server/wwwroot/i18n.en.js` (keyed by
the Russian source string). A missing translation is reported by the e2e test
`tests/e2e/specs/i18n.spec.js`.

# Security policy

## Supported versions

Security fixes are released for the latest minor version of the server and the
agents. Upgrade to the latest release before reporting.

## Reporting a vulnerability

Please **do not open a public issue**. Report privately through
[GitHub Security Advisories](https://github.com/dfmjteh-dev/watcher-audit/security/advisories/new).

Include the affected version, the component (server, Windows/Linux/Android
agent, installer), reproduction steps and the impact you observed. You will get
an acknowledgement within 7 days. Please allow time for a fix to be released
before disclosing details publicly.

## Verifying releases

Every release ships `SHA256SUMS` signed with Sigstore cosign by the release
workflow of this repository. See [README.md](README.md#quick-start) for the
verification command.

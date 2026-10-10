# Security policy

Please do not report security vulnerabilities through public GitHub issues.

Report them privately through the contact form at
[web-engineer.co.uk/contact](https://web-engineer.co.uk/contact). Include what
you found, how to reproduce it, and the plugin and companion extension versions.
We will acknowledge your report and keep you updated while we fix it.

The companion extension accepts commands from the plugin over a local socket or
named pipe, guarded by a per-window token; the
[README](README.md#security-model) describes the model. Reports about that
channel, the registration files or the command policy are especially welcome.

Fixes ship in the latest release only, so please update before reporting.

For bugs and feature requests that are not security issues, open a
[GitHub issue](https://github.com/web-engineer/LoupixDeck.Plugin.VSCode/issues).

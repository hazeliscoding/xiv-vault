# Security

XIV Vault reads and writes settings people spent years building, so a report about its safety gets my attention first.

## Reporting a problem

Report it privately with **Report a vulnerability** on the [Security tab](https://github.com/hazeliscoding/xiv-vault/security), not in a public issue.

Say which version you used, what you did and what happened. A small made-up backup that shows the problem helps most. Please don't send your own backups, configuration files or logs, since they can hold details about your characters.

I maintain XIV Vault in my spare time. I'll reply as soon as I can and tell you when a fix is released. Reporters are credited in the release notes unless they'd rather not be.

## Supported versions

Fixes go into the latest stable release and the latest beta. Older versions don't get fixes, so update first if you can.

## What counts

Anything that breaks the [safety model](README.md#safety-model), for example:

- a backup that makes a restore write outside the allowed files, or into another character's folder
- a restore that changes files without a safety backup first, or runs anything from a backup
- configuration contents or a character's content ID in the logs or the diagnostic report
- an update, or a beta's plugin install, that downloads from somewhere other than the places the docs name

Bugs in XIVLauncher, Dalamud or a plugin belong to those projects, since XIV Vault only backs up their settings. Other XIV Vault bugs can go in a [regular issue](https://github.com/hazeliscoding/xiv-vault/issues/new/choose).

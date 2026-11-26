# Security policy

**English** · [Português](SECURITY.pt-BR.md)

## Reporting a vulnerability

Please **do not open a public issue** with the details of a security problem.

1. Use GitHub's private report: **Security → Report a vulnerability** on the
   [repository page](https://github.com/rafaelbruske/Apparatus/security/advisories/new).
2. If that page is not available, contact the maintainer privately through their GitHub profile and ask for a private way
   to send the details.

Include, if you can:

- the Apparatus version (Settings → Sobre) and the Windows version (`winver`);
- the steps to reproduce the problem and what you think an attacker could do with it;
- a proof of concept, if you have one.

**Do not attach real API keys, clipboard history or other personal data.** Use fake values.

You can write in English or Portuguese.

## What to expect

Apparatus is a personal project kept by one person, so there is no guaranteed response time. I will try to answer within a
few days, tell you whether I consider it a vulnerability, and fix it in a new version. Please give me time to release the fix
before you publish the details; I am glad to credit you in the release notes if you want.

## Supported versions

The project is at version 0.9.x. Only the **latest release** receives fixes; update to it before reporting if you can.

## What counts as a vulnerability

Apparatus keeps a few promises (see [CONTRIBUTING.md](CONTRIBUTING.md#principles-the-project-keeps)). Breaking one of them is
a security problem, for example:

- the AI key leaves its DPAPI-encrypted store, or shows up in settings, backups, sync folders or logs;
- something proposed by the AI assistant runs without the user clicking it, or an action or macro runs a command that is
  outside the closed lists the code validates;
- data leaves the PC without the user having turned the feature on;
- the app injects code into Explorer or other programs, or leaves a Windows setting changed after the feature that needed it
  was turned off;
- the installer or the update check can be made to run code the user did not choose.

## Known limits (not vulnerabilities)

These are documented decisions, listed in [`docs/PENDENCIAS.md`](docs/PENDENCIAS.md) (section 5, in Portuguese):

- Extensions run with the user's permissions, without isolation; the protection is that the user turns each one on.
- The `.apparatus` backup and the sync folder are **not encrypted**.
- The clipboard history is protected from other accounts and stolen disks, not from a program running as the same user.
- Macro recording does not detect password fields in browsers.
- The installer is not code-signed, so SmartScreen warns on the first run.

A report about one of these is still welcome as an improvement idea, but through a normal issue.

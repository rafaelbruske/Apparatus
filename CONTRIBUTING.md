# Contributing to Apparatus

**English** · [Português](CONTRIBUTING.pt-BR.md)

Thanks for wanting to help. Apparatus is a Windows 11 utility (launcher, snippets, actions and macros, its own Start menu
and an optional AI assistant), written in C# with WinUI 3. Bug reports, ideas, documentation fixes and pull requests are
all welcome, in English or Portuguese.

By contributing you agree that your contribution is released under the project license, the
[BSD Zero Clause License](LICENSE) (0BSD), which does not require credit or any notice to be kept.

## Before you start

- **Bugs and ideas:** open an issue first for anything bigger than a small fix, so we can agree on the direction before
  you spend time on it.
- **Questions about how something works:** the decisions and what each phase delivered are in [`docs/`](docs), written in
  Portuguese. [`docs/PENDENCIAS.md`](docs/PENDENCIAS.md) lists what is known to be missing or unverified.
- **Security problems:** please do **not** open a public issue with details. Use GitHub's private vulnerability report
  if it is enabled for the repository; otherwise contact the maintainer privately through GitHub. The details are in
  [`SECURITY.md`](SECURITY.md).

The **New issue** button offers a form for a bug report and another for a feature request (both bilingual); the fields ask
for what is needed below. When you report a bug, include the Apparatus version, your Windows build (`winver`), the steps to reproduce it and what
you expected. The log is in `%LOCALAPPDATA%\Apparatus\logs`. **Never paste an API key, clipboard history or other
personal text** into an issue; remove it from the log first.

## Setting up

You need Windows 11 and the .NET SDK version in [`global.json`](global.json) (.NET 10). The UI uses WinUI 3 through the
Windows App SDK, which is restored as NuGet packages (self-contained), so no extra workload should be needed.

```powershell
git clone https://github.com/rafaelbruske/Apparatus.git
cd Apparatus
dotnet build src/Apparatus.App/Apparatus.App.csproj
dotnet test tests/Apparatus.Core.Tests
dotnet run --project src/Apparatus.App
```

To try the app without touching your real settings, snippets and actions, point it at a throw-away profile folder with
the environment variable `APPARATUS_PROFILE_DIR`. Use a different launcher hotkey in that profile if the default
(`Alt+Space`) is already taken by the copy you use every day.

## How the code is organised

| Folder | What lives there |
|---|---|
| `src/Apparatus.Core` | Everything that does not need a window: search, snippets, actions, backup, Start menu data, the AI assistant engine, the screen capture geometry. Plain `net10.0`, no UI, no P/Invoke. **Put new logic here.** |
| `src/Apparatus.Native` | Windows API calls (P/Invoke, shell, hooks). Keep it thin; no business rules. |
| `src/Apparatus.App` | The WinUI 3 app: windows, controls, tray, wiring. Keep it thin too. |
| `tests/Apparatus.Core.Tests` | The unit tests (xUnit). |
| `tests/Apparatus.E2E.*`, `tests/Apparatus.UI` | Checks that press real keys, click, or drive the interface. Each says in its header what it does; do not touch the keyboard or mouse while one runs. |
| `build/` | The release script ([`release.ps1`](build/release.ps1)): a self-contained publish packed with Velopack. |
| `docs/` | Plans, per-phase notes, extension guide, pending items. |

## Code style

- C# with nullable reference types enabled (see [`Directory.Build.props`](Directory.Build.props)). Follow the style of the
  file you are editing.
- Identifiers and code comments are in **English**. Text the user sees on screen is in **Brazilian Portuguese** for now.
- Prefer a small testable class in `Apparatus.Core` over logic inside a window class.
- Do not add a dependency without a good reason; mention it in the pull request (the installer is about 80 MB already). If you add or update one, check its license and update [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md), which the installer carries, together with [`third-party-licenses.json`](src/Apparatus.Core/Resources/third-party-licenses.json), which feeds Settings → Sobre (a unit test checks that the two lists agree).

## Principles the project keeps

These are easy to break by accident, so please check your change against them:

- **Nothing is injected** into Explorer or other programs; only public Windows APIs are used.
- **Nothing leaves the PC without the user opting in.** The AI assistant is off by default, uses the user's own key
  (stored encrypted with DPAPI, never in settings, backups or sync folders), and sends only what the user types.
- **The AI only suggests.** Tool calls are validated in C# against closed lists and become buttons; nothing runs until the
  user clicks. Proposed actions never contain commands or PowerShell. See [`docs/ASSISTENTE-IA.md`](docs/ASSISTENTE-IA.md).
- **The Windows Start menu must always be recoverable** if the replacement misbehaves.
- **Secrets stay out of the repository** (keys, tokens, `.env` files).

## Tests

- Add or update unit tests in `tests/Apparatus.Core.Tests` for any logic you add or change. Run `dotnet test
  tests/Apparatus.Core.Tests` before you open the pull request; it should be green.
- Some tests are opt-in because they call a real service. For example, the one that asks the reference questions of
  [`assistant-eval.json`](tests/Apparatus.Core.Tests/assistant-eval.json) to a real AI provider only runs when you set a key
  in an environment variable such as `APPARATUS_TEST_GEMINI_KEY`; without it, it returns at once.
- If you change something you cannot test automatically (appearance, real keyboard, high DPI), say what you checked by
  hand and what you did not. The docs follow that habit: they separate "verified" from "not verified".

## Pull requests

1. Fork the repository and create a branch from `master`.
2. Keep the change focused: one fix or feature per pull request.
3. Write the commit message as a short sentence saying what changed and why (the history uses Portuguese verbs such as
   "Adiciona…" and "Corrige…"; English is fine too).
4. Update the docs if behaviour changes: user-facing text in `README.md` / `README.pt-BR.md`, and the relevant note in `docs/`.
5. In the pull request, describe what you changed, how you tested it and what you could not test. The description opens with
   a template ([`.github/PULL_REQUEST_TEMPLATE.md`](.github/PULL_REQUEST_TEMPLATE.md)) that asks for exactly that, plus a checklist of the rules above.

If you are an AI coding assistant, read [`CLAUDE.md`](CLAUDE.md) first: it lists the project rules, how to test safely and what to update.

Please be kind and patient in issues and reviews. Disagree with the idea, not with the person.

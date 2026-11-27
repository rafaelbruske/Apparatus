# Apparatus

**English** · [Português](README.pt-BR.md)

A Windows 11 utility that brings together, in a single program living in the tray:

- **Launcher** (Raycast style): `Alt+Space` (configurable) opens a box that searches apps, files, Windows settings, clipboard history, snippets, actions and extensions, and does math and unit conversions.
- **Snippets** (TextExpander style): `;email` turns into your e-mail address in any program, with variables (`{date}`, `{clipboard}`, `{cursor}`) and fill-in fields.
- **Actions and macros** (AutoHotkey style): global or per-program hotkeys (the numeric keypad included) that open things, send keys, manage windows, and control volume and brightness. To open a program, type its name and pick it from the installed applications, or give a path. Macros can be **recorded** from the keyboard.
- **Its own Start menu** (StartAllBack style): pinned items in sections, all apps, search, shortcuts and power, opened with the Windows key or the Start button. The taskbar stays the Windows one.
- **Screen capture** (Lightshot style): `Print Screen` freezes the screen, you drag the area and annotate it (pen, line, arrow, rectangle, highlighter, text) with one toolbar, then copy, save or print. Optional; turning it on switches off the Windows option that opens the Snipping Tool on `Print Screen`, and turning it off puts it back. See [docs/CAPTURA.md](docs/CAPTURA.md).
- **Windows 11 taskbar tweaks** through your own user's registry, each one with an exact undo.

Nothing is injected into Explorer or other programs: Apparatus only uses public Windows APIs, so a Windows update does not break it silently. If the Start menu replacement stops working after an update, the **Windows menu comes back by itself** (see below).

> The documentation in `docs/` is written in Portuguese. The program's interface is also in Portuguese (Brazil).

## Install

```powershell
dotnet tool install -g vpk --version 1.2.161
.\build\release.ps1
```

The version comes from `<Version>` in [`Directory.Build.props`](Directory.Build.props), the only place where it is written.

**Install by double-clicking `Setup.exe` in File Explorer.** Do not run it from a terminal or from tools inside the Claude app: whatever runs there ends up in a private copy that Windows cannot see (see `docs/PENDENCIAS.md`). The installer is produced at `artifacts\releases\ApparatusApp-win-Setup.exe` (about 80 MB). It installs to `%LOCALAPPDATA%\ApparatusApp` and does not need administrator rights. Your settings and data live in `%APPDATA%\Apparatus` and `%LOCALAPPDATA%\Apparatus`, **outside** the install folder: uninstalling or updating does not delete them.

Without code signing, SmartScreen warns on the first run ("More info → Run anyway"). To sign, pass `-SignParams` to the script.

To update, point Settings → Updates at the `artifacts\releases` folder (or a web address with the same files), or click **Usar o GitHub** to follow the releases published on GitHub. Nothing is checked until you choose a source. With GitHub, Apparatus asks `api.github.com` once a day (and when you ask) which release is the newest and compares it with the installed version on your PC, so GitHub only sees the ordinary request (your IP address), not your version. Apparatus tells you when a new version is available and you decide when to install it.

To run from source: `dotnet run --project src/Apparatus.App`.

## Using the Apparatus Start menu

1. Right-click the tray icon and check **"Windows key opens the Apparatus menu"** (or go to Settings → Apparatus Start menu).
2. Tap the Windows key, or click the Start button. `Win+E`, `Win+D` and the other combinations keep working as always. `Ctrl+Esc` opens the original menu whenever you want.
3. To pin something: search for it and press `Ctrl+P` (also in the launcher), or use the right button.

### The Windows menu comes back by itself if something goes wrong

Apparatus watches itself and hands the menu back to Windows, with nothing for you to do, when:

- **the program hangs or closes**: without it, the keyboard hook is gone and the Windows key goes back to Windows immediately; if only the interface hangs, hotkeys stop being intercepted within 2 seconds;
- **the Apparatus menu does not appear** within 1.5 s after the tap: Apparatus opens the Windows one (`Ctrl+Esc`);
- **Windows was updated and its own menu opens together with the Apparatus one** (seen for real: Apparatus recognizes the Windows 11 26300 menu, which is a "Search" window of `SearchHost`) (the technique that hides the Windows key stopped working): on the first occurrence after an update, or the second in a row on an already verified Windows, the replacement is **turned off** and the reason appears in Settings, with a "Try again" button;
- **an exclusive full-screen game or a presentation** is in the foreground: Windows decides.

### Windows running as administrator

Windows prevents an ordinary program from seeing the keys typed in elevated windows. While one of them is in the foreground, snippet expansion, macro recording and the Apparatus Windows key do not reach it (the Windows key falls back to the Windows menu, as above). See `docs/FASE7.md` for the options.

## Security and privacy

- Clipboard history is optional, text only, and **encrypted** (AES-GCM, key protected by Windows DPAPI); it is never written in clear text.
- Extensions are programs that run with your permissions and never run unless you enable them.
- Imported actions can run programs: import only what you trust.
- Macro recording ignores Windows password fields, but does **not** detect the ones in browsers.
- The **AI assistant** is optional and ships turned off. When on, what you type after `Tab` in the launcher is sent to the provider you chose (or to local Ollama, which never leaves the PC); the names of your actions are only sent if you allow it. The API key is encrypted with DPAPI in `assistant.keys`, outside the settings, the backup and the sync folder. The assistant only *suggests*: nothing runs until you click the card. See `docs/ASSISTENTE-IA.md`.
- The `.apparatus` backup and the sync folder do not include clipboard history and are not encrypted.

## More

`docs/PLANO.md` describes the plan and the decisions; `docs/FASE0-RESULTADOS.md` through `docs/FASE7.md` cover what each phase delivered, how it was verified and what was left out. `docs/EXTENSOES.md` explains how to write an extension and `docs/CAPTURA.md` documents the screen capture module. `docs/PENDENCIAS.md` lists what is still missing (including use in administrator windows) and `docs/ASSINATURA.md` explains code signing. All of these are in Portuguese.

## Tests

```powershell
dotnet test tests/Apparatus.Core.Tests          # unit tests
dotnet run --project tests/Apparatus.E2E.Start  # safe integration (test registry, Start button, hook)
```

There are also checks that really press keys and click (snippets, Windows key, Start button, emergency plan, macro recording); each one states in its own code header what it does and that you must not touch the keyboard and mouse while it runs.

## Contributing and license

See [`CONTRIBUTING.md`](CONTRIBUTING.md) ([Português](CONTRIBUTING.pt-BR.md)). The project is released under the [BSD Zero Clause License](LICENSE) (0BSD): you may use, copy, modify and distribute it for any purpose, without having to give credit ([Português](LICENSE.pt-BR.md)).

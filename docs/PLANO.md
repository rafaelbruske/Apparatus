# Apparatus — Plano de desenvolvimento

Ideia original: [ideia.txt](ideia.txt). Este documento consolida a avaliação das tecnologias e o plano de fases.

## 1. Princípio central: tudo é uma Ação

Uma `Action` (id, nome, tipo, parâmetros em JSON, ícone, tags) pode ser: abrir app, rodar script,
colar snippet, comando de janela ou macro. As **ligações** a ela são separadas:

- atalho de teclado (módulo B);
- alias ou resultado de busca no launcher (módulo C);
- gatilho `;email` (módulo D);
- item fixado no Start (módulo A).

Uma macro é uma ação cujos passos são outras ações. Launcher, gerenciador de atalhos e Start são três
telas sobre o mesmo catálogo.

## 2. Decisões de tecnologia

| Tecnologia | Decisão | Observações |
|---|---|---|
| C# + .NET | Manter | **.NET 10 (LTS)**. P/Invoke gerado com CsWin32. |
| UI | WinUI 3 *condicionado a spike*; plano B: WPF | Gate da Fase 0: janela do launcher aparece em < ~50 ms. |
| Atalhos | `RegisterHotKey` + hook `WH_KEYBOARD_LL` (híbrido) | `RegisterHotKey` para o comum; hook só para Win sozinha, sequências, remap, expansão de snippets, escopo por app. |
| Banco | SQLite (`Microsoft.Data.Sqlite`) + FTS5 + Dapper | Evitar EF Core. |
| Automação | PowerShell como processo externo (`-NoProfile`) | Não embutir o SDK do PowerShell. |
| Busca de arquivos | Índice do Windows Search (OLE DB `SystemIndex`) | Everything (voidtools) como provedor opcional. Indexador próprio só para apps/ranking. |
| Apps instalados | Enumerar `shell:AppsFolder` | Cobre Win32 e UWP com AUMID. |
| Configurações do Windows | Catálogo JSON curado de URIs `ms-settings:` | |
| Ranking | Fuzzy próprio (estilo fzf) + frecency em SQLite | |
| Extensões | Processo separado, JSON via stdio | Sem plugins .NET em AssemblyLoadContext no início. |
| App | `Microsoft.Extensions.Hosting`, CommunityToolkit.Mvvm, Serilog, xUnit, H.NotifyIcon, Velopack | Não contar com `.exe` único do WinUI 3. |

## 3. Módulo Start (A): abordagem

O Start do Windows 11 vive no `StartMenuExperienceHost` e na barra XAML do `explorer.exe`. StartAllBack e
ExplorerPatcher injetam código no Explorer e quebram a cada atualização grande. **Decisão: substituir, não
remendar. Sem injeção.**

- **A1 (estável):** menu próprio aberto pela tecla Win (hook que suprime o Start nativo), painel de
  atalhos e pastas, ajustes de barra por registro (reversíveis).
- **A2 (experimental, opcional):** barra de tarefas própria como AppBar (`SHAppBarMessage`), escondendo a
  original. A área de notificação (tray) é a parte difícil.

## 4. Estrutura

```
Apparatus.sln
src/Apparatus.Core      domínio, ações, SQLite, configurações (sem UI)
src/Apparatus.Native    CsWin32: hotkeys, hooks, input, shell, janelas
src/Apparatus.App       WinUI 3: bandeja, launcher, configurações
tests/Apparatus.Core.Tests
spikes/                 protótipos descartáveis da Fase 0
docs/
```

Processo único na bandeja, instância única, thread dedicada para o hook. Configurações em JSON, dados em
SQLite, com exportar e importar para backup.

## 5. Fases

Status: **Fase 0 concluída** ([resultados](FASE0-RESULTADOS.md)); **Fase 1 concluída** ([detalhes](FASE1.md)); **Fase 2 concluída** ([detalhes](FASE2.md)); **Fase 3 concluída** ([detalhes](FASE3.md)); **Fase 4 concluída** ([detalhes](FASE4.md), [guia de extensões](EXTENSOES.md)); **Fase 5 concluída** ([detalhes](FASE5.md)); **Fase 6 concluída, sem o AppBar** ([detalhes](FASE6.md)).

| Fase | Entrega | Critério de saída |
|---|---|---|
| **0. Fundação e spikes** | Solução, `.gitignore`, spikes: latência da janela WinUI, `Alt+Space`, supressão da tecla Win, consulta ao Windows Search | WinUI < ~50 ms ou troca por WPF; spikes aprovados |
| **1. Launcher (C)** | Atalho, janela pré-aquecida, apps, arquivos, `ms-settings`, web, calculadora, frecency, autostart | Uso diário no lugar da busca do Windows |
| **2. Snippets (D)** | CRUD, FTS5, variáveis (`{date}`, `{clipboard}`, `{cursor}`), campos preenchíveis, expansão automática | `;email` expande em qualquer app comum |
| **3. Ações e atalhos (B)** | UI do catálogo, atalhos com escopo por app, editor de macros, janelas, volume, brilho | Criar "Abrir Photoshop" sem escrever script |
| **4. Extensões** | Protocolo stdio JSON, quicklinks, histórico de clipboard, favoritos | Extensão de exemplo em PowerShell |
| **5. Start A1** | Menu próprio e ajustes por registro | Win abre o menu próprio, sem regressão |
| **6. Opcionais** | AppBar (A2), gravação de macros, sincronização | À parte |

## 6. Riscos

- Antivírus pode sinalizar hooks de baixo nível e `SendInput`. Mitigar com assinatura do binário.
- UIPI: hooks e `SendInput` não atuam sobre janelas elevadas. Helper elevado opcional (Fase 3).
- Atualizações do Windows quebrando o Start. Reduzido por não usar injeção.
- Índice do Windows Search desativado ou parcial. **Confirmado na máquina de desenvolvimento: só o `C:`
  é indexado**, então o Everything (ou varredura própria) deixa de ser opcional. Ver `FASE0-RESULTADOS.md`.
- `Alt+Space` conflita com Raycast e PowerToys Run (confirmado: o Raycast ocupa o atalho na máquina de
  desenvolvimento). Atalho padrão alternativo, configurável, com aviso quando o registro falhar.
- ABNT2 e teclas mortas na expansão de snippets: usar `ToUnicodeEx` para rastrear o buffer.
- Prior art a estudar: Flow Launcher, PowerToys Command Palette, Espanso, AutoHotkey, RetroBar.

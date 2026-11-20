# Fase 0 — Resultados dos spikes

Máquina de teste: Windows 11 build 26200 (25H2), .NET 10.0.13, Windows App SDK 2.x (self-contained).
Os spikes ficam em `spikes/` e são descartáveis.

## Resumo

| Spike | Resultado | Status |
|---|---|---|
| Latência do `RegisterHotKey` | mediana **0,9 ms** (25/25 entregues) | ✅ |
| `Alt+Space` disponível | **Não**: erro 1409, o **Raycast** já o registra | ⚠️ decisão de produto |
| Hook de tecla Win (`WH_KEYBOARD_LL`) | Toque isolado detectado (8/8 no teste manual), Start **não abriu**, `Win+E` funcionou | ✅ confirmado manualmente |
| Latência da janela WinUI 3 (pré-criada) | mediana **11,3 ms** (máx. 21 ms) | ✅ gate de < 50 ms |
| Latência da janela WPF (pré-criada) | mediana **10,7 ms** (máx. 20 ms) | ✅ equivalente |
| Windows Search (OLE DB) | consulta quente **2–13 ms** | ⚠️ só indexa o `C:` nesta máquina |
| `shell:AppsFolder` | 273 apps em 580 ms (frio), 334 ms (quente) | ✅ para cache, não por tecla |

## Detalhes e decisões

### Atalho global
- `RegisterHotKey` entrega em ~1 ms. O custo do atalho não é problema.
- `Alt+Space` está ocupado pelo Raycast (processo `Raycast` rodando). O Apparatus precisa de um atalho
  padrão alternativo (sugestão: `Ctrl+Alt+Space`) e de aviso claro quando o registro falhar (erro 1409).
- Existe também o processo `Raycast.UIAccess`: o Raycast usa um helper com **UIAccess** para contornar o
  UIPI (janelas elevadas). Vale considerar o mesmo caminho para o helper elevado da Fase 3
  (exige binário assinado e instalado em local protegido, como `Program Files`).

### Tecla Win
- O hook viu o toque isolado (1 evento) e não contou `Win+F13` (0 eventos), e o mascaramento com a tecla
  virtual `0xE8` foi enviado.
- O teste automático não serviu como prova: o grupo de controle (sem hook) também não detectou o Start
  aberto, então a detecção por "janela visível sem *cloak*" não é confiável no 25H2.
- **Confirmado manualmente** (`dotnet run --project spikes/Spike.Hotkeys -- --interactive`, 20 s): 8 toques
  isolados detectados, o Start **não abriu** em nenhum e `Win+E` continuou funcionando. A técnica
  (hook + tecla virtual `0xE8` para mascarar o Start) é viável para o Start próprio (módulo A1) no 25H2.
- Não testado: Win direita (`VK_RWIN`), teclas Win mantidas pressionadas e interação com atalhos de
  terceiros (por exemplo, o hook do Raycast convivendo com o nosso).

### WinUI 3 vs WPF
- Com a janela **criada uma vez e só escondida/mostrada**, os dois ficam em ~11 ms (um quadro a 60 Hz).
  O risco de latência do WinUI 3 levantado no plano **não se confirmou**.
- Custo só na inicialização: WinUI 3 levou ~1,1 s para construir a janela e ~265 ms para o primeiro
  quadro; WPF ~130 ms e ~17 ms. Aceitável se a janela for criada ao iniciar o app, em segundo plano.
- Saída self-contained do WinUI 3: **232 MB** em disco. Considerar *framework-dependent* + instalador que
  instala o runtime, ou *trimming*. O WPF é muito menor.
- O projeto WinUI 3 **compila e roda via `dotnet build`**, sem Visual Studio. Aplicativo só com código
  (sem `App.xaml`) **falha** com exceção XAML (0xc000027b); use `App.xaml` com `XamlControlsResources`.
- **Decisão: manter WinUI 3.** O WPF segue como plano B, sem ganho de latência a justificar a troca.

### Busca de arquivos
- O índice do Windows Search responde em poucos ms, mas **só cobre o `C:`** nesta máquina. Os projetos
  ficam em `D:`, que não é indexado (consulta com `SCOPE='file:D:\'` sem resultados).
- **Impacto no plano:** a Fase 1 não pode depender só do Windows Search. Opções: (a) orientar o usuário
  a adicionar `D:`/`E:` ao índice; (b) provedor **Everything** (voidtools), que cobre NTFS inteiro;
  (c) varredura própria de pastas escolhidas. Recomendo (b) como padrão opcional e (a) como fallback.

### Apps instalados
- `shell:AppsFolder` lista Win32 e UWP, mas a enumeração leva centenas de ms. Deve rodar em segundo plano,
  com cache em SQLite e atualização periódica ou por evento.
- A heurística "AUMID com `!` = UWP" não é confiável (Firefox, por exemplo, tem AUMID sem `!`).

## Próximos passos
1. ~~Confirmar a tecla Win com o modo `--interactive`~~ (feito: aprovado).
2. Escolher o atalho padrão do launcher (Raycast ocupa `Alt+Space`).
3. Iniciar a Fase 1: `Apparatus.App` (WinUI 3 com `App.xaml`), bandeja, janela pré-aquecida, provedores
   de apps e arquivos.

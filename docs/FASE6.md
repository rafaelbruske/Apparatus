# Fase 6 — Opcionais: gravação de macros, backup e sincronização

A Fase 6 do plano era "à parte": AppBar (A2), gravação de macros e sincronização. Foram feitas as duas últimas. A barra de
tarefas própria (A2) **não foi feita**, de propósito (veja o fim).

## O que foi entregue

### Gravação de macros do teclado
- **Como usar.** Ações e atalhos → Nova ação → "Gravar uma macro do teclado…" (cria uma ação nova), ou "Gravar teclado…" na
  lista de passos (acrescenta ao que está sendo editado). Um aviso explica o que será gravado; a janela se minimiza e uma
  barra vermelha no alto da tela mostra "Gravando o teclado" e a contagem de passos. **Pause** (ou o botão da barra) para.
- **O que vira passo.** Letras digitadas → um passo "Digitar texto" (Backspace apaga o último caractere gravado; `{` e `}` são
  escritos como `{{` e `}}` para não virarem variável); atalhos e teclas especiais (Ctrl+C, Enter, Tab, setas, F-teclas, Win+R…)
  → "Enviar teclas", com as sequências seguidas juntas num só passo; pausas acima de 0,7 s → "Aguardar" (arredondado a 50 ms,
  no máximo 30 s). Teclas sem nome conhecido (por exemplo as do IME) são ignoradas e contadas no aviso final.
- **Privacidade.** Só teclas de uma pessoa (nada que outro programa injete); nada enquanto o Apparatus está em primeiro plano
  nem enquanto um campo de senha clássico do Windows tem o foco (contado no aviso). **Campos de senha de navegadores não são
  detectáveis**: o aviso inicial diz isso. O resultado é só uma lista de passos, que você revisa, edita e testa antes de salvar.
- Só teclado. O mouse não é gravado (não existe passo de clique).

### Backup, importação e sincronização
- **Arquivo `.apparatus`** (zip com um JSON por parte): configurações, snippets, ações/macros e menu Iniciar. Exportar e importar
  em Configurações → Backup e sincronização. Ao importar, uma janela mostra de onde veio e deixa escolher as partes. Snippets e
  ações de mesmo nome são atualizados (não duplicam); configurações e menu Iniciar são substituídos.
- **Fica de fora**: histórico da área de transferência (sensível), estatísticas de uso, código das extensões e o backup dos ajustes
  do registro. Também não viajam, e nem sobrescrevem o que há no computador de destino: atalho do launcher, iniciar com o
  Windows, pastas indexadas, histórico da área de transferência, tecla Windows e extensões ativas, e a própria pasta de
  sincronização. São decisões de cada computador.
- **Segurança do arquivo.** Nada é extraído para o disco (as entradas são lidas para a memória, com limite de tamanho); versão
  mais nova que a do programa é recusada com orientação; arquivo corrompido ou parte ilegível é relatado sem impedir as outras
  partes. Como ações executam programas, o diálogo avisa para importar só backups de confiança.
- **Pasta de sincronização.** Escolha uma pasta que o OneDrive/Dropbox já sincronize. O Apparatus grava `Apparatus.apparatus`
  lá 10 s depois de cada alteração (e ao iniciar). Não há importação automática: no outro computador, um aviso informa que
  existe uma versão mais nova (e de onde) e o botão "Importar da pasta…" a traz. **Regra anti-perda:** um computador nunca
  sobrescreve um arquivo mais novo de outro computador que ainda não viu; fica "em espera" com aviso até você importar.
  O que foi visto fica em `sync-state.json` (local, não sincroniza).

## Arquitetura

```
Core    Actions/MacroRecorder.cs     keys -> steps (merging, waits, escaping), no Windows dependency
        Backup/BackupService.cs      BackupBundle (zip), BackupService (export/import by part, machine-specific rules)
        Backup/SyncPolicy.cs         when a computer may overwrite the sync file
        Start/StartLayout.cs         + ExportJson/ImportJson (cleaning)
Native  MacroRecording.cs            MacroRecordingSession: shared hook subscriber -> recorder (Pause stops)
App     RecordingBanner.cs           the red bar; ActionsWindow (recording flow)
        SyncService.cs               debounced writes, startup check, import from folder
        SettingsWindow.Backup.cs     the card and the import dialog
```

## Verificação

- Unitários: `MacroRecorderTests` (mesclagem, pausas, Backspace, escape de chaves, teclas sem nome, validade dos passos),
  `BackupTests` (zip: ida e volta, partes ausentes, não-zip, sem manifesto, versão nova, entradas desconhecidas; serviço: viagem
  completa entre dois "computadores", o que não viaja nem sobrescreve, partes escolhidas, mesclagem por nome, parte corrompida,
  limpeza do menu Iniciar importado), `SyncPolicyTests`.
- `tests/Apparatus.E2E.Start --sync <Apparatus.exe> <pasta>`: abre o aplicativo real, confere que o backup aparece sozinho na pasta
  (válido, com as configurações, sem o caminho da pasta) e que um arquivo mais novo de "OUTRO-PC" não é sobrescrito nem importado.
- `tests/Apparatus.E2E.Start --record`: grava teclas reais (letras, pausa, Ctrl+A, Enter, Pause) digitadas num formulário próprio
  e confere os passos gerados e a parada pela tecla Pause.

## Limitações conhecidas

- **Não há barra de tarefas própria (A2).** Substituir a barra do Windows (AppBar + área de notificação) é a parte frágil do
  módulo: a bandeja do sistema não tem API pública para ser hospedada, a barra original precisa ser escondida e o resultado
  quebra em atualizações do Windows. O plano a marcava como experimental e opcional; ficou de fora. O menu Iniciar próprio e
  os ajustes por registro (Fase 5) cobrem o uso diário sem esse risco.
- A gravação não grava mouse, e não grava em programas em modo administrador (mesma limitação do gancho de teclado).
- A sincronização é por arquivo único: duas alterações simultâneas em computadores diferentes não se mesclam; vale a última
  importada, com a proteção contra sobrescrita descrita acima. Não há criptografia do arquivo (ele pode conter ações e textos
  de snippets): deixe-o numa pasta que só você acessa.
- **Não verificado com cliques reais:** os diálogos de gravar/importar, os seletores de arquivo e pasta e os botões da pasta de
  sincronização. A lógica e o fluxo de arquivos estão cobertos pelos testes acima; a interface foi vista em captura.

# Fase 4 — Extensões, quicklinks, área de transferência e favoritos

Estado: **implementada e verificada** com 328 testes unitários (alguns sobem processos PowerShell de verdade), um
teste de ponta a ponta da área de transferência (9 de 9) e capturas do launcher real com um perfil de demonstração.
Os testes de ponta a ponta com teclado das Fases 2 e 3 foram repetidos no fim: 12 de 12 e 12 de 12.

## O que o app faz

### Extensões (protocolo JSON por stdio)
- Programas externos, em qualquer linguagem, que respondem a buscas do launcher. Guia completo em
  [EXTENSOES.md](EXTENSOES.md).
- Um gatilho (`hello mundo`) envia a busca à extensão, e o resultado aparece no topo; ou, se o usuário permitir,
  a extensão recebe todas as buscas.
- A extensão de exemplo **em PowerShell** (`hello-powershell`) é instalada na primeira execução, **desativada**. Ela
  mostra saudação (copiar), hora (digitar no app em uso), pasta temporária (abrir; `Ctrl+Enter` copia o caminho) e uma
  contagem de arquivos (volta para o script, que avisa na bandeja).
- Configurações → Extensões: ativar/desativar (com aviso de que é um programa executado com as suas permissões),
  "responder a todas as buscas", abrir a pasta, recarregar, restaurar o exemplo, e erros de manifesto.

### Histórico da área de transferência
- **Desligado por padrão**, porque guarda tudo o que se copia. Liga em Configurações, no menu da bandeja ou pelo
  resultado "Ativar o histórico…" que aparece ao digitar `clip`.
- Só texto (até 64 KB), sem duplicar (copiar de novo sobe o item e conta), com limite de itens (500) e de dias (30), e
  itens **fixados** que nunca são apagados.
- **Não registra** cópias marcadas como privadas pela origem (as marcas `ExcludeClipboardContentFromMonitorProcessing`
  e `CanIncludeInClipboardHistory` que gerenciadores de senha usam), cópias de programas da lista de exclusão
  (KeePass, 1Password, Bitwarden…), imagens, nem o que o próprio Apparatus coloca na área de transferência.
- No launcher: `clip` lista, `clip texto` filtra; `Enter` cola no programa em uso, `Ctrl+Enter` só copia, `Ctrl+D` fixa,
  `Shift+Del` esquece.

### Favoritos
- `Ctrl+D` no launcher fixa ou solta o item selecionado (aplicativo, arquivo, pasta, configuração, ação, snippet,
  extensão, comando, quicklink). Favoritos aparecem **primeiro** quando o campo está vazio, com uma estrela, e sobem no
  ranking quando você digita. Limpar o histórico não apaga favoritos.

### Quicklinks
- A lista de buscas na web agora é uma lista de **quicklinks e buscas**, editável em Configurações (adicionar, editar,
  remover, restaurar padrões). Com `{query}` no destino, o atalho é uma busca (`yt lofi`); sem `{query}`, é um atalho
  que abre um site, pasta, arquivo ou programa, achado pelo nome ou pelo atalho no launcher.

### Passo de ação "Abrir o launcher"
- Qualquer atalho de teclado pode abrir o launcher já com um texto. Por exemplo, uma ação com esse passo e o texto
  `clip ` vira "o atalho do histórico da área de transferência".

## Arquitetura

```
Core    Extensions/ (manifesto, protocolo, ExtensionProcess, ExtensionHost, ExtensionsProvider)
        Clipboard/ (ClipboardHistoryStore, ClipboardProvider)   Search/Providers/Quicklinks   UsageStore (favoritos)
Native  MessageHost (aviso de cópia), ClipboardAccess.ReadForHistory
App     ClipboardService, ExtensionRunner, LauncherStepExecutor, cartões em SettingsWindow
```

- **Isolamento de falhas.** `ExtensionProcess` contém tudo o que pode dar errado com um processo de terceiros: resposta
  atrasada (timeout), queda (reinício, no máximo 3 por minuto), saída que não é JSON (ignorada), pedido cancelado (a
  extensão continua viva). Para o launcher, uma extensão defeituosa só deixa de mostrar resultados.
- **Dados.** O histórico fica em `%LOCALAPPDATA%\Apparatus\clipboard.db` (fora da pasta que sincroniza), os favoritos
  em `apparatus.db`, as extensões e quicklinks em `%APPDATA%\Apparatus`.

## Verificação

- Unitários: manifesto, protocolo, extensões com processos PowerShell reais (consulta, reuso do processo, acentos,
  timeout, cancelamento, ruído na saída, queda e limite de reinícios, programa inexistente, `oneshot`, `Stop`),
  provedor (palavra-chave, global, falhas engolidas), favoritos (persistência, ordem, ranking, lista vazia),
  histórico (deduplicação, retenção, limite, fixados, persistência) e quicklinks.
- Ponta a ponta da área de transferência (`tests/Apparatus.E2E.Clipboard`): copia texto de verdade com o histórico
  desligado, ligado e com o programa excluído; confere que cópias privadas, imagens e texto em branco não entram.
- Capturas do launcher real: favoritos com estrela, `clip` com idade/origem/tamanho, a extensão PowerShell respondendo
  a `hello mundo` e o quicklink `docs`.

### Defeitos encontrados no caminho
- **Ordem do histórico**: o horário guardava segundos, então duas cópias no mesmo segundo empatavam e a repetida não
  subia ao topo. Agora são milissegundos.
- **Acentos na extensão de exemplo**: sem BOM, o Windows PowerShell 5.1 lê o `.ps1` como ANSI e estraga as strings.
  O exemplo foi salvo com BOM e o guia avisa.

## Limitações conhecidas

- **O histórico fica em texto puro** no disco (`clipboard.db`), sem criptografia. Por isso é opcional e só texto.
  Proteger o arquivo com DPAPI é um passo natural, mas não foi feito.
- **Não exercitado com cliques/teclas reais:** os cartões das Configurações (ativar extensão com o aviso, editar
  quicklink), `Ctrl+D` e `Shift+Del` no launcher, o `Enter` de resultados de extensão/área de transferência (abrir,
  copiar, inserir, colar) e o menu da bandeja. A lógica por trás tem testes; as ações `copy`, `insert` e `open`
  reutilizam caminhos já testados de ponta a ponta nas fases anteriores.
- **Extensões não têm isolamento.** Rodam com as suas permissões; a proteção é só a ativação explícita.
- Favoritos de **arquivo** não são validados: se o arquivo for movido, o item fica e o `Enter` mostra um erro.
- O passo "Abrir o launcher" não vem em nenhum exemplo (os exemplos só são criados na primeira execução); crie a ação
  no editor.
- Imagens e arquivos copiados não entram no histórico.

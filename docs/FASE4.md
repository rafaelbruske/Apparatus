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

## Proteção do histórico (DPAPI)

O texto copiado nunca vai ao disco em claro. Código em `Apparatus.Core/Clipboard/ClipboardCipher.cs` e `ClipboardHistory.cs`.

- **Chave.** 32 bytes aleatórios, guardados em `%LOCALAPPDATA%\Apparatus\clipboard.key` embrulhados pelo DPAPI do Windows
  (escopo do usuário, com um "entropy" próprio do Apparatus). Só a mesma conta, na mesma máquina, consegue desembrulhar.
- **Cifra.** Da chave-mestra saem duas subchaves (HKDF): uma para AES-GCM (cada entrada: versão, nonce de 12 bytes,
  tag de 16, texto cifrado) e outra para HMAC-SHA256, que substitui o hash simples usado para reconhecer uma cópia repetida
  (um hash puro de um texto curto seria adivinhável). Origem do texto, horário e contagem continuam legíveis.
- **Arquivo.** `PRAGMA secure_delete=ON`, para que linhas apagadas sejam sobrescritas. A migração do formato antigo
  (texto puro) recriptografa tudo, faz `wal_checkpoint(TRUNCATE)` e `VACUUM`, e só então termina: nenhum resto em claro
  fica no arquivo nem no WAL.
- **Falha fechada.** Se a chave não puder ser criada ou lida, o histórico fica **indisponível** (a busca `clip` avisa) em
  vez de gravar sem proteção. Se a chave se perder (outro perfil do Windows, arquivo apagado), uma nova é criada e as
  entradas ilegíveis são descartadas, com aviso no log.
- **Desempenho.** Cifrar ou decifrar uma entrada leva microssegundos; o custo do histórico continua sendo o do SQLite.
- **O que não protege.** Um programa que rode como o seu usuário pode pedir ao DPAPI a mesma chave. O objetivo é proteger
  contra outra conta, disco roubado e cópias de segurança, não contra malware já em execução na sua sessão.

## Verificação

- Unitários: manifesto, protocolo, extensões com processos PowerShell reais (consulta, reuso do processo, acentos,
  timeout, cancelamento, ruído na saída, queda e limite de reinícios, programa inexistente, `oneshot`, `Stop`),
  provedor (palavra-chave, global, falhas engolidas), favoritos (persistência, ordem, ranking, lista vazia),
  histórico (deduplicação, retenção, limite, fixados, persistência) e quicklinks.
- Criptografia (`ClipboardEncryptionTests`): ida e volta (acentos, emoji, texto grande), adulteração detectada, chave
  persistida e chave perdida, DPAPI real, varredura dos arquivos (inclusive WAL) atrás do texto em claro, apagamento,
  falha fechada e migração do formato antigo.
- Ponta a ponta da área de transferência (`tests/Apparatus.E2E.Clipboard`): copia texto de verdade com o histórico
  desligado, ligado e com o programa excluído; confere que cópias privadas, imagens e texto em branco não entram, que
  os arquivos do histórico não contêm o texto copiado e que a chave está embrulhada pelo DPAPI.
- Capturas do launcher real: favoritos com estrela, `clip` com idade/origem/tamanho, a extensão PowerShell respondendo
  a `hello mundo` e o quicklink `docs`.

### Defeitos encontrados no caminho
- **Ordem do histórico**: o horário guardava segundos, então duas cópias no mesmo segundo empatavam e a repetida não
  subia ao topo. Agora são milissegundos.
- **Acentos na extensão de exemplo**: sem BOM, o Windows PowerShell 5.1 lê o `.ps1` como ANSI e estraga as strings.
  O exemplo foi salvo com BOM e o guia avisa.

## Limitações conhecidas

- **O histórico é criptografado, mas isso não protege de um programa rodando como você** (veja "Proteção do histórico"
  abaixo). Continua opcional e só texto.
- **Não exercitado com cliques/teclas reais:** os cartões das Configurações (ativar extensão com o aviso, editar
  quicklink), `Ctrl+D` e `Shift+Del` no launcher, o `Enter` de resultados de extensão/área de transferência (abrir,
  copiar, inserir, colar) e o menu da bandeja. A lógica por trás tem testes; as ações `copy`, `insert` e `open`
  reutilizam caminhos já testados de ponta a ponta nas fases anteriores.
- **Extensões não têm isolamento.** Rodam com as suas permissões; a proteção é só a ativação explícita.
- Favoritos de **arquivo** não são validados: se o arquivo for movido, o item fica e o `Enter` mostra um erro.
- O passo "Abrir o launcher" não vem em nenhum exemplo (os exemplos só são criados na primeira execução); crie a ação
  no editor.
- Imagens e arquivos copiados não entram no histórico.

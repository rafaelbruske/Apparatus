# Fase 2 — Snippets (módulo D)

Estado: **implementada e verificada** com um teste de ponta a ponta automático (12 de 12) e 174 testes unitários.

## O que o app faz

- **Expansão ao digitar, em qualquer aplicativo.** Digitar `;data` troca o texto por `20/11/2026` na hora em que o
  último caractere do gatilho é digitado.
- **No launcher:** snippets aparecem junto com apps e arquivos (por nome, gatilho, categoria, tag ou conteúdo).
  `Enter` insere no aplicativo que tinha o foco antes do launcher; `Ctrl+Enter` só copia.
- **Gerenciador** (bandeja → Snippets…, ou "Gerenciar snippets" no launcher): lista com busca e filtro por
  categoria, editor com prévia ao vivo, validação do gatilho enquanto digita, favoritos, duplicar, excluir,
  importar e exportar (JSON). Alterações não salvas pedem confirmação antes de trocar de item.
- **Variáveis** (menu "Inserir variável" no editor):

  | Variável | Resultado |
  |---|---|
  | `{date}`, `{time}`, `{datetime}` | data/hora atuais (`dd/MM/yyyy`, `HH:mm`) |
  | `{date+7d}`, `{date-1m:dd/MM}`, `{time+2h}` | deslocamento (d, w, m, y, h, min) e formato opcionais |
  | `{date:dddd, dd 'de' MMMM}` | formato livre, nomes de dia/mês em português |
  | `{clipboard}` | texto da área de transferência |
  | `{cursor}` | onde o cursor fica depois de inserir |
  | `{field:Nome}`, `{field:Assunto=Reunião}` | campo preenchido numa janelinha antes de inserir |
  | `{choice:Tom=formal\|informal}` | lista de opções |
  | `{user}`, `{computer}`, `{uuid}` | usuário, computador, identificador único |
  | `{{` e `}}` | chaves literais |

- **Como inserir** (por snippet): Automático (digita textos curtos de uma linha, cola os longos ou multilinha, para
  que `Enter` nunca "envie" uma mensagem de chat), Sempre digitar ou Sempre colar. A colagem restaura o texto
  anterior da área de transferência e a esconde do histórico do Windows (Win+V) e da nuvem.
- **Configurações:** liga/desliga a expansão (também na bandeja), lista de aplicativos onde nunca expandir
  (padrão: gerenciadores de senha e área de trabalho remota).

## Regras dos gatilhos

- 2 a 32 caracteres, sem espaços. Maiúsculas e minúsculas contam.
- Gatilhos que começam com símbolo (`;email`) expandem em qualquer posição; os que começam com letra (`btw`) só
  expandem após um espaço, pontuação ou início de texto, e nunca dentro de uma palavra.
- **Um gatilho nunca pode ser o começo de outro** (`;em` e `;email`): como a expansão é imediata, o menor
  dispararia antes de o maior ser digitado. O editor recusa o conflito e diz com qual snippet.

## Arquitetura

```
Core    Snippet, SnippetTemplate (variáveis), SnippetStore (SQLite + FTS5), TriggerBuffer, SnippetsProvider
Native  KeyboardHook, KeyTranslator, TextInjector, ClipboardAccess
App     SnippetService (hook + worker + UI), SnippetsWindow, SnippetFillWindow
```

- Os snippets ficam em `%APPDATA%\Apparatus\snippets.db`, separados do cache e do histórico
  (`%LOCALAPPDATA%\Apparatus\apparatus.db`): são dados do usuário, o resto pode ser apagado. Na primeira execução
  entram quatro exemplos (uma vez só; se você apagá-los, não voltam).
- **Três threads.** A do hook decide em microssegundos e nunca bloqueia; uma worker faz toda a injeção, um trabalho
  por vez; a de UI mostra o diálogo de campos. O hook roda em thread própria para que uma UI lenta não faça o
  Windows desligá-lo.
- **Como uma expansão acontece:** o hook reconhece o gatilho, engole a última tecla, e a worker apaga os
  caracteres já digitados, insere o texto e move o cursor. Enquanto isso, o que o usuário digita é segurado e
  reenviado na ordem depois, para não embaralhar o texto. Uma trava de segurança de 2,5 s garante que o teclado
  nunca fica retido.
- **Tradução de teclas** pelo layout ativo (`ToUnicodeEx`): ABNT2, AltGr, Caps Lock e pacotes Unicode de IME ou
  teclado virtual.
- **O que nunca expande:** campos de senha clássicos do Windows (`ES_PASSWORD`), os processos excluídos e o próprio
  Apparatus. Ctrl ou Alt isolados, Win, Enter, Tab, setas e troca de janela limpam o que foi digitado.

## Verificação

`tests/Apparatus.E2E.Snippets` abre um formulário próprio com uma caixa de texto, inicia o Apparatus com um perfil
isolado (`APPARATUS_PROFILE_DIR`, sem tocar nos dados reais) e digita de verdade pelo teclado. Só envia teclas
enquanto o formulário é a janela em primeiro plano. Casos cobertos:

1. expansão simples, digitada em velocidade humana
2. expansão no meio do texto, com mais digitação imediata logo depois
3. posição do cursor (`({cursor})`)
4. texto multilinha colado, com o cursor no meio
5. área de transferência restaurada depois da colagem
6. teclas digitadas durante uma expansão são reenviadas em ordem
7. variável `{clipboard}`
8. gatilho de palavra não expande dentro de outra palavra
9. gatilho de palavra expande depois de um espaço
10. backspace corrige um erro antes de o gatilho completar
11. snippet com expansão automática desligada fica como digitado
12. diálogo de campos: digitar o valor e `Enter`

Resultado: **12 de 12**. Uma expansão leva de 8 a 46 ms (inclui a colagem). Rodar: ver o cabeçalho de `Program.cs`.

### Bugs que o teste pegou

- **Laço infinito de reinjeção.** O Windows entrega ao hook só os 32 bits baixos do `dwExtraInfo`, e a constante
  `nuint` com bit alto ligado era estendida com sinal: a marca nunca batia, o app tratava a própria injeção como
  digitação do usuário, segurava e reinjetava sem fim (travou o teclado pelos 2,5 s da trava). Corrigido com marca
  abaixo de `0x80000000`, comparação só dos 32 bits baixos, e uma proteção extra contra reinjeção durante o replay.
- **Diálogo sem foco.** Um processo em segundo plano não pode tomar o foco com `SetForegroundWindow`; agora o foco é
  pedido anexando a fila de entrada à da janela em primeiro plano.
- **Ranking:** correspondências de 3+ letras agora precisam começar uma palavra ou ser um trecho contínuo, o que
  tirou ruído como `ola` casando "Metro Last Light".

## Limitações conhecidas

- **Testado só em uma caixa de texto WinForms.** Navegadores, Word, VS Code, Notepad++, terminais e apps de chat
  não foram exercitados; o modo "Automático" foi escolhido justamente porque alguns tratam digitação e colagem de
  formas diferentes.
- **Aplicativos em modo administrador** não recebem a expansão (o Windows bloqueia entrada de um processo comum
  para um elevado). O mesmo vale para o hook de teclado. Resolver exige o helper com UIAccess, previsto para a
  Fase 3.
- **Campos de senha de navegadores** não são detectáveis por esta técnica. Use a lista de aplicativos excluídos
  ou mantenha senhas fora dos snippets com gatilho.
- **Teclas mortas** (acentos compostos, como `´` + `a`) não entram no buffer de digitação; gatilhos devem usar
  caracteres ASCII.
- A expansão depende de o aplicativo aceitar entrada sintética (`SendInput`). Jogos em tela cheia e áreas de
  trabalho remotas podem ignorá-la.
- **Não verificado:** os botões Importar/Exportar da janela (só a lógica, por testes), o menu da bandeja e o
  diálogo de campos pelo launcher (só pelo caminho de expansão automática).

# Fase 3 — Ações e atalhos (módulo B)

Estado: **implementada e verificada** com um teste de ponta a ponta automático (12 de 12), 239 testes unitários e uma
sonda dos controles nativos (volume, brilho e janelas) na máquina de desenvolvimento.

## O que o app faz

- **Criar uma ação sem escrever script.** Bandeja → "Ações e atalhos…" (ou "Gerenciar ações e atalhos" / "Nova ação"
  no launcher). "Nova ação" oferece modelos (abrir aplicativo, abrir site, gerenciar janela, PowerShell, enviar teclas)
  ou uma macro vazia. Para "Abrir Photoshop": escolher *Abrir um aplicativo*, digitar "photo" e escolher o programa na lista (ou usar **Arquivo…** para
  apontar o `.exe`), dar o nome, gravar o atalho e salvar.
- **Atalhos globais** gravados pressionando as teclas. O app avisa na hora se o atalho é inválido, se já é de outra
  ação ou se é o do launcher, e depois de salvo mostra se o Windows aceitou. O **teclado numérico** vale (desde 26/11/2026), com o Num Lock ligado, e
  aparece como `Ctrl+Win+Num4`, `NumAdd`, `NumSubtract`, `NumMultiply`, `NumDivide` e `NumDecimal`; os mesmos nomes servem no passo "Enviar teclas".
- **Atalhos por aplicativo.** "Em qualquer aplicativo", "Somente nestes aplicativos" ou "Em todos, exceto nestes".
  As mesmas teclas podem fazer coisas diferentes em programas diferentes, e fora do escopo a tecla passa intacta
  para o programa. Há um menu "Adicionar app aberto" para não precisar saber o nome do processo.
- **Macros.** Uma ação é uma lista de passos executados em ordem. Cada passo pode ter uma **condição** (só se o app
  ativo for/não for X, ou se o título da janela contiver/não contiver um texto), avaliada no momento do passo. Dá para
  reordenar, duplicar e remover passos. Se um passo falhar, a ação para (ou continua, à escolha).
- **Tipos de passo:**

  | Passo | O que faz |
  |---|---|
  | Abrir aplicativo | programa, argumentos, opcionalmente como administrador |
  | Abrir site / Abrir arquivo ou pasta | no navegador padrão / no programa associado |
  | Executar comando / PowerShell | oculto ou visível, esperando ou não terminar (limite de 5 min) |
  | Enviar teclas | `Ctrl+Shift+T`, ou em sequência: `Ctrl+L Ctrl+V Enter` |
  | Digitar texto / Inserir snippet | com as variáveis dos snippets (`{date}`, `{clipboard}`, `{field:…}`) |
  | Aguardar | milissegundos |
  | Gerenciar janela | maximizar, minimizar, restaurar, fechar, metades, centralizar, sempre no topo, próximo monitor |
  | Volume | aumentar, diminuir, silenciar, definir em % |
  | Brilho | aumentar, diminuir, definir em % |
  | Controle de mídia | tocar/pausar, próxima, anterior, parar |
  | Comando do sistema | bloquear, suspender, desligar o monitor, mostrar a área de trabalho, esvaziar a lixeira, proteção de tela |

- **Escolher o aplicativo** (desde 26/11/2026): na caixa "Aplicativo ou arquivo" do passo "Abrir aplicativo", digitar um nome sugere os aplicativos instalados (a
  mesma lista do launcher, até 8, os mais parecidos primeiro, cada um com o seu ícone, ou um genérico quando não há; quando a lista abre para cima, o Windows põe o melhor junto da caixa). Escolher um escreve o que o
  Windows usa para abri-lo (o caminho do programa ou `shell:AppsFolder\…`) e o passo aparece como "Abrir Relógio" (o nome é guardado no passo, em `name`; num
  passo salvo sem ele, o nome é descoberto só para exibir, sem alterar a ação). A
  caixa continua aceitando um caminho, um nome que o Windows resolve (`calc.exe`) ou qualquer outro alvo, sem sugestões, e o botão **Arquivo…** funciona como
  antes. A lógica está em `AppSuggestions` (Core, com testes).
- **Ícone no passo:** o item "Abrir aplicativo" da lista de passos mostra o ícone do que vai abrir (app instalado, arquivo informado pelo caminho completo ou
  programa escrito só pelo nome, como `notepad.exe`, achado nas pastas do Windows e do `PATH`); sem ícone, fica o desenho padrão do passo. Enquanto se digita, a
  busca espera uma pausa de 0,4 s. Quem decide é `StepIcons` (Core, com testes).
- **No launcher**, as ações aparecem por nome ou categoria; `Enter` executa no aplicativo que tinha o foco antes.
- **Testar** (em 3 s) roda o que está no editor, mesmo sem salvar, depois de uma contagem para você trocar para o
  aplicativo onde a ação deve agir.
- **Importar/exportar** em JSON, com aviso de que ações importadas executam programas. Se um atalho importado entra em
  conflito, a ação é importada sem ele.
- Oito exemplos vêm prontos na primeira execução (janelas, volume, mostrar a área de trabalho e uma macro que abre
  o Bloco de Notas e digita a data), **todos sem atalho**, para não tomar teclas sem você pedir.

## Como funciona

```
Core    ActionDefinition/ActionStep, StepCatalog (campos, resumo, validação), ActionStore (SQLite),
        HotkeyPlan (quais atalhos valem em cada app), ActionRunner (motor), ActionsProvider
Native  MessageHost (vários atalhos + aviso de troca de foco), WindowManager, AudioControl, BrightnessControl,
        KeySender, SystemCommands, ProcessRunner, WindowsStepExecutor
App     ActionService (roteador de atalhos + execução), SnippetStepExecutor, ActionsWindow
```

- **O formulário do editor é gerado do catálogo.** `StepCatalog` descreve cada passo (campos, rótulos, escolhas,
  dicas, validação e como aparece em uma lista). Para criar um novo tipo de passo basta descrevê-lo ali e ensinar um
  executor a rodá-lo; o editor, o resumo e a validação aparecem sozinhos.
- **Atalhos por aplicativo sem hook de teclado.** Um atalho registrado no Windows consome as teclas, então um atalho
  de escopo só é registrado enquanto o app dele está em primeiro plano. O `MessageHost` avisa cada troca de foco
  (`SetWinEventHook`) e o `ActionService` recalcula o conjunto com `HotkeyPlan`: para as mesmas teclas vence o mais
  específico (só-neste-app > exceto-estes > global). Dois atalhos só conflitam quando têm as mesmas teclas, a mesma
  especificidade e podem estar ativos no mesmo app.
- **Execução.** O `ActionRunner` roda os passos em ordem fora da UI, pula os de condição falsa, trata *Aguardar*,
  segue a política de erro e impede que a mesma ação rode duas vezes ao mesmo tempo. Falhas aparecem como aviso na
  bandeja e no log.
- **Dados.** `actions.db` fica em `%APPDATA%\Apparatus`, junto de `snippets.db` e `settings.json`.

## Verificação

`tests/Apparatus.E2E.Actions` abre um formulário e o Bloco de Notas, inicia o Apparatus com um perfil isolado e
pressiona os atalhos de verdade (só com um deles em primeiro plano). Resultado: **12 de 12**.

1. um atalho global executa a ação
2. no app-alvo roda só a ação desse app, mesmo com outra ação nas mesmas teclas para o Bloco de Notas
3. o mais específico vence uma ação global com as mesmas teclas
4. um atalho registrado só para outro app passa intacto até o programa
5. gerenciamento de janela por atalho (metade esquerda, ao pixel)
6. macro: ordem, condição falsa pulada, condição de título, espera e digitação do texto
7. enviar teclas (`Ctrl+A Delete`)
8. um passo que falha interrompe a macro
9. no Bloco de Notas roda a ação do Bloco de Notas, não a do alvo
10. fora do escopo, a ação global volta a valer
11. o atalho só-do-Bloco-de-Notas funciona nele
12. ao sair do Bloco de Notas, o atalho é liberado de novo

A sonda dos controles nativos (fora do repositório) leu e regravou o **volume** (60%) e o **brilho por DDC/CI** (80%) deste
PC, e conferiu ao pixel as posições de janela (metades, centro, maximizar, restaurar, minimizar, sempre no topo). Os
testes dos **snippets** foram repetidos depois da refatoração da inserção de texto: 12 de 12.

### Defeitos encontrados no caminho
- A categoria de itens existentes aparecia **vazia** nos editores de snippets e ações: atribuir `Text` a um `ComboBox`
  editável é ignorado quando o valor está na lista. Corrigido selecionando o item.
- Atalhos que o próprio app registra para ações aparecem como "em uso por outro aplicativo" quando se tenta usá-los
  como atalho do launcher; agora a mensagem diz de qual ação são.

## Limitações conhecidas

- **Não verificado em uso real:** os botões do editor (gravar atalho, menus de passos, contagem do "Testar", importar
  e exportar), a execução de uma ação pelo launcher e o aviso de falha na bandeja. A lógica por trás deles tem testes
  automáticos, mas os cliques não foram exercitados.
- **Brilho:** só foi regravado o valor atual; a mudança efetiva de brilho não foi forçada na sua tela. O controle age no
  monitor da janela ativa, por DDC/CI (com WMI para telas de notebook); monitores sem DDC/CI ligado dão erro explicado.
- **Volume:** ajusta o dispositivo de saída padrão; não há indicador na tela (o Windows só mostra o seu quando as
  teclas físicas de volume são usadas).
- **Teclas reservadas do Windows** (`Win+L`, `Ctrl+Alt+Del`...) não podem ser registradas; o editor mostra o erro.
  Atalhos só de modificadores (tocar Ctrl, por exemplo) e distinção entre tecla esquerda e direita exigiriam um hook
  de teclado, e ficaram de fora.
- **Escopo por título de janela** vale só para condições de passo, não para o atalho (que é por processo).
- **Janelas em modo administrador** não recebem teclas nem mudanças de janela de um Apparatus comum (UIPI). O helper
  com UIAccess previsto para esta fase **não foi feito**: exige assinar o binário e instalá-lo em pasta protegida, o
  que cabe melhor na fase de empacotamento.
- **PowerShell e comandos** rodam com as permissões do Apparatus; ações de terceiros devem ser lidas antes de importar.

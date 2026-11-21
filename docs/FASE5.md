# Fase 5 — Menu Iniciar próprio e ajustes da barra de tarefas

Módulo A do plano: o que o StartAllBack faz, sem substituir o shell do Windows. O Apparatus oferece **o seu próprio menu
Iniciar** (aberto ao tocar na tecla Windows) e **ajustes da barra de tarefas/Iniciar** feitos pelo registro do usuário,
cada um com desfazer exato.

## O que foi entregue

### Menu Iniciar do Apparatus
- **Abre com o toque isolado na tecla Windows** (opcional, desligado por padrão: toma uma tecla do sistema). Win+E, Win+D,
  Win+Shift+S e qualquer outra combinação continuam como sempre. `Ctrl+Esc` segue abrindo o menu original. Desligar a opção
  devolve a tecla ao Windows na hora. Também abre pela bandeja ("Menu Iniciar do Apparatus").
- **Fixados em seções nomeadas**, com tiles de ícone real. Arrastar reordena dentro da seção (e entre seções); o botão
  direito move para outra seção, desafixa, abre, mostra na pasta e adiciona à barra rápida. Cada seção tem renomear,
  subir/descer e excluir (os itens vão para a seção vizinha; a última seção não se exclui).
- **Fixa qualquer coisa que o launcher abre**: aplicativos, arquivos, pastas, configurações do Windows, URLs, ações e
  comandos. Resultados passageiros (cálculo, snippet, área de transferência, busca na web, extensão) não se fixam.
  No launcher: `Ctrl+P` fixa/desafixa o item selecionado.
- **Todos os aplicativos**: lista alfabética (letra à esquerda no primeiro de cada letra, sem acento), com o menu de contexto.
- **Mais usados** ("Recomendados"): os itens de maior uso que ainda não estão fixados (opcional).
- **Pesquisa embutida**: digitar já pesquisa em tudo o que o launcher conhece; `Enter` abre, `Ctrl+Enter` a alternativa,
  `Ctrl+P` fixa.
- **Barra de baixo**: atalhos rápidos (Configurações, Documentos, Downloads, Imagens na primeira vez; até 8, editáveis pelo
  botão direito/menu de contexto dos itens) e o menu de energia (bloquear, sair, suspender, hibernar, reiniciar, desligar).
- **Aparência** (Configurações): tema (sistema/claro/escuro), fundo (acrílico/Mica/sólido), itens por linha (4–8),
  posição (junto ao botão Iniciar, centro, esquerda) e mostrar ou não os mais usados.
- **Posição**: encostado na barra de tarefas, lendo o lado onde ela está (`SHAppBarMessage`) e o alinhamento dos ícones
  (`TaskbarAl`); usa a tela onde está o cursor, respeita o DPI dela e encolhe se a área útil for pequena.
- **Primeira execução**: fixa os aplicativos mais usados (se houver histórico) completados com os conhecidos instalados
  (Edge, Explorador de Arquivos, Configurações, Calculadora...), em português ou inglês. Esvaziar o menu não traz isso de volta.

### Ajustes da barra de tarefas e do Iniciar do Windows
Nove ajustes em `HKCU`, sem administrador: alinhamento dos ícones, tamanho da barra, pesquisa na barra, botão de Visão de
Tarefas, Widgets, agrupamento dos botões (inclusive o valor de múltiplos monitores), segundos no relógio, layout do
Iniciar do Windows e arquivos recentes no Iniciar do Windows.

- **Desfazer exato.** Na primeira alteração de cada ajuste guarda-se o valor original em `windows-tweaks-backup.json`
  (inclusive "o valor não existia", que no desfazer apaga o valor). Alterar de novo não sobrescreve o original. O backup
  sobrevive a reinícios; há "Desfazer" por ajuste e "Desfazer todos".
- **Verificação ao gravar.** O valor é lido de volta; se o Windows recusar ou ignorar (alguns valores são protegidos em
  versões recentes, como Widgets), o cartão avisa em vez de fingir que funcionou.
- **Reiniciar o Explorer** (com confirmação): encerra o `explorer.exe`, espera o Windows relançá-lo e, se não voltar em
  4 s, inicia-o. Só os ajustes marcados como "precisam de reinício" dependem disso (o alinhamento vale na hora).

## Arquitetura

```
Core    Start/StartLayout.cs      pinned layout store (sections, items, quick bar), JSON, atomic write
        Start/StartSeed.cs        first-run contents
        Start/StartPlacement.cs   where the window opens (pure geometry)
        Start/WindowsTweaks.cs    ITweakRegistry, TweakCatalog, TweakService (backup/verify/restore)
        Input/WinTapDetector.cs   "Win pressed and released alone" state machine
Native  KeyboardHub.cs            one shared low-level hook with subscribers (snippets + Win tap)
        StartSupport.cs           RegistryTweakStore, TaskbarInfo, ExplorerControl, PowerActions, WinKeyInterceptor
App     StartMenuWindow(.xaml)    the menu; StartMenuService owns it and the interceptor
        SettingsWindow.Start.cs   the two settings cards
```

### Como a tecla Windows é tomada sem quebrar os atalhos
O Windows abre o Iniciar **ao soltar** a tecla Windows, se nada foi pressionado enquanto ela estava segurada. O
`WinTapDetector` enxerga cada tecla pelo gancho de teclado global:

1. Win pressionada → começa a observar (repetições automáticas são ignoradas).
2. Qualquer outra tecla pressionada enquanto Win está segurada → é uma combinação, nada acontece.
3. Win solta sem nada no meio → é um toque. Antes de deixar a soltura chegar ao Windows, o gancho injeta uma tecla
   inexistente (VK `0xE8`) marcada como do Apparatus; o Windows passa a ver "Win + outra tecla" e **não abre o Iniciar**.
   A soltura da tecla Windows continua passando, para o sistema não achar que ela ficou presa.

O gancho passou a ser um só, compartilhado (`KeyboardHub`): nasce com o primeiro interessado (expansão de snippets ou
toque na tecla Windows) e some com o último. Quem não está usando não paga nada.

## Verificação

- Unitários (`StartTests`, `WinTapTests`, `StartSeedAndPlacementTests`): fixar/desafixar/mover/reordenar, seções
  (renomear, remover com herança, trocar de lugar, limites), barra rápida, semeadura única, arquivo corrompido,
  persistência, notificações; ajustes (valor original, desfazer exato, espelho de múltiplos monitores, recusa e
  gravação ignorada, backup que sobrevive a reinício); detector da tecla Windows (toque, repetição, combinações, mouse);
  posição (cada lado da barra, segundo monitor, tela pequena).
- `tests/Apparatus.E2E.Start`: registro real em uma chave de teste (`HKCU\Software\ApparatusE2eSandbox`, removida ao
  final, e conferência de que as chaves reais não mudam), posição da barra de tarefas, vida do gancho compartilhado. Com
  `--inject-win <Apparatus.exe> <pasta temporária>` também **toca a tecla Windows de verdade** e confere que o menu do
  Apparatus abre e o do Windows não.
- Capturas do menu real (fixados, todos os aplicativos, pesquisa) e dos cartões das Configurações.

## Limitações conhecidas

- **Clique no botão Iniciar da barra** continua abrindo o menu do Windows: só a tecla Windows é interceptada. Interceptar
  o clique exige localizar o botão por UI Automation e é frágil entre versões do Windows 11; fica para depois.
- **Win+clique do mouse**: o gancho é só de teclado, então um clique enquanto Win está segurada não cancela o toque.
- **Programas em modo administrador**: o gancho de baixo nível não recebe teclas dessas janelas enquanto elas têm o foco
  (mesma limitação dos snippets); resolve-se com o auxiliar UIAccess, previsto para depois.
- **Tela cheia exclusiva e jogos** podem não deixar o menu aparecer por cima.
- **Tamanho da barra e Widgets**: versões recentes do Windows 11 podem ignorar ou reverter esses valores; o cartão
  mostra o aviso quando isso acontece.
- **Não verificado com cliques reais:** arrastar tiles (reordenar e mover entre seções), o menu de contexto, os diálogos de
  renomear/nova seção e o menu de energia. A lógica por trás está nos testes unitários; a interface foi vista em capturas.
- O menu **não** substitui o botão Iniciar nem a barra de tarefas (isso é a Fase 6, opcional).

# Fase 7 — Pronto para uso diário

Objetivo: o Apparatus poder substituir o menu Iniciar do Windows (e o StartAllBack) no dia a dia, sem o risco de ficar sem
nenhum menu. Entregas, na ordem em que foram feitas.

## 1. Clique no botão Iniciar

- A posição do botão vem do **UI Automation**: a barra de tarefas do Windows 11 expõe o botão com o id `StartButton`
  (confirmado no Windows 26300: `0,1392–55,1440`; a busca leva ~5 ms). `StartButtonLocator` repete a busca a cada 1,5 s em uma
  thread própria e guarda o retângulo; o gancho do mouse só compara o ponteiro com ele, e nunca chama o UI Automation.
- `MouseHook` (gancho global do botão esquerdo, em thread própria; o movimento do mouse nem chega ao tratador) e
  `StartClickInterceptor`: pressionar sobre o botão engole o clique e soltar sobre ele abre ou fecha o menu do Apparatus.
  Arrastar para fora cancela. Um retângulo maior que 400 px não é aceito como botão.
- Só o botão esquerdo, e só com o mouse; toque e caneta ficam com o Windows. Desligável em Configurações.

## 2. Plano de emergência: voltar ao menu do Windows sozinho

O pedido: como o StartAllBack faz, desligar a substituição se uma atualização do Windows a quebrar. Como o Apparatus não injeta
nada, não há uma "versão incompatível" a detectar; o que pode acontecer são falhas visíveis, e cada uma tem sua resposta
(`StartFailsafe`, `UiHeartbeat`, `OfficialStart`):

| Situação | O que acontece |
|---|---|
| Apparatus fechado ou travado por completo | O gancho de teclado some junto: a tecla Windows é do Windows. |
| Só a interface travou | Um "batimento" da interface a cada 0,5 s; sem batimento por 2 s, os ganchos deixam de interceptar. |
| O pedido se perdeu (o menu não aparece em 1,5 s) | O Apparatus abre o menu do Windows (`Ctrl+Esc`) e conta como problema. |
| As duas versões do menu abrem juntas (a técnica que esconde a tecla Windows deixou de funcionar) | A partir do instante do toque, e por 0,9 s (a cada 30 ms), o Apparatus confere se o menu do Windows apareceu. Conta como problema. A conferência começa no toque porque o menu do Windows se fecha sozinho assim que a janela do Apparatus toma o foco: olhar depois nunca o veria. |
| Jogo em tela cheia exclusiva ou apresentação | Não intercepta: o Windows decide. |

Regra dos "problemas": **a primeira ocorrência desliga** enquanto a versão atual do Windows (build e revisão) ainda não foi
vista funcionando duas vezes seguidas (ou seja, logo após uma atualização do Windows ou na primeira execução); em uma versão já
verificada, **duas seguidas** desligam. Um uso limpo zera a contagem. Ao desligar: a opção "tecla Windows abre o menu do
Apparatus" é desmarcada, os ganchos param, um aviso diz o motivo, e Configurações mostra "Desligado automaticamente" com o
motivo e o botão "Tentar de novo" (que volta a observar com rigor). O estado fica em `%LOCALAPPDATA%\Apparatus\start-failsafe.json`.

**Como o menu do Windows é reconhecido (descoberto por experimento no Windows 26300):** o Iniciar não é uma janela de
`StartMenuExperienceHost`. É uma `Windows.UI.Core.CoreWindow` do processo **`SearchHost`**, com o título "Pesquisar" (o Iniciar e a
pesquisa estão fundidos), que o `EnumWindows` **não lista**, mas que vira a **janela em primeiro plano** enquanto o menu está
aberto. O reconhecimento usa isso (primeiro plano pertence a um dos processos do menu, visível, não encoberta, com mais de 200 px)
e, como reserva para outras versões, também a lista de janelas. Uma primeira versão olhava só a lista e nunca via o menu: o
teste com o Windows de verdade acusou, e há agora um **teste de controle** (abrir o menu do Windows de propósito e exigir que
ele seja visto) para que "o menu do Windows está fechado" nunca mais seja uma verificação vazia.

Limite honesto: o plano de emergência só enxerga o que o menu faz; se uma atualização quebrasse algo que não se vê (por exemplo
só a posição da janela), nada o detectaria. E o menu do Windows e o do Apparatus nunca ficam os dois indisponíveis: desligar o
Apparatus basta para o do Windows voltar.

## 3. Instalador e atualização (Velopack)

- `build\release.ps1 -Version x.y.z` publica a versão autocontida e gera, em `artifacts\releases`: `ApparatusApp-win-Setup.exe`
  (~80 MB), o pacote completo, o pacote **delta** (0,2 MB entre 0.7.0 e 0.7.1) e a versão portátil.
- O instalador é por usuário (sem administrador), em `%LOCALAPPDATA%\ApparatusApp`. O id do pacote difere de propósito da pasta
  de dados (`%LOCALAPPDATA%\Apparatus`): desinstalar apaga a pasta de instalação inteira e não pode levar os dados junto.
  Ao desinstalar, a entrada de "iniciar com o Windows" é removida.
- **Tamanho**: 237 MB → 177 MB (instalado) trocando o pacote guarda-chuva do Windows App SDK pelos componentes usados
  (as partes de IA, ML, Search e Widgets traziam ONNX Runtime e DirectML sem uso). O instalador comprime para ~80 MB.
- **Atualizações**: Configurações → Atualizações, ou `--update-now` na linha de comando. A fonte é uma pasta ou um endereço web
  com os arquivos da release; nada é baixado sem fonte, e nada é instalado sem você mandar.
- **Verificado**: uma cópia portátil da 0.7.0 atualizou-se sozinha para a 0.7.1 a partir de uma pasta (achou, baixou o delta,
  reiniciou e passou a rodar a 0.7.1). E o instalador de verdade (0.7.2), neste computador: instalou em 2 s em
  `%LOCALAPPDATA%\ApparatusApp`, criou os atalhos (Área de Trabalho e Menu Iniciar) e a entrada em "Aplicativos instalados", e o
  programa instalado rodou a partir dali. A desinstalação silenciosa removeu a pasta, os atalhos e a entrada, **apagou o item de
  "iniciar com o Windows"** e deixou os dados do usuário intactos (mesma contagem de arquivos em `%LOCALAPPDATA%\Apparatus` e
  `%APPDATA%\Apparatus`).
- **Defeito do instalador (Velopack 1.2.161):** `Setup.exe --silent -- --background` (argumentos para o programa depois do `--`)
  dá erro interno e o instalador fica pendurado sem instalar. Sem os argumentos, funciona. O programa instalado também não é
  iniciado depois de uma instalação silenciosa.
- **Assinatura de código**: sem certificado não há como assinar; o script aceita `-SignParams` para quando houver um.

## 4. Janelas em modo administrador (UIAccess) — análise, não implementado

O Windows (UIPI) não entrega a um programa comum as teclas digitadas em uma janela elevada: snippets, gravação de macros e a
tecla Windows não a alcançam. (A tecla Windows cai para o menu do Windows, que é o comportamento seguro.) Opções:

1. **UIAccess de verdade.** O manifesto `uiAccess="true"` só vale se o executável for **assinado com um certificado que o
   computador confie** (Autoridades de Certificação Raiz Confiáveis do computador, ou seja, administrador) **e** estiver em
   uma pasta protegida (`Program Files`, `Windows`). O instalador por usuário usa `%LOCALAPPDATA%`, fora do permitido; seria
   preciso um segundo instalador (MSI, com administrador) e um certificado de assinatura. Sem certificado, não há como testar.
2. **Executar o Apparatus como administrador** (tarefa agendada de nível mais alto no logon, sem pedir UAC a cada vez).
   Resolve o alcance, mas tudo o que o Apparatus inicia, inclusive extensões e ações, passaria a rodar elevado, a menos que
   se implemente o lançamento "deselevado" (token do Explorer); arrastar e soltar de/para janelas comuns também deixa de funcionar.
   É uma mudança de postura de segurança que precisa ser sua decisão.
3. **Não fazer nada** (o estado atual): as funções de teclado deixam de funcionar só enquanto uma janela elevada está em
   primeiro plano, e voltam sozinhas ao sair dela.

Nada disso foi implementado: (1) exige um certificado e uma instalação em Program Files, e (2) muda quem executa o quê.

## 5. Acabamento

- `README.md` na raiz.
- Proteção de jogos: com um jogo Direct3D em tela cheia exclusiva ou em modo de apresentação (`SHQueryUserNotificationState`),
  a tecla Windows e o botão Iniciar ficam com o Windows.
- Vários monitores: o menu abre no monitor do cursor, respeita o DPI dele e usa o lado da barra de tarefas; o botão Iniciar das
  barras secundárias (`Shell_SecondaryTrayWnd`) também é localizado, se existir.
- Textos: toda a interface está em português do Brasil, fixa no código; a tradução ficaria para uma etapa própria.

## Verificação

- Unitários: `StartFailsafeTests` (verificação em duas utilizações, rigor após atualização, tolerância, persistência, reinício,
  arquivo corrompido), `UiHeartbeatTests`, `StartInterceptPolicyTests`.
- `tests/Apparatus.E2E.Start` (parte segura): o UI Automation acha o botão Iniciar real, o menu do Windows está fechado em
  repouso, leitura da versão do Windows.
- `tests/Apparatus.E2E.Start --inject-failsafe <Apparatus.exe> <pasta>`: **usa o mouse e o teclado de verdade** (31 de 31 passam
  no Windows 26300). Cenários: controle (o detector vê o menu do Windows quando ele está aberto); clique no botão Iniciar abre o
  menu do Apparatus e não o do Windows, e um segundo clique o fecha; com a interface congelada (`APPARATUS_DEBUG_FREEZE_UI_MS`) a
  tecla Windows abre o menu do Windows e o do Apparatus nem é tentado; com a técnica de esconder a tecla sabotada
  (`APPARATUS_DEBUG_NO_MASK`) o plano de emergência desliga a substituição por conta própria, grava o motivo, e a tecla
  Windows volta a abrir o menu do Windows.
- Reexecutados depois das mudanças: toque na tecla Windows (25/25), pasta de sincronização (26/26), gravação de macros (25/25).
  O teste de snippets não pôde ser repetido nesta rodada (sua janela de teste não conseguiu o primeiro plano e ele aborta sem
  digitar nada); ele havia passado 12/12 depois da troca para o gancho compartilhado, e nada nos snippets mudou desde então.
- Atualização portátil 0.7.0 → 0.7.1, instalação e desinstalação (descritas acima).

### Depois da fase: atalho padrão `Alt+Space` (0.7.4)
O atalho padrão do launcher passou de `Ctrl+Alt+Space` para `Alt+Space` (quem já tinha um atalho salvo o mantém). Ao gravá-lo
nas Configurações, o Windows abria o menu do sistema da janela (Restaurar/Mover/Tamanho), porque `Alt+Space` é o atalho dele:
as janelas de Configurações e de Ações agora descartam esse pedido (`KeyMenuSuppressor`) enquanto um atalho é gravado. Verificado
por teste automático (com controle: sem a proteção o menu abre) e, no app instalado, pelo dono do projeto: `Alt+Space` abre a
busca e regravar o atalho não abre mais o menu.

### Lição sobre os testes com teclado
Os testes antigos fechavam menus com **Esc**. Uma tecla Esc injetada vai para a janela em primeiro plano, e quando essa é a do
Claude Code ela **interrompe a sessão em andamento**: foi o que fez duas execuções parecerem travadas por muito tempo. Agora os
testes fecham o menu tocando a tecla Windows de novo (que alterna o menu) e rodam em segundo plano, com limite de tempo e
arquivo de progresso (`E2E_REPORT_FILE`).

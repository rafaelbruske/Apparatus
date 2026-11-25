# O que ainda falta

Estado: Fases 0 a 7 concluídas (veja `PLANO.md`). Esta lista reúne o que ficou de fora, com o motivo, o que é preciso para fazer e, quando
for o caso, a decisão que cabe ao dono do projeto. Atualizada em 21/11/2026.

## 1. Decisões que dependem de você

### 1.1 Uso do Apparatus em janelas em modo administrador
**Problema.** O Windows (UIPI) não entrega a um programa comum as teclas digitadas em uma janela elevada (Gerenciador de Tarefas,
instaladores, terminais "como administrador"). Enquanto uma delas está em primeiro plano, a expansão de snippets, a gravação de
macros e a tecla Windows do Apparatus não a alcançam. A tecla Windows cai para o menu do Windows (comportamento seguro), e o resto
volta sozinho ao sair da janela elevada. Hoje: **não implementado**.

**Opções:**
| Opção | O que é | Custo / risco |
|---|---|---|
| A. UIAccess | Um executável com `uiAccess="true"` no manifesto, que o Windows deixa ver janelas elevadas sem ser administrador. | Exige assinatura com certificado **confiável para o computador** e instalação em pasta protegida (`Program Files`). O instalador atual é por usuário (`%LOCALAPPDATA%`), então seria preciso um segundo instalador (MSI, com administrador) e um certificado de verdade (veja `ASSINATURA.md`). Não dá para testar sem eles. |
| B. Executar como administrador | Uma tarefa agendada de nível mais alto, no logon, sem UAC a cada vez. | Tudo o que o Apparatus inicia (ações, extensões, aplicativos abertos pelo launcher) passaria a rodar elevado, a menos que se implemente o lançamento "deselevado" (token do Explorer). Arrastar e soltar entre janelas comuns e elevadas deixa de funcionar. Muda a postura de segurança. |
| C. Não fazer nada (atual) | | As funções de teclado só deixam de valer enquanto a janela elevada está em primeiro plano. |

**Recomendação:** ficar na C até haver um certificado de assinatura; então fazer a A. A B só se a limitação incomodar muito, e
com o lançamento deselevado implementado antes.

### 1.2 Assinatura de código
Sem ela o SmartScreen avisa na primeira execução do instalador e a opção A acima é impossível. Veja `ASSINATURA.md` (como funciona,
custos, caminhos). Decisão: assinar ou não, e com qual serviço.

## 2. Verificação que ainda não foi feita com cliques reais
**Já confirmado pelo dono do projeto, no app instalado (0.7.4, 21/11/2026):** `Alt+Space` abre a busca; regravar o atalho em
Configurações → Atalho do launcher não abre mais o menu do sistema da janela (Restaurar/Mover/Tamanho).

O que o código e os testes cobrem está nas fases; o que **nunca foi exercitado com mouse e teclado de verdade**:
- Menu Iniciar do Apparatus: arrastar tiles (reordenar e mover entre seções), menu de contexto dos tiles, diálogos de renomear e
  "nova seção", menu de energia, ordem de rolagem com a roda do mouse.
- Configurações: cartões de ajustes da barra de tarefas (gravam no registro de verdade; só leitura foi vista), reiniciar o Explorer,
  seletores de arquivo e pasta, diálogos de importar backup e gravar macro.
- Ações e atalhos: o fluxo de "Gravar uma macro do teclado" pela interface (o gravador foi testado por teclas reais, a janela não).
- Launcher: `Ctrl+D`, `Shift+Del`, `Enter` em resultados de extensão e da área de transferência, `Ctrl+P` para fixar.
- Tray: o menu de contexto.
- **Assistente de IA** (branch `feature/assistente-ia`): roteiro manual em `docs/FASE8.md`. Já medido com provedor real pelo executor das perguntas de referência (24/11/2026: Groq 21/21 e 21/21, Gemini 19/21 com o prompt novo; ver `docs/ASSISTENTE-IA.md`). **Ainda não verificado:** respostas dentro do launcher com chave real, clique do mouse nos cartões, DPI alto, `run_my_action` real, cartão "Alternativa moderna" na tela e a mudança do tamanho do instalador (0.8.0 já inclui o assistente; comparar com os 80 MB). Acompanhar: o Gemini às vezes responde `propose_action` onde bastava `open_url`.
- **Opção "manter a janela aberta" no modo IA** (`AssistantKeepOpen`): implementada e coberta por teste unitário (padrão, clonagem, backup), mas **não verificada na tela**. O script `tests/Apparatus.UI/ai-keep-open.ps1` (atalho, Tab e um Bloco de Notas que toma o foco) abre o launcher e entra no modo IA, mas em 23/11/2026 o launcher não se escondeu nem sem a opção e sem o modo IA, com o app iniciado de um perfil descartável pelas ferramentas; o resultado não prova nada. Falta confirmar à mão (clicar fora com a opção desligada e ligada, atalho com a janela sem foco, botão ×) ou descobrir por que o teste não vê o launcher esconder.
- **O teste de snippets** (`tests/Apparatus.E2E.Snippets`) não pôde ser repetido na última rodada (a janela de teste não conseguiu o
  primeiro plano; ele aborta sem digitar). Passou 12/12 antes da fase 7.

## 2.1 Incidente de 22/11/2026: instalação que só existia dentro do sandbox do Claude
O 0.7.2/0.7.4 "instalado" nesta sessão foi executado por uma ferramenta do aplicativo Claude, que roda dentro de um pacote MSIX:
tudo o que ele gravou em `%LOCALAPPDATA%`, `%APPDATA%` e `HKCU` (pasta do programa, "iniciar com o Windows", entrada em "Aplicativos
instalados", dados e configurações) ficou numa cópia privada, em `%LOCALAPPDATA%\Packages\Claude_*\LocalCache`, invisível para o
Windows. Por isso, depois do reinício: o atalho "não encontrava o programa", o app não aparecia em Aplicativos instalados e não
abria com o Windows. Nada foi desinstalado pelo Windows.
- **Os dados do usuário (configurações, snippets, ações, fixados) estão nessa cópia privada**; as pastas reais têm só o que um
  Apparatus aberto fora do sandbox criou (padrões). `build\migrar-dados.ps1 -Aplicar`, rodado num PowerShell do próprio usuário,
  guarda um backup das pastas reais e copia a cópia privada sobre elas. Depois, instalar com um duplo clique no `Setup.exe`.
- Corrigido junto, na 0.7.5: o início automático é refeito sozinho se a configuração está ligada mas a entrada sumiu ou aponta
  para outro lugar (`AutoStart.Reconcile`, só em cópia instalada); e um erro ao sair (`SingleInstance.Dispose` chamado duas vezes).
- Regra para o futuro: instaladores e verificações de instalação "de verdade" devem ser feitos pelo usuário, fora do aplicativo Claude.

## 2.2 Ideias para depois (assistente)
- **Markdown nas respostas** (versão mínima: negrito, listas, `código`): desejado, não feito.
- **Histórico de conversas em disco:** sem interesse agora; teria de ser criptografado como o da área de transferência.
- **Modo "JSON no texto"** para modelos locais pequenos (Ollama): ideia futura.
- **Janela de chat própria:** descartada; a opção `AssistantKeepOpen` cobre a necessidade.
- Mais pares clássico → moderno no catálogo (`ncpa.cpl`, disco), ao custo de aumentar o prompt.

## 2.3 Painel dentro do launcher (em andamento)
- **Feito:** configurações, snippets e ações deixaram de ser janelas. `SettingsView`, `SnippetsView` e `ActionsView` (UserControls) são
  hospedadas no próprio launcher, que cresce para 1280×780 com uma barra lateral por grupo (Apparatus, Módulos, Menu Iniciar,
  Assistente de IA, Windows). `Esc` ou "Voltar" volta à pesquisa; o painel não se esconde ao perder o foco; sair de uma página com edição
  não salva (Esc, Voltar, trocar de página ou o atalho do launcher) pergunta antes; gravar uma macro esconde o launcher e o mostra de novo.
  `Ctrl+,` e a engrenagem do rodapé abrem. Os comandos "Novo snippet", "Nova ação" e as propostas do assistente abrem a página certa.
- **Verificado pela automação de interface:** o painel abre nas três áreas, as páginas trocam, "Voltar" restaura o launcher e
  `tests/Apparatus.UI/browse-without-edits.ps1` passa para snippets e ações (controle com diálogo, 0 diálogos ao navegar).
  **Não verificado:** aparência, `Esc` com o teclado real, DPI alto, gravação de atalho e de macro dentro do painel, seletores de arquivo,
  o aviso ao trocar de página com edição pendente.
- **Falta:** redesenhar Snippets e Ações como lista + pré-visualização + `Ctrl+K`; página das Configurações do Windows (catálogo do
  assistente) e links diretos de páginas para o assistente; busca dentro das configurações; categorias Privacidade, Atalhos de teclado
  e Avançado.

### Página "Sobre" (25/11/2026)
- Configurações → **Sobre** mostra a versão, a licença do Apparatus (0BSD) e o link do repositório, e a seção **Licenças de código aberto**
  com os 25 componentes de terceiros do instalador. Cada linha traz "Nome (vVersão)", "Autor · Licença" e "Ver licença"; clicar abre um diálogo com
  o aviso de copyright, o texto completo da licença (rolável, em fonte monoespaçada) e os links do repositório e do NuGet. Se a pasta
  `licenses\` existe ao lado do programa (instalado), há o botão "Abrir a pasta com os avisos completos". Dados em `third-party-licenses.json`
  (embutido no Core, `ThirdPartyLicenses`), mantidos junto com `THIRD-PARTY-NOTICES.md`; um teste confere que as duas listas concordam.
- **Verificado** na tela (build de depuração, perfil isolado): o item no menu lateral, a lista, o diálogo do Velopack e o do Windows App SDK,
  e que o Esc fecha só o diálogo. **Não verificado:** os links abrindo no navegador, o botão da pasta de licenças (só existe no instalado),
  DPI alto e o teclado (Tab/Enter nas linhas).
- **Falta:** os termos de uso para o usuário final exigidos pela licença do Windows App SDK (diálogo de primeiro uso); veja `THIRD-PARTY-NOTICES.md`.

### Captura de tela (25/11/2026)

Implementada (ver `docs/CAPTURA.md`): seleção com anotações e modo de texto, captura automática (Shift+Print Screen), PNG/JPEG com qualidade,
ajuste do Print Screen do Windows com desfazer exato. Verificada à mão em 2 monitores de 100 %; o que não foi verificado está listado no próprio documento.

Pendências do módulo (atualizado em 25/11/2026):

*Assistente de IA*
- **Feito (25/11/2026):** o prompt do assistente cita a captura (e traz um exemplo "como tiro um print?"), a tela `capture` entrou na lista fechada
  `AssistantTools.Screens` (o esquema da ferramenta agora é gerado dessa lista), o `ResultExecutor` abre a página, e há duas perguntas novas em
  `assistant-eval.json` (23 no total). Coberto por testes offline.
- **Pendente: rodar as 23 perguntas com um provedor real** (o teste opt-in `Integration_RealProvider_...`, com `APPARATUS_TEST_GEMINI_KEY` ou
  `APPARATUS_TEST_GROQ_KEY`) para medir se a IA realmente passa a sugerir a captura e se o prompt maior não piorou as outras. Nada disso foi medido com IA de verdade.

*Segurança e ciclo de vida*
- **Desinstalar com a captura ligada deixa a opção do Windows desligada**: a Ferramenta de Captura deixa de abrir no Print Screen e nada a devolve.
  Falta um gancho de desinstalação do Velopack (e, de preferência, devolver a opção também ao sair do app com a captura desligada em outro PC). Hoje o valor
  só volta ao desligar a captura nas configurações.
- Se o Windows religar a opção sozinho (atualização, política), o Apparatus a desliga de novo só ao iniciar.

*Uso e verificação*
- Verificar: impressão, monitores com DPI diferente de 100 %, uso só com teclado, o app **instalado** (os testes foram em build local e publicado, perfil isolado)
  e a tecla Print Screen sozinha com o Lightshot fechado (ele a segura; os testes usaram outra tecla).
- **A dica da barra** (nome e atalho do botão) foi refeita porque a padrão do XAML leva ~1 s; a falha total relatada não foi reproduzida. Confirmar no app instalado.
- O flash da captura automática foi medido em 2 monitores (brilho sobe e volta em ~300 ms, o arquivo salvo não o contém); não foi visto em DPI alto.

*Limites do módulo*
- Anotações não são editáveis depois de inseridas (não dá para mover, redimensionar ou reescrever); só desfazer.
- Fonte e tamanho do modo de texto não são lembrados entre capturas (a cor é).
- A leitura da tela usa GDI: conteúdo protegido ou em tela cheia exclusiva pode sair preto (a gravação, com Windows.Graphics.Capture, resolveria).
- A janela de salvar só abre em Imagens (limite do seletor do Windows); a pasta configurada vale para a captura automática.
- Escolher uma janela clicando nela, durante a seleção, não existe (só região).

*Futuro*
- **Gravação de tela** (monitor inteiro ou janela de um aplicativo): planejada sobre `CaptureTarget`/`IFrameSource` com Windows.Graphics.Capture.
- WebP: adiado (exige libwebp como DLL nativa e aviso de terceiros).

## 2.4 Menu Iniciar no estilo clássico (24/11/2026)
- O menu Iniciar do Apparatus passou ao layout de duas colunas (como o do StartAllBack): à esquerda os apps fixados e os mais usados
  ("Usados recentemente"), "Todos os programas" e a busca embaixo; à direita o usuário e os locais do sistema (Documentos, Imagens,
  Músicas, Downloads, Itens recentes, Este Computador, Painel de Controle, Configurações, Windows Update, Executar…, Configurações do
  Apparatus) e o menu Desligar. Os dados dos fixados (seções) continuam os mesmos.
- **Deixou de existir na tela (código removido, dados mantidos):** blocos em grade, arrastar para reordenar ou mover entre seções, botão
  "Nova seção", renomear/mover/excluir seção pelo menu da seção, barra rápida do rodapé e o ajuste "Itens por linha" (escondido nas
  configurações). Mover para uma seção e criar seção continuam no menu de contexto de um item fixado.
- **Verificado:** o menu abre com os elementos esperados nas duas colunas (automação de interface). **Não verificado:** aparência,
  clique e teclado reais, DPI alto, menu de contexto e o menu Desligar na nova posição.

## 2.5 Tarefas dos aplicativos no menu Iniciar (24/11/2026)
- Cada app com "tarefas" na lista de atalhos do Windows (Nova janela, Nova janela privativa, Nova sessão…) ganha um botão `›` na linha, no menu
  Iniciar, que mostra essas tarefas. O Apparatus lê o arquivo `%APPDATA%\Microsoft\Windows\Recent\CustomDestinations\<hash>.customDestinations-ms`
  do app (nome = CRC-64 do AppUserModelID em maiúsculas, UTF-16) e usa a primeira categoria, que são as tarefas (`JumpLists`, `JumpTaskReader`).
- **Limites:** só as tarefas (não os "recentes/frequentes" do app); só apps que gravaram a lista (um app que nunca a criou não mostra o botão);
  apps clássicos identificados por caminho (sem AppUserModelID) podem não ter arquivo; os itens usam um ícone genérico; sem teclado
  (só clique). **Verificado:** o botão aparece e o menu lista as tarefas do AnyDesk (automação); o hash confere com os arquivos de Chrome, Edge e
  Firefox desta máquina. **Não verificado:** executar uma tarefa, Chrome/Edge/Firefox na tela, aparência.

## 2.6 Menu de contexto clássico (24/11/2026)
- Configurações → Windows → "Menu de contexto do Windows": um interruptor liga o menu clássico do botão direito (cria a chave
  `HKCU\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32` com o valor padrão vazio) ou volta ao do Windows 11 (apaga a
  chave `{86ca1aa0-...}`). Depois de mudar, pergunta se reinicia o Explorer ("Reiniciar" ou "Mais tarde"). Classe `ClassicContextMenu`.
- **Verificado** pela automação de interface num perfil descartável: ligar cria a chave com valor vazio, desligar a apaga, o diálogo aparece; o
  Explorer não foi reiniciado. **Não verificado:** o efeito no menu depois de reiniciar o Explorer.

## 3. Funcionalidades que ficaram de fora
- **Barra de tarefas própria (AppBar, módulo A2).** Deliberadamente não feita: a bandeja do sistema não tem API pública para ser
  hospedada, a barra original precisa ser escondida e quebra a cada atualização do Windows. O plano a marcava como opcional.
- **Gravação de macros com o mouse** (não existe passo de clique) e **Win+clique** (o gancho de teclado não vê o mouse).
- **Toque e caneta** no botão Iniciar ficam com o Windows (só o mouse é interceptado).
- **Localização:** toda a interface está em português do Brasil, fixa no código.
- **Barras de tarefas secundárias** (outros monitores) têm o botão Iniciar localizado, mas nunca foram testadas.

## 4. Instalador e distribuição
- O `Setup.exe` não está assinado (SmartScreen avisa).
- Defeito do Velopack 1.2.161: `Setup.exe --silent -- <argumentos>` dá erro interno. Não use argumentos para o programa. Verificar se
  versões novas corrigem.
- Depois de uma instalação **silenciosa** o programa não é iniciado sozinho (a instalação comum o inicia).
- Para atualizações é preciso **hospedar** a pasta de release (uma pasta de rede/nuvem sincronizada ou um endereço web); o app só
  consulta a fonte escolhida em Configurações → Atualizações.
- Tamanho: 177 MB instalado, 80 MB o instalador. `Microsoft.Windows.SDK.NET.dll` (24 MB) é o maior arquivo e não é reduzível sem
  cortes arriscados do WinUI.

## 5. Segurança e privacidade, limites conhecidos
- Extensões rodam com as suas permissões, sem isolamento; a proteção é a ativação explícita.
- O backup `.apparatus` e a pasta de sincronização **não são criptografados** (podem conter ações e textos de snippets).
- A gravação de macros não detecta campos de senha de navegadores.
- O histórico da área de transferência é protegido contra outras contas e discos roubados, não contra um programa rodando como você.
- Favoritos de arquivo não são validados (se o arquivo for movido, o `Enter` mostra um erro).

## 6. Pequenos defeitos e ideias
- Alguns ícones de atalhos `.url` (por exemplo os do PostgreSQL) não carregam: o caminho tem barras `/` que o Windows não abre.
  Mostram o ícone padrão. Normalizar o caminho resolve.
- Quando a lista de aplicativos termina de carregar com uma pesquisa já digitada no menu Iniciar, os resultados não são atualizados.
- Ao abrir o menu pela primeira vez sem cache, os fixados iniciais dependem de a lista de aplicativos já ter carregado (há uma
  nova tentativa a cada 0,5 s por até 20 s).
- Arrastar de resultados de pesquisa ou da lista "Todos os aplicativos" direto para uma seção do menu (hoje: botão direito ou `Ctrl+P`).
- Ajustes da barra de tarefas que versões recentes do Windows protegem (Widgets, tamanho): o cartão avisa quando o valor não é mantido.
- Os avisos de compilação `PInvoke004` do CsWin32 e `CA1416` (`GetDpiForWindow`) em `Apparatus.Native` são antigos e inofensivos.

## 7. Como retomar
- Compilar e testar: `dotnet build Apparatus.sln`; `dotnet test tests/Apparatus.Core.Tests` (770 testes).
- Verificação segura: `dotnet run --project tests/Apparatus.E2E.Start`.
- Verificação com entrada real (mouse/teclado): `--inject-failsafe`, `--inject-win`, `--sync`, `--record` do mesmo projeto. **Nunca
  mande Esc para a área de trabalho**: ele interrompe a sessão do Claude Code. Rodar em segundo plano, com o PC parado.
- Instalador: `.\build\release.ps1 -Version x.y.z`.

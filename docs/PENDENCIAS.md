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
O que o código e os testes cobrem está nas fases; o que **nunca foi exercitado com mouse e teclado de verdade**:
- Menu Iniciar do Apparatus: arrastar tiles (reordenar e mover entre seções), menu de contexto dos tiles, diálogos de renomear e
  "nova seção", menu de energia, ordem de rolagem com a roda do mouse.
- Configurações: cartões de ajustes da barra de tarefas (gravam no registro de verdade; só leitura foi vista), reiniciar o Explorer,
  seletores de arquivo e pasta, diálogos de importar backup e gravar macro.
- Ações e atalhos: o fluxo de "Gravar uma macro do teclado" pela interface (o gravador foi testado por teclas reais, a janela não).
- Launcher: `Ctrl+D`, `Shift+Del`, `Enter` em resultados de extensão e da área de transferência, `Ctrl+P` para fixar.
- Tray: o menu de contexto.
- **O teste de snippets** (`tests/Apparatus.E2E.Snippets`) não pôde ser repetido na última rodada (a janela de teste não conseguiu o
  primeiro plano; ele aborta sem digitar). Passou 12/12 antes da fase 7.

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
- Compilar e testar: `dotnet build Apparatus.sln`; `dotnet test tests/Apparatus.Core.Tests` (491 testes).
- Verificação segura: `dotnet run --project tests/Apparatus.E2E.Start`.
- Verificação com entrada real (mouse/teclado): `--inject-failsafe`, `--inject-win`, `--sync`, `--record` do mesmo projeto. **Nunca
  mande Esc para a área de trabalho**: ele interrompe a sessão do Claude Code. Rodar em segundo plano, com o PC parado.
- Instalador: `.\build\release.ps1 -Version x.y.z`.

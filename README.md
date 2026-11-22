# Apparatus

Um utilitário para Windows 11 que reúne, em um só programa residente na bandeja:

- **Launcher** (estilo Raycast): `Alt+Space` (configurável) abre uma caixa que pesquisa aplicativos, arquivos, configurações do Windows, histórico da área de transferência, snippets, ações e extensões, e faz contas e conversões.
- **Snippets** (estilo TextExpander): `;email` vira o seu e-mail em qualquer programa, com variáveis (`{date}`, `{clipboard}`, `{cursor}`) e campos preenchíveis.
- **Ações e macros** (estilo AutoHotkey): atalhos globais ou por programa que abrem coisas, enviam teclas, gerenciam janelas, controlam volume e brilho. Macros podem ser **gravadas** pelo teclado.
- **Menu Iniciar próprio** (estilo StartAllBack): fixados em seções, todos os aplicativos, pesquisa, atalhos e energia, aberto pela tecla Windows ou pelo botão Iniciar. A barra de tarefas continua sendo a do Windows.
- **Ajustes da barra de tarefas** do Windows 11 pelo registro do seu usuário, cada um com desfazer exato.

Nada é injetado no Explorer nem em outros programas: o Apparatus usa só APIs públicas do Windows, por isso uma atualização do Windows não o quebra em silêncio. Se a substituição do menu Iniciar parar de funcionar depois de uma atualização, o **menu do Windows volta sozinho** (veja abaixo).

## Instalar

```powershell
dotnet tool install -g vpk --version 1.2.161
.\build\release.ps1 -Version 0.7.0
```

O instalador sai em `artifacts\releases\ApparatusApp-win-Setup.exe` (cerca de 80 MB). Ele instala em `%LOCALAPPDATA%\ApparatusApp` e não precisa de administrador. Suas configurações e dados ficam em `%APPDATA%\Apparatus` e `%LOCALAPPDATA%\Apparatus`, **fora** da pasta de instalação: desinstalar ou atualizar não os apaga.

Sem assinatura de código, o SmartScreen avisa na primeira execução ("Mais informações → Executar assim mesmo"). Para assinar, passe `-SignParams` ao script.

Para atualizar, coloque a pasta `artifacts\releases` (ou um endereço web com os mesmos arquivos) em Configurações → Atualizações. O Apparatus avisa quando há versão nova e você decide quando instalar.

Para rodar a partir do código: `dotnet run --project src/Apparatus.App`.

## Usar o menu Iniciar do Apparatus

1. Clique com o botão direito no ícone da bandeja e marque **"Tecla Windows abre o menu do Apparatus"** (ou em Configurações → Menu Iniciar do Apparatus).
2. Toque na tecla Windows, ou clique no botão Iniciar. `Win+E`, `Win+D` e as demais combinações continuam como sempre. `Ctrl+Esc` abre o menu original quando você quiser.
3. Para fixar algo: pesquise e pressione `Ctrl+P` (também no launcher), ou use o botão direito. Arraste para reordenar.

### O menu do Windows volta sozinho se algo der errado

O Apparatus vigia a si mesmo e devolve o menu ao Windows, sem você fazer nada, quando:

- **o programa trava ou fecha**: sem ele, o gancho de teclado some e a tecla Windows volta ao Windows na hora; se só a interface travar, os atalhos deixam de ser interceptados em 2 segundos;
- **o menu do Apparatus não aparece** em até 1,5 s depois do toque: o Apparatus abre o do Windows (`Ctrl+Esc`);
- **o Windows foi atualizado e o menu dele abre junto com o do Apparatus** (visto de verdade: o Apparatus reconhece o menu do Windows 11 26300, que é uma janela "Pesquisar" do `SearchHost`) (a técnica que esconde a tecla do Windows deixou de funcionar): na primeira ocorrência depois de uma atualização, ou na segunda seguida em um Windows já verificado, a substituição é **desligada** e o motivo aparece em Configurações, com o botão "Tentar de novo";
- **um jogo em tela cheia exclusiva ou uma apresentação** está no primeiro plano: o Windows decide.

### Janelas em modo administrador

O Windows impede que um programa comum veja as teclas digitadas em janelas elevadas. Enquanto uma delas está em primeiro plano, a expansão de snippets, a gravação de macros e a tecla Windows do Apparatus não a alcançam (a tecla Windows cai para o menu do Windows, como acima). Veja `docs/FASE7.md` para as opções.

## Segurança e privacidade

- O histórico da área de transferência é opcional, só de texto, e fica **criptografado** (AES-GCM, chave protegida pelo DPAPI do Windows); ele nunca é gravado em claro.
- Extensões são programas que rodam com as suas permissões e nunca rodam sem você ativá-las.
- Ações importadas podem executar programas: importe só o que for de confiança.
- A gravação de macros ignora campos de senha do Windows, mas **não** detecta os de navegadores.
- O backup `.apparatus` e a pasta de sincronização não incluem o histórico da área de transferência e não são criptografados.

## Mais

`docs/PLANO.md` descreve o plano e as decisões; `docs/FASE0-RESULTADOS.md` a `docs/FASE7.md` o que cada fase entregou, como foi verificado e o que ficou de fora. `docs/EXTENSOES.md` explica como escrever uma extensão. `docs/PENDENCIAS.md` lista o que ainda falta (inclusive o uso em janelas de administrador) e `docs/ASSINATURA.md` explica a assinatura de código.

## Testes

```powershell
dotnet test tests/Apparatus.Core.Tests          # unitários
dotnet run --project tests/Apparatus.E2E.Start  # integração segura (registro de teste, botão Iniciar, gancho)
```

Há também verificações que pressionam teclas e clicam de verdade (snippets, tecla Windows, botão Iniciar, plano de emergência, gravação de macros); cada uma avisa no próprio cabeçalho do código o que faz e que você não deve mexer no teclado e no mouse enquanto ela roda.

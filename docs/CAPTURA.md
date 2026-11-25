# Captura de tela

Módulo de captura de tela no estilo do Lightshot: o usuário escolhe uma área, anota e copia, salva ou imprime. Fica em
**Configurações → Captura de tela** e é opt-in (vem desligado). Adicionado depois da Fase 8; a gravação de tela é o próximo passo
planejado (veja o fim deste arquivo).

## Como funciona para o usuário

1. O atalho (padrão **Print Screen**) congela cada monitor e escurece a imagem.
2. Arrastar escolhe a área. Arrastar pelas bordas e cantos ajusta; arrastar por dentro move.
3. Uma única barra ao lado da área reúne tudo: lápis, linha, seta, retângulo, marca-texto, texto, cor | imprimir, copiar, salvar,
   desfazer, fechar. Cada botão tem uma dica com o nome e o atalho (aparece 250 ms depois de o mouse parar sobre ele).
4. Teclas: `Ctrl+C` copia, `Ctrl+S` salva, `Ctrl+P` imprime, `Ctrl+Z` desfaz, `Esc` fecha.

### Modo de texto

Ao escolher **texto**, a barra troca por: voltar, **desfazer** | negrito, itálico, sublinhado, cor | tamanho, fonte | rótulo
“Ctrl + Enter finaliza a edição”. O botão de voltar retorna à barra de ferramentas.

- **Enter quebra a linha; Ctrl+Enter** grava o texto na imagem e fecha a edição. `Esc` cancela o texto que está sendo digitado.
- **Desfazer** tira o texto que está sendo digitado ou, se não houver, o último desenho ou texto inserido (um clique por item).
- `Ctrl+B`, `Ctrl+I` e `Ctrl+U` também valem na digitação. A caixa de edição não mostra o sublinhado; ele aparece quando o texto é confirmado.
- Fontes: Segoe UI, Arial, Calibri, Verdana, Georgia, Times New Roman, Courier New, Consolas, Comic Sans MS e Impact; tamanhos de 12 a 72.
  As escolhas valem só durante a captura aberta; a cor é lembrada entre capturas (`CaptureColor`).

### Salvamento

- **Notificação depois de salvar** (`CaptureNotifyOnSave`): desligada por padrão. Erros são avisados sempre.
- **Pasta** (`CaptureSaveFolder`; vazia = `Imagens\Screenshots`) e **captura automática** (`CaptureQuickHotkey`, padrão **Shift+Print Screen**).
  A captura automática **não abre a tela de seleção e não pede confirmação**: ao pressionar o atalho, salva na hora uma imagem na pasta,
  com o nome padrão (se o nome já existir, ganha “ (2)”, “ (3)”…). Sem a notificação ligada, é silenciosa.
  Com o atalho normal, a seleção abre e **Salvar** usa a janela de salvar do Windows (que só abre em Imagens; o seletor de arquivos do
  Windows não aceita uma pasta qualquer).
- **O que a captura automática salva** (`CaptureAutoAllMonitors`): por padrão, **apenas o monitor focado**, o da janela que tem o foco
  (se não houver uma, como com a área de trabalho em foco ou uma janela minimizada, vale o monitor sob o mouse). A outra opção junta
  **todos os monitores numa imagem só** (o retângulo que cobre a área virtual inteira).
- **Formato e qualidade** (`CaptureFormat`, `CaptureQuality`): PNG por padrão, ou JPEG; qualidade de 1 a 100, padrão **90**. A qualidade só
  age no JPEG (o PNG é sem perdas e a ignora). Vale para a captura automática e define o tipo oferecido primeiro na janela de salvar; se o
  usuário escolher “.jpg” ali, grava JPEG com a mesma qualidade. Copiar e imprimir não mudam (a imagem vai por PNG).
- **Nome padrão**: `Screenshot AAAA-MM-DD HH-mm-ss.png` (ou `.jpg`).

Fora por decisão: envio para a nuvem, compartilhamento e busca de imagem. WebP foi analisado e adiado: o Windows não tem codificador, então
exigiria o libwebp (BSD-3) como DLL nativa, com aviso de terceiros.

## A Ferramenta de Captura do Windows

No Windows 11, Print Screen abre a Ferramenta de Captura por padrão. Se os dois respondessem à mesma tecla, abririam juntos.
Por isso, **ativar a captura do Apparatus desliga a opção do Windows** “Usar o botão Print Screen para abrir a captura de tela”
(Configurações → Acessibilidade → Teclado), e a página avisa isso antes do usuário ligar.

- Valor: `HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingEnabled` (DWORD, 1 = a Ferramenta de Captura abre).
- É gravado pelo `TweakService` (o mesmo dos ajustes da barra de tarefas), que **guarda o valor original** na primeira alteração e o restaura
  exatamente ao desativar (inclusive “o valor não existia”). O ajuste é interno (`TweakCatalog.Internal`): não aparece na página de ajustes
  do Windows, e “Desfazer tudo” de lá não mexe nele.
- Ao iniciar, com a captura ativa, o Apparatus confere se o Windows não ligou a opção de novo e a desliga outra vez.
- `Win+Shift+S` continua sendo da Ferramenta de Captura: é um atalho reservado do sistema, que não dá para tomar.
- Se o registro do atalho falha com erro 1409 (outro programa tem a tecla), a mensagem cita o programa de captura em execução
  (Lightshot, ShareX, Greenshot…), e o ajuste do Windows é devolvido ao valor anterior: nada fica pela metade.
  Ao desligar a opção do Windows, o sistema solta a tecla um instante depois, por isso o registro tenta de novo algumas vezes.

## Arquitetura

| Parte | Onde | Papel |
|---|---|---|
| Modelo e geometria | `Apparatus.Core/Capture/CaptureModel.cs` | `RectD` (com `Union`), `CaptureTarget` (`RegionTarget`, `MonitorTarget`, `WindowTarget`), `CapturedFrame`, `IFrameSource`, `SelectionGeometry` (acerto de alças, mover/redimensionar, posição da barra), `ArrowGeometry`, `CaptureImageFormats`, `CaptureNaming`, `KnownCaptureTools`. Tudo puro e testado (`CaptureTests`). |
| Leitura da tela | `Apparatus.Native/ScreenCapture.cs` | `GdiFrameSource`: um `BitBlt` do retângulo da área virtual (pixels físicos, DPI por monitor) e `ForegroundWindowCenter` (qual monitor tem o foco). |
| Codificação | `Apparatus.App/CaptureImage.cs` | BGRA → PNG ou JPEG (com qualidade) pelo `BitmapEncoder`. |
| Janela de seleção | `Apparatus.App/CaptureOverlayWindow.cs` | Uma janela sem borda por monitor, com a imagem congelada, seleção, anotações (XAML), as duas barras, as dicas e as ações. |
| Serviço e sessão | `Apparatus.App/CaptureService.cs` | `CaptureService`: atalhos (ids `0xA991` e `0xA992`), ajuste do Windows, ativar/desativar, captura automática. `CaptureSession`: congela todos os monitores e abre as janelas. |
| Configurações | `SettingsView.Capture.cs` + página `Page_capture` | Interruptor, atalhos (aceitam Print Screen sozinha), pasta, formato, qualidade, escopo, “Capturar agora”. |

Configurações em `AppSettings`: `CaptureEnabled`, `CaptureHotkey` (`PrintScreen`), `CaptureColor`, `CaptureFormat` (`png`), `CaptureQuality` (90),
`CaptureNotifyOnSave` (falso), `CaptureSaveFolder`, `CaptureQuickHotkey` (`Shift+PrintScreen`), `CaptureAutoAllMonitors` (falso).
Entram no backup como as demais. Nenhum pacote NuGet novo foi preciso: tudo usa APIs do Windows já disponíveis (GDI, WinRT de imagem, WinUI).

Decisões de implementação:

- **Imagem congelada**: todos os monitores são lidos *antes* de qualquer janela aparecer, então as janelas não saem na foto e nada se mexe
  enquanto o usuário escolhe.
- **Uma janela por monitor** (em vez de uma gigante): cada monitor tem seu DPI, e o XAML trabalha em DIPs por janela. A seleção fica em um
  monitor; começar outra seleção em outro monitor descarta a primeira.
- **Resultado**: `RenderTargetBitmap` do palco (imagem + anotações, sem a barra e as alças) na resolução física do monitor, recortado na
  seleção. A cópia usa `DataPackage.SetBitmap`; imprimir salva um PNG temporário e usa o verbo `print` do Windows.
- **Dicas próprias**: a dica padrão do XAML leva cerca de 1 s para aparecer e parecia que não existia; a barra usa uma dica própria de 250 ms.
- **Foco do mouse**: o `PointerReleased` aplica a última posição antes de encerrar, porque movimentos rápidos podem não gerar eventos intermediários.
- Anotações são elementos XAML recortados pela seleção; desfazer remove o último.

## Preparado para a gravação de tela (futuro)

O que já está no lugar:

- `CaptureTarget` já descreve **região**, **monitor** e **janela** (`WindowTarget` mede a janela pelo quadro visível do DWM, sem as bordas invisíveis).
  A gravação usa os mesmos alvos; o usuário escolhe “tela inteira” (`MonitorTarget`) ou “janela de um aplicativo” (`WindowTarget`).
- `IFrameSource` devolve um quadro (`CapturedFrame`) de um alvo. Para gravar, ganha um irmão que entrega uma **sequência** de quadros
  (`IFrameStream`), sem mexer no resto. O `GdiFrameSource` serve para foto; a gravação deve usar **Windows.Graphics.Capture**
  (`GraphicsCaptureItem` criado a partir de um `HMONITOR` ou `HWND`), que é eficiente e também pega conteúdo que o GDI devolve preto
  (janelas aceleradas por GPU, por exemplo).
- `CaptureService` ganha um terceiro atalho (iniciar/parar) com o mesmo mecanismo de registro e mensagens de conflito; `RecordingBanner`
  (já usado na gravação de teclado) serve de indicador “gravando”.
- Codificação: `Windows.Media.Transcoding`/`MediaStreamSource` com H.264 em MP4, sem dependências novas.

Fora de propósito por ora: seleção de janela por clique no overlay (hoje só região), áudio e envio para a nuvem.

## Testes e verificação

- `CaptureTests` (Core, 44 casos): geometria da seleção (alças, mover, redimensionar, inverter, tamanho mínimo, limites), posição da barra,
  setas, união dos monitores, nome de arquivo e caminho único, formato e qualidade, reconhecimento de programas de captura, Print Screen como
  atalho válido, padrões das configurações e o ajuste do Windows (aplica, restaura exatamente, não é afetado pelo “desfazer tudo”).
- **Verificado à mão** (build local e build publicado, perfil isolado `APPARATUS_PROFILE_DIR`, 2 monitores de 1920×1080): atalho, seleção nos
  dois monitores, mover/redimensionar pela alça, barra e ordem dos botões, lápis, retângulo, seta, marca-texto e texto (várias linhas,
  Ctrl+Enter, desfazer), dicas, cópia (imagem do tamanho exato da seleção, sem o escurecimento), janela de salvar acima da sobreposição
  (com JPEG como tipo inicial quando configurado), `Esc`, ativar/desativar com o valor do Windows em 1 (vira 0 e volta a 1), captura
  automática (monitor focado 1920×1080, todos 3840×1080, PNG e JPEG nas qualidades 90 e 30) e a página de configurações.
- **Não verificado**: impressão, monitores com DPI diferente de 100 %, a tecla Print Screen sozinha com o Lightshot fechado (nos testes ele a
  segurava; foi usado `Ctrl+Alt+F8`), o instalador com o app instalado, uso apenas com teclado nas barras.

### Como testar sem atrapalhar

Use um perfil isolado (`APPARATUS_PROFILE_DIR`) e um atalho de teste (por exemplo `Ctrl+Alt+F8`) no `settings.json` desse perfil. O app instalado do
usuário pode estar aberto: **encerre só o processo de teste, pelo caminho do executável**, nunca pelo nome. Para mexer na opção do Windows
sem perder a configuração do usuário, force o valor para 1, teste e devolva o valor original (o que a máquina tinha antes).

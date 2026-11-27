# Apparatus: guia para assistentes de IA e desenvolvedores

Leia isto antes de mexer no projeto. É o resumo do que o `README.md`, o `CONTRIBUTING.md` e os arquivos de `docs/` dizem, mais as
regras de trabalho e as armadilhas que já custaram tempo. Está em português, como os documentos de `docs/`; **identificadores e
comentários de código ficam em inglês**, e o **texto que o usuário vê na tela fica em português do Brasil**.

## O que é

Utilitário para Windows 11 que roda na bandeja: launcher (estilo Raycast), snippets, ações e macros, menu Iniciar próprio, captura de
tela (estilo Lightshot), assistente de IA opcional (BYOK) e ajustes do Windows. C# / .NET 10, interface em WinUI 3 (Windows App SDK,
sem pacote MSIX), instalador pelo Velopack. Licença 0BSD. Só a branch `master`.

## Estrutura

| Pasta | O que vive nela |
|---|---|
| `src/Apparatus.Core` | Tudo que não precisa de janela: busca, snippets, ações, backup, dados do Iniciar, assistente, geometria da captura. `net10.0` puro, sem UI e sem P/Invoke. **A lógica nova vai aqui.** |
| `src/Apparatus.Native` | Chamadas ao Windows (P/Invoke, shell, hooks, GDI). Fina, sem regra de negócio. |
| `src/Apparatus.App` | O app WinUI 3: janelas, controles, bandeja, ligações. Fina também. |
| `tests/Apparatus.Core.Tests` | Testes unitários (xUnit). Rodam sem teclado, mouse nem tela. |
| `tests/Apparatus.E2E.*`, `tests/Apparatus.UI` | Verificações que usam teclado e mouse de verdade. Cada uma diz no cabeçalho o que faz. **Não rode sem o dono do projeto saber.** |
| `build/release.ps1`, `build/verify-release.ps1` | Publica self-contained e empacota com o Velopack; confere o resultado (feed, versão, licenças). |
| `docs/` | Plano, notas por fase, guias e pendências. |

Como as telas se ligam: configurações são páginas do `SettingsView` (`PageIds`/`PageTitles` em `SettingsView.xaml.cs`, uma `Page_<id>` no XAML e uma
entrada em `Navigation` de `SettingsWindow.xaml.cs`, a janela de configurações); os serviços ficam pendurados em `AppServices` e são criados em `App.xaml.cs`;
atalhos globais passam pelo `MessageHost` (cada recurso tem seus ids: launcher `0xA990`, captura `0xA991`/`0xA992`, ações `0x1000`–`0xBFFF`).

## Comandos

```powershell
dotnet build src/Apparatus.App/Apparatus.App.csproj --no-restore -p:Platform=x64
dotnet test tests/Apparatus.Core.Tests --no-restore
.\build\release.ps1                       # gera o instalador em artifacts\releases (ignorada pelo git), na versão do código
```

- Use `--no-restore`: o restore da solução falha numa fonte NuGet privada desta máquina; os pacotes já estão no cache.
- O build com `-p:Platform=x64` escreve em `src\Apparatus.App\bin\x64\Debug\...`. A pasta `bin\Debug` é **antiga**: não execute dela.
- Erros de XAML em cascata (`Cannot resolve DataType`, `Unknown type`) quase sempre vêm de **um erro de C#**: corrija o C# primeiro.
- O `Apparatus.exe` fica bloqueado enquanto roda: encerre o processo de teste antes de recompilar (veja a seção de testes seguros).
- **A versão fica em um só lugar:** `<Version>` do `Directory.Build.props` (nenhum `.csproj` define a sua; o teste `AppVersionTests` confere). Antes de
  gerar uma versão nova, suba esse número, seguindo a sequência (0.9.x), num pull request; a tag da release tem de ser `v` + esse número. `-Version` só serve para
  um build descartável. O `release.ps1` cria o delta sobre a versão anterior e copia `LICENSE`, `THIRD-PARTY-NOTICES.md` e `licenses\` para o pacote. Sem assinatura de código, o SmartScreen avisa na primeira execução.
- A `master` só aceita pull request, e cada um passa por uma verificação no GitHub (compila, testes, ensaio do instalador; `docs/VERIFICACAO-PR.md`).
  Rode os testes e o `verify-release.ps1` antes de abrir o PR, para não descobrir no GitHub. Para lançar, crie a tag `vX.Y.Z` **na `master`** e envie (`git push origin vX.Y.Z`): o workflow
  `.github/workflows/release.yml` roda os testes, gera o instalador, confere com `build\verify-release.ps1` e **publica** a release no GitHub.
  Passo a passo em `docs/LANCAMENTO.md`. **Não crie nem envie tag sem o dono do projeto pedir**: publicar é público e a release pela metade
  precisa ser desfeita à mão. O `release.ps1` falha se faltar licença (`-AllowMissingLicenses` só para teste local).

## Regras do projeto

- **Nada é injetado** no Explorer nem em outros programas: só APIs públicas do Windows.
- **Nada sai do PC sem o usuário ligar.** O assistente de IA vem desligado, usa a chave do próprio usuário (DPAPI, nunca em configurações, backup ou
  pasta de sincronização) e a IA **só sugere**: as chamadas são validadas em C# contra listas fechadas e viram botões. Veja `docs/ASSISTENTE-IA.md`.
- **O menu Iniciar do Windows tem de ser sempre recuperável** se a substituição falhar.
- **Alterações no registro do Windows** passam pelo `TweakService`, que guarda o valor original e restaura exatamente. Um ajuste de recurso
  próprio vai em `TweakCatalog.Internal` (fora da página de ajustes e do “desfazer tudo”). Nunca deixe uma opção do Windows alterada se a
  funcionalidade que a exigia não ficou ligada.
- **Recursos novos vêm desligados** (opt-in) quando tomam uma tecla do sistema, gravam o que o usuário digita/copia ou mandam dados para fora.
- Nova configuração: propriedade em `AppSettings` com padrão seguro (arquivos antigos continuam válidos), entra no backup sozinha; alterações
  passam por `AppServices.ApplySettings`.
- Dependência nova só com bom motivo (o instalador já tem ~80 MB). Veja “Pacotes de terceiros”.
- Segredos fora do repositório (chaves, tokens, `.env`).

## Ao terminar uma mudança

1. **Testes unitários** em `tests/Apparatus.Core.Tests` para a lógica nova ou alterada (em `Core`, para ser testável). Rode a suíte inteira; tem de ficar verde.
2. **Veja o resultado funcionando**: o que é de tela, teclado, mouse ou DPI só se confirma rodando o app (perfil isolado, abaixo). Diga o que
   foi verificado e **o que não foi**; os documentos seguem esse hábito.
3. **Atualize a documentação** (lista abaixo).
4. **Confira o `git status`** antes de commitar: nada de `bin/`, `obj/`, `artifacts/`, capturas de tela de teste, segredos.
5. **Não faça commit nem push sem o dono do projeto pedir.** Quando pedir, mensagem curta no estilo do histórico (verbo no presente em português:
   “Adiciona…”, “Corrige…”, “Ajusta…”), explicando o que mudou e por quê, e termine com a linha de coautoria que o ambiente indicar.

## Como documentar

Tudo em `docs/` é em português. O que atualizar, conforme a mudança:

| Mudou | Atualize |
|---|---|
| Um módulo novo ou comportamento de usuário | Um `docs/<MODULO>.md` (como funciona para o usuário, arquitetura, decisões e o porquê, testes e verificação), linha em `docs/PLANO.md`, item no `README.md` **e** no `README.pt-BR.md` |
| Algo ficou de fora, foi adiado ou não foi verificado | `docs/PENDENCIAS.md` (com a data), nunca só na conversa |
| Estrutura de pastas ou regra de contribuição | `CONTRIBUTING.md` e `CONTRIBUTING.pt-BR.md` (e este arquivo) |
| Pacote de terceiros | `THIRD-PARTY-NOTICES.md` e `src/Apparatus.Core/Resources/third-party-licenses.json` |
| Processo de lançamento ou o workflow de release | `docs/LANCAMENTO.md` |
| A verificação dos pull requests (`.github/workflows/ci.yml`) ou o `.github/dependabot.yml` | `docs/VERIFICACAO-PR.md` |
| Contagem de testes | `docs/PENDENCIAS.md` (seção “Como retomar”) |

Escreva o **porquê** das decisões e separe **“verificado”** de **“não verificado”**. Não registre como feito o que não foi testado.
Os README e os CONTRIBUTING existem em inglês (padrão) e português: mexeu em um, mexa no outro.

## Pacotes de terceiros

Ao adicionar ou atualizar um pacote NuGet (ou levar uma DLL nativa): confira a licença; atualize `THIRD-PARTY-NOTICES.md` (tabela, texto da
licença, se for nova) **e** `third-party-licenses.json` (alimenta Configurações → Sobre; o teste `ThirdPartyLicensesTests` confere que as
duas listas concordam). Se o `release.ps1` precisar levar um arquivo de licença do pacote, acrescente-o lá. Ao atualizar o `Velopack`, atualize
junto a versão do `vpk` onde ela aparece (o teste `VpkVersionTests` confere).

## Testes manuais com segurança

- **Perfil isolado**: defina `APPARATUS_PROFILE_DIR` (pasta com `config\settings.json`) e use outro atalho do launcher e da funcionalidade
  (por exemplo `Ctrl+Alt+F9` e `Ctrl+Alt+F8`). O app tem instância única por perfil, então o do dono do projeto, instalado, continua aberto.
- **Nunca encerre o `Apparatus.exe` pelo nome**: ele derrubaria o app instalado do usuário. Filtre pelo caminho (`Path -like "D:\Repos*"`)
  ou pelo PID que você iniciou.
- Se o teste mexer num valor do Windows, **guarde o original e devolva-o** (ex.: `PrintScreenKeyForSnippingEnabled` na máquina do dono é `0`).
- Para automatizar a tela: UI Automation e `mouse_event` absoluto sobre a área virtual (movimentos simulados com `SetCursorPos` não geram
  todos os eventos de ponteiro). Não deixe imagens de teste com a tela do usuário no disco ou no repositório.
- O Lightshot do usuário costuma estar aberto e segura Print Screen; use outra tecla nos testes e não o feche.

## Armadilhas conhecidas

- Elementos XAML **colapsados medem zero**: deixe visível antes de `Measure`.
- A dica padrão do XAML leva ~1 s para aparecer; a barra de captura usa uma própria.
- Em `System.IO.Path` × `Microsoft.UI.Xaml.Shapes.Path` use o nome completo; o mesmo vale para `Buffer`.
- Ícones das barras e do menu lateral são caracteres do Segoe Fluent Icons, guardados como o caractere (ou `\uXXXX` em C#). Cuidado com
  ferramentas de texto que perdem a barra invertida; prefira editar com um script ou com a ferramenta de edição de arquivos.
- O seletor de arquivos do Windows só abre em pastas conhecidas; salvar numa pasta qualquer exige gravar direto.
- Um app **não empacotado** não pode usar `ApplicationData.Current`; configurações vão para o `settings.json`.
- Linhas de comando longas com aspas e `here-doc` no Bash dão erro de sintaxe com facilidade: para scripts maiores, crie um arquivo e rode-o.

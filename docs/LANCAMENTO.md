# Como lançar uma versão

O **GitHub compila o instalador e publica a release** quando uma tag de versão é enviada. Você só prepara a `master` e cria a tag. Se qualquer
verificação falhar, o workflow para **antes** de criar a release: nada incompleto é publicado.

## Passo a passo

1. Deixe tudo mesclado na `master` (ela só aceita mudanças por pull request) e atualize o clone: `git switch master` e `git pull`.
2. Crie a tag **em cima da `master`**, no formato `vX.Y.Z` (a sequência é 0.9.x), e envie: `git tag v0.9.16` e `git push origin v0.9.16`.
3. Acompanhe na aba **Actions** do repositório (workflow "Criar release"). Leva alguns minutos.
4. Quando termina, a release "Apparatus v0.9.16" já está **publicada**, com as notas geradas a partir dos pull requests mesclados desde a
   release anterior e com o instalador, o zip portátil e os arquivos de atualização anexados.
5. Baixe o `Setup.exe` da release e instale pelo Explorador de Arquivos (duplo clique) para conferir. O build do GitHub não passa por nenhum teste
   de tela: quem confere o programa é você.

O `build\release.ps1` continua servindo para gerar um instalador **local**, para testar antes de lançar.

## O que o workflow faz, nesta ordem (`.github/workflows/release.yml`)

Roda num Windows do GitHub (`windows-latest`). Cada passo que falha encerra tudo:

1. **Nome da tag:** só aceita `vX.Y.Z`. Uma tag como `v1.2.3-teste` casa com o gatilho do GitHub, mas é recusada aqui.
2. **Tag na `master`:** recusa uma tag que aponte para um commit que não foi mesclado.
3. **.NET:** instala o SDK da versão do `global.json`.
4. **Testes:** `dotnet test tests/Apparatus.Core.Tests` (tem de ficar verde).
5. **vpk:** instala o empacotador do Velopack na versão que o app usa (1.2.161).
6. **Instalador:** `build\release.ps1 -Version X.Y.Z`. O script **falha se faltar algum arquivo de licença** (os do .NET e do Windows App SDK
   são copiados do cache do NuGet). Para um teste local sem as licenças existe `-AllowMissingLicenses`; o workflow nunca o usa.
7. **Conferência:** `build\verify-release.ps1 -Version X.Y.Z`, que você também pode rodar à mão depois do `release.ps1`. Confere:
   - o `Setup.exe` (com tamanho plausível, mais de 40 MB), o zip portátil, o pacote completo, o `RELEASES` e o `releases.win.json`;
   - que o `releases.win.json` lista esta versão, com o tamanho e o SHA-256 do arquivo que está na pasta;
   - que o `Apparatus.exe` diz ser desta versão;
   - que as licenças estão na pasta publicada **e dentro do pacote** que o instalador e as atualizações entregam.
8. **Publicação:** cria a release (sem rascunho) com as notas geradas, agrupadas por rótulo (`.github/release.yml`), e anexa tudo de
   `artifacts\releases`. Falha se não houver arquivo para anexar.

## Quando o workflow falha

- Veja o passo marcado em vermelho na aba Actions; a mensagem diz o que faltou.
- Se falhou **antes** do passo 8, nenhuma release foi criada. Corrija na `master` (por pull request), apague a tag e crie de novo:
  `git push origin --delete v0.9.16`, `git tag -d v0.9.16`, e volte ao passo 2.
- Se falhou **no** passo 8 depois de criar a release, apague a release e a tag no GitHub e refaça.

## Por que é assim

- **Publicação direta, sem rascunho.** Decisão do dono do projeto. Como não há como conferir o instalador antes de ele ficar público, as
  verificações do workflow são rigorosas: é por isso que ele compara o feed com o arquivo e olha dentro do pacote.
- **A versão vem da tag.** A `<Version>` do `Apparatus.App.csproj` (0.7.0) não é usada: o `release.ps1` passa `-p:Version`, e o workflow tira o `v`
  da tag. Assim a release e o programa não divergem.
- **As notas agrupam por rótulo** (`.github/release.yml`): `enhancement` em Novidades, `bug` em Correções, `documentation` em Documentação e o
  resto em Outras mudanças. Um pull request com o rótulo `ignore-for-release` não aparece. O título do pull request é o texto da nota: escreva-o
  como uma frase para o usuário.
- **O instalador do GitHub também não é assinado.** O SmartScreen avisa como antes (`docs/ASSINATURA.md`).

## Verificado e não verificado

- **Verificado (nesta máquina):** os 770 testes passam com o mesmo comando do workflow; o `release.ps1` gera o instalador numa pasta de teste e a
  conferência dá OK; a conferência falha, com a mensagem certa, para versão errada e para licença faltando; o `release.ps1` aborta, antes de empacotar,
  quando uma licença não é encontrada; o YAML e a sintaxe PowerShell de cada passo leem sem erro; a regra do nome da tag aceita `v0.9.16` e `v1.0.0` e recusa
  `v1.2.3-teste`, `va.b.c` e `v1.2`.
- **Não verificado:** o workflow **nunca rodou** no GitHub. Só a primeira tag mostra: se o runner restaura e compila o app sem a máquina do mantenedor,
  se o `setup-dotnet` acha o SDK do `global.json`, se o `vpk` fica no `PATH`, se o `GITHUB_TOKEN` pode escrever (Settings → Actions → General →
  Workflow permissions), se as notas saem agrupadas e se o `Setup.exe` instala e abre. Espere ajustes na primeira tentativa; uma tag que falha antes
  do passo 8 não deixa nada publicado.

## Ideias para depois

- **Pacote delta** (atualização de poucos MB em vez de ~75 MB): depois que existir uma release feita pelo workflow, baixar a release anterior com
  `vpk download github` antes do `release.ps1`; o script já cria o delta quando o pacote anterior está na pasta. Tem de tolerar a primeira release (que
  não tem anterior), e a conferência passaria a esperar o `*-delta.nupkg` também.
- **Atualização automática pelo GitHub:** o app já aceita um endereço web em Configurações → Atualizações. O endereço
  `https://github.com/rafaelbruske/Apparatus/releases/latest/download` deve servir sem mudar código (o GitHub redireciona `latest/download/<arquivo>` para a
  release mais recente que não seja rascunho nem pré-lançamento), mas **não foi testado**. Manter a opção **desligada por padrão**, como a regra
  "nada sai do PC sem o usuário ligar" exige, por exemplo com um botão "Usar o GitHub" que preenche o campo, e dizer no README o que é enviado.
- **Verificação em pull requests:** um workflow que roda o build e os testes em cada PR, para a `master` só aceitar o que compila.

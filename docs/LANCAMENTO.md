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
- **A ação que publica é fixada pelo SHA do commit** (26/11/2026: `softprops/action-gh-release`, `v3.0.4`, `8a0cd651…`). Ela é de terceiro, roda com
  `contents: write` e entrega o que os usuários instalam e recebem como atualização. Uma tag (`v3`, ou mesmo `v3.0.4`) pode ser apontada para outro código
  por quem controla o repositório da ação; um SHA não. As ações do próprio GitHub (`actions/checkout`, `actions/setup-dotnet`) continuam por tag.
  - **O custo:** correções não chegam sozinhas, e o GitHub **não gera alerta do Dependabot** para ação fixada por SHA. O
    `.github/dependabot.yml` abre, uma vez por mês, um pull request com as versões novas (`docs/VERIFICACAO-PR.md`).
  - **Para atualizar:** `git ls-remote --tags https://github.com/softprops/action-gh-release "vX.Y.Z*"` e use o SHA da linha terminada em `^{}` (o do
    commit; sem essa linha, a tag é simples e o único SHA já é o do commit). Troque o SHA e o comentário no `release.yml`, num pull request, depois de ler as
    notas da versão. O `release.yml` só roda numa tag, então o pull request não exercita essa linha.

## Verificado e não verificado

- **Verificado (nesta máquina):** os 770 testes passam com o mesmo comando do workflow; o `release.ps1` gera o instalador numa pasta de teste e a
  conferência dá OK; a conferência falha, com a mensagem certa, para versão errada e para licença faltando; o `release.ps1` aborta, antes de empacotar,
  quando uma licença não é encontrada; o YAML e a sintaxe PowerShell de cada passo leem sem erro; a regra do nome da tag aceita `v0.9.16` e `v1.0.0` e recusa
  `v1.2.3-teste`, `va.b.c` e `v1.2`.
- **Já visto no GitHub** (26/11/2026, pela verificação dos pull requests, que repete os passos 3 a 7 com uma versão descartável; `docs/VERIFICACAO-PR.md`): o
  runner restaura e compila o app sem a máquina do mantenedor, o `setup-dotnet` acha o SDK do `global.json`, o `vpk` fica no `PATH`, o `release.ps1` gera o
  `Setup.exe` (82 MB) sem faltar licença e a conferência dá OK.
- **Não verificado:** o workflow de release **nunca rodou**. Só a primeira tag mostra: a checagem do nome da tag e da presença na `master`, se o `GITHUB_TOKEN`
  pode escrever (Settings → Actions → General → Workflow permissions), a criação da release, se as notas saem agrupadas e se o `Setup.exe` gerado lá instala e
  abre. Espere ajustes na primeira tentativa; uma tag que falha antes do passo 8 não deixa nada publicado.

## Atualização automática pelo GitHub (25/11/2026)

Em Configurações → Atualizações e dados, o botão **Usar o GitHub** preenche o campo "De onde vêm as atualizações" com
`https://github.com/rafaelbruske/Apparatus` e liga a verificação diária (sem reiniciar). É **opt-in**: a fonte vem vazia e nada é consultado até o usuário
clicar (ou digitar um endereço). Quem digita o endereço de um repositório (`https://github.com/dono/repo`, também de um fork) tem o mesmo comportamento;
pasta e endereço web comuns continuam como antes.

- **Por que o `GithubSource` e não o endereço de download da release:** com um endereço web comum o Velopack acrescenta ao pedido informações da máquina
  (medi `?arch=x64&os=win&rid=win-x64`) e, segundo o código da biblioteca, pode acrescentar a versão instalada e um identificador anônimo da instalação.
  O `GithubSource` faz o contrário: pergunta à API qual é a release mais recente e **compara no próprio app**.
- **Medido** com um servidor de gravação no lugar do GitHub (programa de teste na pasta temporária, fora do repositório): o pedido do `GithubSource` é só
  `GET https://api.github.com/repos/rafaelbruske/Apparatus/releases?per_page=10&page=1` com o cabeçalho `Accept`, mais o `User-Agent: Velopack/1.2.161` que o
  cliente HTTP padrão da biblioteca manda (visto no teste do endereço web comum, que usa o mesmo cliente). Nem a versão do Apparatus nem identificador algum vão junto. O GitHub vê, como em qualquer pedido, o IP.
- **Limite do GitHub:** sem autenticação são 60 pedidos por hora por IP. Uma verificação por dia não chega perto, mas numa rede compartilhada o GitHub pode recusar;
  a tela mostra a mensagem de erro e a verificação seguinte tenta de novo.
- Só vê **releases estáveis** (o app não pede pré-lançamentos). Rascunhos não aparecem sem autenticação.
- **Não verificado:** contra o GitHub de verdade, porque ainda não existe release feita pelo workflow. Falta ver, depois da primeira: se o app acha a release,
  baixa o pacote completo e se atualiza (o `releases.win.json` e o `.nupkg` precisam estar entre os arquivos da release, o que o workflow faz); a aparência
  com o tema claro e com DPI alto; e a primeira verificação feita pelo temporizador diário (a verificação "Verificar agora" usa o mesmo caminho).

## Ideias para depois

- **Pacote delta** (atualização de poucos MB em vez de ~75 MB): depois que existir uma release feita pelo workflow, baixar a release anterior com
  `vpk download github` antes do `release.ps1`; o script já cria o delta quando o pacote anterior está na pasta. Tem de tolerar a primeira release (que
  não tem anterior), e a conferência passaria a esperar o `*-delta.nupkg` também.

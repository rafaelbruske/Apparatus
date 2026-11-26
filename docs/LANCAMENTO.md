# Como lançar uma versão

Duas partes: o **instalador** é feito no PC do mantenedor (`build\release.ps1`); a **release no GitHub** (a página com as notas e os
downloads) é criada por um workflow quando uma tag de versão é enviada. O workflow **não compila nada**.

## Passo a passo

1. Com tudo mesclado na `master` (ela só aceita mudanças por pull request), atualize o seu clone: `git switch master` e `git pull`.
2. Gere o instalador: `.\build\release.ps1 -Version 0.9.16` (a sequência é 0.9.x). O script cria o delta sobre a versão anterior se o pacote dela
   estiver em `artifacts\releases`. Para publicar só a versão nova, apague da pasta os pacotes das versões anteriores e o `*-delta.nupkg`, e deixe
   `RELEASES` e `releases.win.json` listando só o pacote completo novo. Instale pelo Explorador de Arquivos e confira.
3. Crie a tag **em cima da `master`** e envie: `git tag v0.9.16` e `git push origin v0.9.16`. O nome é `v` + a versão do passo 2.
4. O workflow [`.github/workflows/release.yml`](../.github/workflows/release.yml) roda (aba **Actions**) e cria um **rascunho** de release
   chamado "Apparatus v0.9.16", com as notas geradas a partir dos pull requests mesclados desde a release anterior.
5. Anexe o instalador ao rascunho, pela página da release ou pela linha de comando:
   `gh release upload v0.9.16 artifacts\releases\ApparatusApp-win-Setup.exe artifacts\releases\ApparatusApp-win-Portable.zip`.
6. Leia as notas, ajuste o que quiser e clique em **Publish release**.

## Por que é assim

- **A release é um rascunho.** O Setup.exe (82 MB) é feito aqui, não no GitHub; uma release pública sem ele pareceria quebrada. O rascunho dá o
  tempo de anexá-lo. Para publicar direto, troque `draft: true` por `false` em `release.yml`.
- **O workflow não compila.** Compilar no GitHub pediria um runner Windows com o .NET 10, o `vpk` e os pacotes do WinUI, e o resultado não é
  assinado de qualquer modo. Isso só vale a pena quando houver assinatura de código (`docs/ASSINATURA.md`); até lá o instalador sai do
  `release.ps1`, que já é conhecido e testado.
- **A tag precisa estar na `master`.** O primeiro passo do workflow falha se a tag apontar para um commit que não foi mesclado, para que uma tag
  feita por engano numa branch não vire release.
- **A versão vem da tag e do `-Version`.** A `<Version>` do `Apparatus.App.csproj` (0.7.0) não é usada: o `release.ps1` passa `-p:Version`.
  Mantenha os dois iguais, senão a release diz uma coisa e o programa outra.
- **As notas agrupam por rótulo** (`.github/release.yml`): `enhancement` em Novidades, `bug` em Correções, `documentation` em Documentação e o
  resto em Outras mudanças. Um pull request com o rótulo `ignore-for-release` não aparece. Sem rótulo, cai em "Outras mudanças"; o título do
  pull request é o texto da nota, então escreva-o como uma frase para o usuário.

## Verificado e não verificado

- **Verificado:** o YAML dos dois arquivos lê sem erro, o gatilho é só `push` de tag `v*.*.*`, a sintaxe do passo de conferência está certa e a
  conferência dá o resultado esperado numa simulação local (o commit da `master` passa; um commit que só existe em outra branch falha).
- **Não verificado:** o workflow **nunca rodou** no GitHub. Falta ver na primeira tag: se o rascunho é criado, se o passo "Conferir que a tag está
  na master" funciona com o checkout de uma tag, se as notas saem agrupadas e se `gh release upload` aceita o `Setup.exe`. Também depende de o
  repositório permitir que o `GITHUB_TOKEN` escreva (Settings → Actions → General → Workflow permissions); o workflow pede `contents: write`
  sozinho, mas uma regra da organização pode negar.
- Uma tag no padrão `v*.*.*` também casa com nomes como `v1.2.3-teste`; o rascunho sairia assim mesmo. Apague a release e a tag se acontecer.

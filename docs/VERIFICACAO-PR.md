# Verificação dos pull requests

Todo pull request para a `master` passa por uma verificação automática no GitHub (`.github/workflows/ci.yml`, workflow **"Verificar"**). Com ela marcada
como obrigatória, a `master` só aceita o que compila, passa nos testes e ainda consegue gerar o instalador.

## O que roda

Um único trabalho, **"Compilar e testar"**, num Windows do GitHub. Acontece em cada pull request para a `master`, em cada commit novo dele (o que ainda
estiver rodando para a versão anterior é cancelado) e de novo na `master` depois do merge. Também pode ser disparado à mão na aba Actions.

1. **Compila o app** (`Apparatus.App`, Release, x64) e os quatro projetos `Apparatus.E2E.*`. Esses últimos só são **compilados**, nunca executados (eles
   apertam teclas de verdade), para que uma mudança que os quebre apareça.
2. **Roda os testes unitários** (`tests/Apparatus.Core.Tests`).
3. **Confere a sintaxe dos scripts de `build\`** (`release.ps1` e `verify-release.ps1`). Um erro de digitação ali só apareceria no dia da release.
4. **Ensaia o instalador:** instala o `vpk`, roda o `release.ps1` com uma versão descartável (`0.0.1`, numa pasta `artifacts\ensaio`) e o
   `verify-release.ps1`. Nada é publicado. Pega, antes de uma tag, licença faltando, publicação quebrada ou pacote que não bate com o feed.

Qualquer passo que falhe deixa o pull request vermelho, com a mensagem do passo (erros de sintaxe aparecem como anotação no arquivo e na linha).

## O que o dono do projeto precisa fazer (uma vez)

O workflow sozinho só **avisa**; para **impedir** o merge de um pull request vermelho é preciso marcar a verificação como obrigatória:

1. Abra um pull request qualquer para a `master` e espere o "Compilar e testar" rodar uma vez. O GitHub só oferece na lista as verificações que já rodaram
   nos últimos dias.
2. Settings → Branches → a regra da `master` (ou Rulesets, se foi assim que a proteção foi criada) → **Require status checks to pass before merging**.
3. Procure e marque **"Compilar e testar"**. Vale também marcar **Require branches to be up to date before merging** (assim a verificação é feita sobre a
   `master` mais recente, não sobre uma base antiga).

Se alguém renomear o trabalho no `ci.yml`, a regra deixa de achar a verificação e o merge fica travado (ou, pior, passa sem ela): mude os dois juntos.

## Por que é assim

- **Um trabalho só, e o ensaio do instalador sempre.** O custo é de alguns minutos num repositório público (gratuito), e o ensaio é o que mais protege: o
  `release.ps1` é a parte mais frágil do processo e só era exercitado no dia de lançar. Com ele em todo pull request, a primeira tag deixa de ser a primeira vez
  que o runner do GitHub compila o app. Se ficar lento ou instável, o passo 4 pode virar um trabalho separado que só roda quando mudam `build\`, os `.csproj`,
  o `global.json` ou os workflows.
- **Os testes de tela não rodam aqui.** Teclado, mouse, DPI e tema dependem de uma sessão de usuário real; seguem como verificação manual, descrita nos
  documentos (`docs/PENDENCIAS.md`).
- **`contents: read`:** a verificação não precisa escrever em nada; um pull request vindo de um fork roda sem acesso a segredos.
- **Cancelar o que ficou velho:** um novo commit no mesmo pull request cancela a verificação anterior, para não gastar tempo com código já substituído.

## Verificado e não verificado

- **Verificado nesta máquina:** o YAML lê sem erro; os quatro passos de `run` foram extraídos do próprio arquivo e executados em ordem (compilação do app e dos E2E,
  798 testes, sintaxe dos scripts, ensaio do instalador com a conferência dando OK); o passo de sintaxe falha, com anotação de arquivo e linha, num script
  quebrado.
- **Não verificado:** o workflow **nunca rodou no GitHub**. A primeira execução, no próprio pull request que o traz, mostra: se o runner restaura e compila
  sem a máquina do mantenedor, se o `setup-dotnet` acha o SDK do `global.json`, o tempo total e se o `vpk` fica no `PATH`. O ensaio do instalador é a mesma
  parte que o workflow de release usa, então a primeira execução também antecipa os riscos da primeira tag. Também não foi testada a regra de proteção.

# Como contribuir com o Apparatus

[English](CONTRIBUTING.md) · **Português**

Obrigado por querer ajudar. O Apparatus é um utilitário para Windows 11 (launcher, snippets, ações e macros, menu Iniciar
próprio e um assistente de IA opcional), escrito em C# com WinUI 3. Relatos de erros, ideias, correções na documentação e
pull requests são bem-vindos, em português ou em inglês.

Ao contribuir você concorda que a sua contribuição seja liberada sob a licença do projeto, a
[Licença BSD Zero Cláusula](LICENSE.pt-BR.md) (0BSD; o texto legal é o arquivo [`LICENSE`](LICENSE), em inglês), que não exige
créditos nem a manutenção de nenhum aviso.

## Antes de começar

- **Erros e ideias:** abra uma issue antes de qualquer coisa maior que uma correção pequena, para combinarmos o rumo antes
  de você gastar tempo.
- **Dúvidas sobre como algo funciona:** as decisões e o que cada fase entregou estão em [`docs/`](docs), em português.
  [`docs/PENDENCIAS.md`](docs/PENDENCIAS.md) lista o que se sabe que falta ou que não foi verificado.
- **Problemas de segurança:** **não** abra uma issue pública com detalhes. Use o relatório privado de vulnerabilidade do
  GitHub, se estiver ativado no repositório; senão, fale com o mantenedor em particular pelo GitHub. Os detalhes estão em
  [`SECURITY.pt-BR.md`](SECURITY.pt-BR.md).

O botão **New issue** oferece um formulário para relatar um erro e outro para sugerir uma funcionalidade (os dois bilíngues);
os campos pedem o que é necessário, descrito abaixo. Ao relatar um erro, informe a versão do Apparatus, a versão do Windows (`winver`), os passos para reproduzir e o que você
esperava. O registro (log) fica em `%LOCALAPPDATA%\Apparatus\logs`. **Nunca cole uma chave de API, histórico da área de
transferência ou outro texto pessoal** numa issue; apague isso do log antes.

## Preparar o ambiente

Você precisa do Windows 11 e do SDK do .NET na versão de [`global.json`](global.json) (.NET 10). A interface usa o WinUI 3
pelo Windows App SDK, restaurado como pacotes NuGet (autocontido), então nenhuma carga de trabalho extra deve ser
necessária.

```powershell
git clone https://github.com/rafaelbruske/Apparatus.git
cd Apparatus
dotnet build src/Apparatus.App/Apparatus.App.csproj
dotnet test tests/Apparatus.Core.Tests
dotnet run --project src/Apparatus.App
```

Para experimentar sem mexer nas suas configurações, snippets e ações de verdade, aponte o app para uma pasta de perfil
descartável com a variável de ambiente `APPARATUS_PROFILE_DIR`. Use outro atalho do launcher nesse perfil se o padrão
(`Alt+Space`) já estiver tomado pela cópia que você usa todo dia.

## Como o código está organizado

| Pasta | O que há nela |
|---|---|
| `src/Apparatus.Core` | Tudo o que não precisa de janela: pesquisa, snippets, ações, backup, dados do menu Iniciar, o motor do assistente de IA, a geometria da captura de tela. `net10.0` puro, sem interface e sem P/Invoke. **Coloque a lógica nova aqui.** |
| `src/Apparatus.Native` | Chamadas à API do Windows (P/Invoke, shell, ganchos). Mantenha enxuto; sem regras de negócio. |
| `src/Apparatus.App` | O app WinUI 3: janelas, controles, bandeja, ligações. Também deve ficar enxuto. |
| `tests/Apparatus.Core.Tests` | Os testes unitários (xUnit). |
| `tests/Apparatus.E2E.*`, `tests/Apparatus.UI` | Verificações que pressionam teclas, clicam ou conduzem a interface. Cada uma diz no cabeçalho o que faz; não mexa no teclado nem no mouse enquanto uma roda. |
| `build/` | O script de lançamento ([`release.ps1`](build/release.ps1)): publicação autocontida empacotada com o Velopack, e o [`verify-release.ps1`](build/verify-release.ps1), que confere o resultado antes de publicar. |
| `.github/` | Formulários de issue, modelo de pull request, a verificação dos pull requests (`ci.yml`) e o workflow de release (uma tag `vX.Y.Z` enviada roda os testes, gera o instalador e publica a release; veja [`docs/LANCAMENTO.md`](docs/LANCAMENTO.md)). |
| `docs/` | Planos, notas de cada fase, guia de extensões, pendências. |

## Estilo do código

- C# com tipos de referência anuláveis habilitados (veja [`Directory.Build.props`](Directory.Build.props)). Siga o estilo do
  arquivo que você está editando.
- Identificadores e comentários de código ficam em **inglês**. O texto que o usuário vê na tela fica em **português do
  Brasil**, por enquanto.
- Prefira uma classe pequena e testável em `Apparatus.Core` a colocar lógica numa classe de janela.
- Não adicione uma dependência sem um bom motivo; cite-a no pull request (o instalador já tem cerca de 80 MB). Se adicionar ou atualizar uma, confira a licença dela e atualize o [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md), que o instalador leva junto (em inglês), e também o [`third-party-licenses.json`](src/Apparatus.Core/Resources/third-party-licenses.json), que alimenta Configurações → Sobre (um teste confere que as duas listas concordam).

## Princípios que o projeto mantém

São fáceis de quebrar sem querer, então confira a sua mudança contra eles:

- **Nada é injetado** no Explorer nem em outros programas; só APIs públicas do Windows.
- **Nada sai do computador sem o usuário ligar.** O assistente de IA vem desligado, usa a chave do próprio usuário
  (criptografada com o DPAPI, nunca nas configurações, nos backups nem na pasta de sincronização) e envia só o que o
  usuário digita.
- **A IA só sugere.** As chamadas de ferramenta são validadas em C# contra listas fechadas e viram botões; nada roda até o
  usuário clicar. Ações propostas nunca contêm comandos nem PowerShell. Veja [`docs/ASSISTENTE-IA.md`](docs/ASSISTENTE-IA.md).
- **O menu Iniciar do Windows tem de poder voltar sempre** se a substituição se comportar mal.
- **Segredos ficam fora do repositório** (chaves, tokens, arquivos `.env`).

## Testes

- Adicione ou atualize testes unitários em `tests/Apparatus.Core.Tests` para qualquer lógica que você criar ou mudar. Rode
  `dotnet test tests/Apparatus.Core.Tests` antes de abrir o pull request; tudo deve passar.
- Alguns testes são opcionais porque chamam um serviço real. Por exemplo, o que faz ao provedor de IA as perguntas de
  referência de [`assistant-eval.json`](tests/Apparatus.Core.Tests/assistant-eval.json) só roda quando você define uma chave
  numa variável de ambiente como `APPARATUS_TEST_GEMINI_KEY`; sem ela, retorna na hora.
- Se você mudar algo que não dá para testar automaticamente (aparência, teclado real, DPI alto), diga o que verificou à
  mão e o que não verificou. A documentação segue esse hábito: separa "verificado" de "não verificado".

## Pull requests

1. Faça um fork e crie uma branch a partir da `master`.
2. Mantenha a mudança focada: uma correção ou recurso por pull request.
3. Escreva a mensagem do commit como uma frase curta dizendo o que mudou e por quê (o histórico usa verbos em português,
   como "Adiciona…" e "Corrige…"; inglês também serve).
4. Atualize a documentação se o comportamento mudar: o texto para o usuário em `README.md` / `README.pt-BR.md` e a nota
   correspondente em `docs/`.
5. No pull request, descreva o que mudou, como testou e o que não conseguiu testar. A descrição já abre com um modelo
   ([`.github/PULL_REQUEST_TEMPLATE.md`](.github/PULL_REQUEST_TEMPLATE.md)) que pede exatamente isso, mais uma lista de conferência das regras acima.
6. Todo pull request é verificado automaticamente no GitHub: o app e os testes têm de compilar, os testes unitários têm de passar e o instalador tem de
   continuar sendo gerado ([`docs/VERIFICACAO-PR.md`](docs/VERIFICACAO-PR.md)). Rode `dotnet test tests/Apparatus.Core.Tests` você mesmo antes; uma
   verificação vermelha impede o merge.

Seja gentil e paciente nas issues e nas revisões. Discorde da ideia, não da pessoa.

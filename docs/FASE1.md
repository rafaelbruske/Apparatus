# Fase 1 — Launcher (módulo C)

Estado: **implementada e verificada** no ambiente de desenvolvimento (Windows 11 25H2, .NET 10, Windows App SDK 2.7).

## O que o app faz

- Roda na bandeja do sistema, em instância única (abrir o `.exe` de novo mostra o launcher).
- **Atalho global configurável** (padrão `Ctrl+Alt+Space`; trocar para `Alt+Space` é só mudar em
  Configurações → Atalho do launcher → Alterar, depois de fechar o Raycast). Se o atalho estiver em uso por
  outro programa (erro 1409), o app avisa e abre as configurações em vez de falhar em silêncio.
- Janela do launcher centralizada no monitor do cursor, acrílica, com ícones reais, destaque das letras
  casadas, rolagem por teclado e dimensionamento conforme o número de resultados.
- Resultados, todos em uma lista só, ordenados por relevância + histórico:
  - **Aplicativos** (clássicos e da Microsoft Store, via `shell:AppsFolder`), com cache no SQLite.
  - **Arquivos e pastas** de todos os discos configurados (índice próprio, ver abaixo).
  - **Configurações e ferramentas do Windows** (~100 entradas, em português e inglês, com palavras-chave).
  - **Calculadora** (`2+2*3`, `sqrt(16)`, `2pi`, vírgula ou ponto decimal) e **conversão de unidades**
    (`10 km to mi`, `5 kg em lb`, `100 c para f`).
  - **Web**: `yt lofi`, `g texto`, `gh`, `w`, `maps`; endereços (`github.com/dotnet`) e pesquisa padrão como
    último recurso.
  - **Comandos do Apparatus**: configurações, reindexar, limpar histórico, pasta de dados, sair.
- **Histórico/frecency**: itens usados com frequência sobem; o que você escolheu para uma consulta
  (`ch` → Firefox) sobe para consultas parecidas. Consulta vazia mostra os itens mais relevantes do
  histórico. `Shift+Delete` na lista vazia esquece o item selecionado.
- Teclas: `↑ ↓ Tab` navegam, `Enter` abre, `Ctrl+Enter` mostra o arquivo na pasta (ou copia, no cálculo),
  `Esc` limpa e depois fecha. Clicar fora fecha.
- Configurações: atalho, iniciar com o Windows, nº de resultados, mecanismo de busca padrão, pastas
  indexadas (automático ou lista própria), pastas ignoradas, situação do índice, limpar histórico.

## Arquitetura

```
Apparatus.Core    sem dependência de UI/Windows: busca, fuzzy, índice, histórico (SQLite), configurações, calculadora
Apparatus.Native  CsWin32 + COM: janela de mensagens (atalho + bandeja), apps, ícones, autostart, instância única
Apparatus.App     WinUI 3: launcher, configurações, composição (AppServices)
```

- **Busca**: cada fonte é um `ISearchProvider`; o `SearchEngine` roda todos em paralelo, soma o boost do
  histórico e devolve o topo. Falha de um provedor não derruba os outros.
- **Fuzzy**: subsequência estilo fzf, ignora maiúsculas e acentos, prefere início de palavra e trechos
  contíguos, vários termos (todos precisam casar) e piso de qualidade para descartar letras espalhadas.
- **Índice de arquivos**: um segmento por pasta raiz, em arrays contíguos (nomes em um `char[]`, offsets,
  pais e flags em arrays paralelos), sem objeto por arquivo. Snapshot binário em
  `%LOCALAPPDATA%\Apparatus\index` carrega em milissegundos; varredura em segundo plano a cada 6 h;
  `FileSystemWatcher` captura arquivos novos entre varreduras; itens apagados são descartados na hora da
  consulta. Caminhos fundos e pastas de build (`bin`, `obj`, `dist`…) perdem pontos.
- **Janela pré-aquecida**: a janela é criada e mostrada fora da tela na inicialização, então o primeiro
  atalho já é instantâneo.

## Medidas (máquina de desenvolvimento)

| Item | Resultado |
|---|---|
| Itens indexados (D: e F:, mais pastas do usuário) | ~690 mil |
| Memória do índice | ~68 MB (≈104 bytes por item) |
| Consulta de arquivos | 12–20 ms em 680 mil itens |
| Varredura | D: 563 mil itens em 12 s; F: 119 mil itens em 109 s a frio (HDD) |
| Snapshot | 5 MB (F:) e 31 MB (D:), carrega em 5–14 ms |
| Atalho → janela visível | ~17 ms (inclui o laço de medição) |
| Memória do app em repouso (Debug) | ~136 MB privados / ~198 MB de working set |
| Testes automatizados | 108 (Core) |

## Limitações conhecidas

- **Não verificado em uso real**: o menu da bandeja (clicar e escolher itens), a captura de um novo atalho
  na janela de configurações e a execução por `Enter`/clique dentro da janela. A abertura de programas
  (`ShellLauncher`), os ícones, o autostart e o registro do atalho foram testados à parte.
- O índice não tem busca por conteúdo, só por nome, e não acompanha renomeação de pastas inteiras até a
  próxima varredura.
- Pastas decompiladas ou de download com milhares de subpastas iguais (por exemplo `smali`) poluem os
  resultados; use "Nomes de pastas ignoradas" nas configurações.
- Consumo de memória em repouso ainda alto por ser build Debug e self-contained; medir em Release.
- Sem ainda: favoritos explícitos, extensões, histórico da área de transferência, snippets (Fase 2).
- A saída do WinUI 3 self-contained pesa ~230 MB; o empacotamento (Velopack, runtime compartilhado ou
  trimming) fica para a fase de distribuição.

## Como rodar

```powershell
dotnet build src/Apparatus.App -c Debug
src\Apparatus.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\Apparatus.exe
```

Argumentos úteis: `--background` (não mostra o launcher), `--show --query texto` (abre já com um texto),
`--settings` (abre as configurações). Logs em `%LOCALAPPDATA%\Apparatus\logs`.

# Escrevendo uma extensão para o Apparatus

Uma extensão é **qualquer programa** que fala um protocolo simples de linhas de JSON pela entrada e saída padrão.
PowerShell, Python, Node, Go, um executável seu: tanto faz. O exemplo completo está em
[`src/Apparatus.App/Samples/hello-powershell`](../src/Apparatus.App/Samples/hello-powershell).

## Onde fica

Cada extensão é uma pasta dentro de `%APPDATA%\Apparatus\extensions\` (Configurações → Extensões → "Abrir a pasta")
com um arquivo `extension.json`. Uma extensão **nunca roda sozinha**: aparece na lista de Configurações desativada, e
você a ativa depois de ler um aviso de que ela é um programa executado com as suas permissões.

## `extension.json`

```json
{
  "id": "hello-powershell",
  "name": "Olá, PowerShell",
  "description": "O que ela faz, em uma frase.",
  "version": "1.0.0",
  "author": "Você",
  "keyword": "hello",
  "command": "powershell.exe",
  "args": ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", "main.ps1"],
  "mode": "persistent",
  "timeoutMs": 4000
}
```

| Campo | Obrigatório | Significado |
|---|---|---|
| `id` | sim | 2 a 64 caracteres: minúsculas, números, `.`, `-`, `_` |
| `name` | sim | nome mostrado |
| `command` | sim | programa a executar (caminho completo, ou um nome do PATH) |
| `args` | não | argumentos; um caminho relativo que exista na pasta vira absoluto, e `{dir}` vira a pasta da extensão |
| `keyword` | não | gatilho: digitar `hello mundo` no launcher envia `mundo` à extensão (até 16 caracteres, sem espaços) |
| `mode` | não | `persistent` (padrão): um processo contínuo; `oneshot`: um processo novo por busca |
| `timeoutMs` | não | quanto esperar por uma resposta (200 a 30000, padrão 2000) |
| `debounceMs` | não | espera após a última tecla antes de perguntar (padrão: 0 no `persistent`, 250 no `oneshot`) |

A pasta da extensão é o diretório de trabalho do processo.

## O protocolo

Uma mensagem por linha, em **UTF-8 sem BOM** e sem quebras de linha dentro do JSON.

**Apparatus → extensão**

```json
{"type":"query","id":7,"query":"mundo","keyword":"hello"}
{"type":"activate","id":8,"result":"count","data":{"folder":"Downloads"}}
```

`query` é enviada a cada busca. Com o gatilho digitado sozinho (`hello`), `query` vem vazio: uma boa hora para mostrar
ajuda. `activate` chega quando o usuário escolhe um resultado cuja ação é `callback`.

**Extensão → Apparatus**

```json
{"type":"results","id":7,"results":[
  {"id":"greet","title":"Olá, mundo!","subtitle":"Enter copia","score":20,
   "icon":"path:C:\\Programas\\app.exe",
   "action":{"type":"copy","text":"Olá, mundo!"},
   "alt":{"type":"open","target":"https://exemplo.com"}}
]}
{"type":"done","id":8}
{"type":"notify","title":"Título","message":"Aparece como aviso na bandeja"}
{"type":"log","message":"Vai para o log do Apparatus"}
```

- Responda a cada `query` com um `results` de **mesmo `id`** (sem `id`, vale para a única busca em andamento).
  Uma busca que já foi substituída por outra mais nova é descartada: não precisa se preocupar com isso.
- `score` ajusta a ordem (maior primeiro). Resultados do gatilho aparecem acima de tudo o mais.
- `icon` é opcional: `path:C:\arquivo.exe` ou `shell:…` mostram o ícone real do arquivo.
- Tudo que a extensão escrever em **stderr** vai para o log (`%LOCALAPPDATA%\Apparatus\logs`). Linhas que não são
  JSON na saída padrão são ignoradas.

**Ações** (`action` é o `Enter`; `alt` é o `Ctrl+Enter`; se faltar `action`, vale `callback`)

| `type` | Campos | O que acontece |
|---|---|---|
| `open` | `target` | abre site, pasta, arquivo ou programa |
| `copy` | `text` | copia para a área de transferência |
| `insert` | `text` | digita/cola o texto no programa que estava em uso |
| `reveal` | `path` | mostra o arquivo no Explorer |
| `callback` | `data` | devolve `activate` à extensão, com o `data` |
| `none` | — | só fecha o launcher |

## Todas as buscas, ou só o gatilho

Por padrão a extensão só ouve o próprio gatilho. Em Configurações, "Responder a todas as buscas" faz o Apparatus
enviar também o que você digita sem gatilho (a partir de 2 caracteres). Isso manda tudo o que você pesquisa para o
programa: ative só em extensões em que você confia e que precisem disso.

## Robustez

- Se a extensão demorar mais que `timeoutMs`, o resultado dela é simplesmente omitido. Cancelar a busca (digitar mais
  uma letra) não derruba o processo.
- Se o processo cair, ele é reiniciado na próxima busca; depois de **3 quedas em um minuto** o Apparatus desiste até
  você recarregar as extensões (a lista em Configurações mostra o erro e as últimas linhas do stderr).
- `oneshot`: o pedido vai por stdin, que é fechado em seguida; leia a linha, responda e termine. Mais simples, porém
  mais lento a cada busca (um PowerShell leva uns 400 ms para iniciar); prefira `persistent` para busca ao vivo.

## Armadilhas

- **Windows PowerShell 5.1 só lê acentos em scripts `.ps1` salvos como "UTF-8 com BOM".** Sem o BOM, `"Olá"` vira
  `"OlÃ¡"` na saída. O exemplo está nesse formato.
- Para ler e escrever UTF-8 sem BOM em PowerShell, use `StreamReader`/`StreamWriter` sobre
  `[Console]::OpenStandardInput()`/`OpenStandardOutput()` com `AutoFlush = $true`, como no exemplo; `Write-Host` e a
  codificação padrão do console não servem.
- Esvazie o buffer da saída a cada mensagem (`AutoFlush`, `flush()`); senão o Apparatus espera até o `timeoutMs`.
- Em `ConvertTo-Json`, passe os resultados como array (`@($itens)`) para que um único item continue sendo lista.

## Segurança

Uma extensão roda **sem isolamento**, com as suas permissões, e pode fazer o que você faria. O que o Apparatus garante é
só o contorno: nada roda sem ativação explícita, uma extensão com defeito não derruba o launcher, e as respostas dela
são tratadas como dados (uma ação `open` abre o alvo pedido, e é por isso que a ativação exige confiança).

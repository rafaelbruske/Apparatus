# Assistente de IA (BYOK) — avaliação e plano

Status: **proposta, nada implementado.** Escrito em 23/11/2026 a partir da leitura do código (0.7.5). Este documento segue o
formato de `PLANO.md`/`FASEn.md`: decisões, estrutura, fases com critério de saída, riscos.

## 1. Veredito

**Viável, e o encaixe com a arquitetura atual é bom.** Quase tudo o que a ideia pede já existe como peça reutilizável:

| A ideia pede | O que já existe no código |
|---|---|
| Catálogo de "ferramentas" que o app sabe executar | `StepCatalog` (15 tipos de passo, com campos, validação e resumo), `ActionRunner`, `InternalCommands`, `windows-settings.json` (105 páginas `ms-settings:`/ferramentas) |
| Executar só depois do clique do usuário | `ResultExecutor` / `ActionService.Run` já separam "escolher" de "executar"; o launcher já tem lista + Enter |
| Chave guardada cifrada na máquina | `IKeyProtector` / `DpapiKeyProtector` (`Clipboard/ClipboardCipher.cs`), com testes de cifragem prontos para copiar |
| Arquivo de instruções embutido e editável pela comunidade | Padrão de `EmbeddedResource` com `LogicalName` já usado em `windows-settings.json` |
| Nova tela em Configurações | `SettingsWindow` é uma página rolável dividida em seções e em arquivos parciais (`SettingsWindow.Start.cs`, `.Backup.cs`, `.Updates.cs`) |
| Lógica testável sem UI | `Apparatus.Core` é `net10.0` puro, com xUnit (491 testes) |

O que **não** existe e é o trabalho real: (a) cliente HTTP/IA (o app não usa `HttpClient` hoje; só o Velopack acessa a rede),
(b) um modo de conversa na janela do launcher, que hoje é só caixa de busca + lista de 56 px por linha, (c) a ponte
"chamada de ferramenta da IA → cartão clicável → executor existente".

Nenhuma parte exige mudar o modelo de segurança do app. O ponto crítico é de **privacidade** (texto do usuário sai da
máquina), tratado na seção 7.

## 2. Decisões de arquitetura (com recomendação)

### 2.1 Camada de IA: `Microsoft.Extensions.AI` sim, mas atrás de uma interface própria
- Usar `Microsoft.Extensions.AI.Abstractions` (`IChatClient`, `AIFunctionFactory`) + `Microsoft.Extensions.AI.OpenAI` apontando
  o endpoint para cada provedor. Um único formato (Chat Completions) cobre Gemini (endpoint de compatibilidade OpenAI),
  Groq, OpenRouter, Ollama (`/v1`) e OpenAI. O usuário também ganha um provedor "Personalizado" (LM Studio etc.) só trocando a URL.
- **Não** usar `FunctionInvokingChatClient`. Ele executa a função sozinho, que é exatamente o que não queremos. Declarar as
  ferramentas só como declaração (`AIFunctionFactory.CreateDeclaration`) e tratar `FunctionCallContent` como **sugestão**.
- Semantic Kernel: descartar. É pesado (centenas de KB a MB de dependências) para um uso de um passo só.
- **Spike obrigatório antes da Fase 2** (meio dia): confirmar, com chave real de cada provedor, que o SDK da OpenAI lida com
  as particularidades da camada de compatibilidade (chamadas de ferramenta em *streaming* do Gemini e do Ollama costumam ser
  o ponto frágil; ids de chamada ausentes, `index` faltando). Se falhar, o plano B é um cliente próprio de ~300 linhas com
  `HttpClient` + `System.Text.Json` contra `/chat/completions`, que roda sem dependência nova.
- Por isso: o app depende de `IAssistantClient` (nossa interface, em `Core`), não de tipos do SDK. Trocar a implementação
  não toca a UI nem os testes.

### 2.2 Onde guardar a chave: arquivo protegido por DPAPI, não `settings.json`
- "Keychain" no Windows é o DPAPI ou o Gerenciador de Credenciais. Recomendo **reutilizar o DPAPI** (`IKeyProtector`) por
  consistência com a área de transferência, zero dependência nova e testabilidade (fake `IKeyProtector` nos testes).
  Precisa de um ajuste: `DpapiKeyProtector` tem a *entropy* fixa `Apparatus.clipboard.v1`; torná-la parâmetro.
- Arquivo: `%LOCALAPPDATA%\Apparatus\assistant.keys` (pasta *Local*, como `clipboard.key`), conteúdo = JSON
  `{ providerId: chave }` embrulhado pelo DPAPI. Uma chave por provedor, para trocar de provedor sem perder as outras.
- **A chave nunca entra em `AppSettings`**: `settings.json` é exportado no backup `.apparatus` e na pasta de sincronização,
  e ambos são sem criptografia (README, "Segurança e privacidade"). `BackupService.ForExport` não deve ver chave alguma.
- Limite honesto, igual ao do histórico da área de transferência: protege contra outro usuário/disco roubado/backup, não
  contra um programa rodando como você. Dizer isso na tela.
- Alternativa (Gerenciador de Credenciais via CsWin32): a chave fica visível ao usuário no Painel de Controle. Mais
  "padrão do sistema", porém mais código nativo e mais difícil de testar. Fica como opção se você preferir.

### 2.3 Onde mora a conversa: dentro do launcher
- Entrada: novo `ResultKind.Assistant = 12` (acrescentar no fim; o enum é numérico) vindo de um `AssistantProvider`
  (`ISearchProvider`). Aparece quando o assistente está ligado e a consulta começa com `?` ou `ia `, ou como último
  recurso quando nada corresponde, com pontuação baixa, ao lado do "Pesquisar no Google" (que tem −500). Enter entra no modo de conversa.
- Modo conversa: a mesma janela, com a lista de resultados trocada por uma área rolável de mensagens + cartões de ação.
  A caixa de texto continua em foco; Enter envia; `Esc` limpa, depois sai do modo, depois fecha; `↑/↓` com a caixa vazia
  navegam pelos cartões e Enter aciona. O app continua sendo "teclado primeiro".
- A janela do launcher **se esconde ao perder o foco** (`LauncherWindow.OnActivated`). A conversa vive em memória num
  `AssistantSession` guardado em `AppServices`, para sobreviver ao esconder/mostrar, e é apagada ao sair do modo ou
  em N minutos. Não grava em disco.
- Janela própria de chat fica como evolução opcional (Fase 5), se a conversa longa incomodar.

### 2.4 Ferramentas = sugestões tipadas, validadas no C#, executadas por código que já existe
O modelo devolve `nome + argumentos JSON`. O C# **valida e normaliza** (nunca confia) e transforma em `SuggestedAction`
(título, descrição, glifo, nível de risco, delegate de execução). A UI mostra um botão; só o clique executa.

| Ferramenta (nome em inglês, para o modelo) | Executa via | Validação | Risco |
|---|---|---|---|
| `open_windows_setting(uri)` | `ShellLauncher.Open` | `uri` deve existir no catálogo `windows-settings.json` (o mesmo catálogo vai no prompt). URIs desconhecidos `ms-settings:` ficam fora na v1 | baixo |
| `open_apparatus_screen(screen)` | `InternalCommands` / `_openActions` / `_openSnippets` | enum fechado: `settings`, `snippets`, `actions`, `extensions`. **Fora:** `Quit`, `ClearHistory`, `ClipboardClear` | baixo |
| `open_launcher(query)` | passo `OpenLauncher` | texto livre, só preenche a caixa | baixo |
| `open_url(url)` | passo `OpenUrl` | `StepCatalog.Validate` (http/https/mailto) | baixo |
| `run_my_action(name)` | `ActionHost.Run(id, LauncherTarget)` | a ação tem que existir e estar habilitada; o nome vem da lista que o usuário permitiu enviar | médio (o usuário a criou, mas pode fazer qualquer coisa) |
| `propose_action(name, hotkey, steps[])` | abre o editor de ações **pré-preenchido, sem salvar** | cada passo passa por `StepCatalog.Validate` e `ActionStore.Validate`; na v1 só tipos seguros (`OpenApp`, `OpenUrl`, `OpenPath`, `SendKeys`, `Window`, `Volume`, `Media`, `OpenLauncher`, `Wait`). **Sem `RunPowerShell`/`RunCommand`/`TypeText`** | médio |
| `propose_snippet(trigger, text)` | abre o editor de snippets pré-preenchido, sem salvar | gatilho e texto validados pelas regras de `SnippetStore` | baixo |

Observações:
- O `CriarAtalhoAutoHotKey` da ideia vira `propose_action`: o Apparatus tem o próprio modelo de ações/macros, não usa AutoHotkey.
- O `ExecutarComandoCustomizado(id)` vira `run_my_action(name)`. Usar **nome** (o que o modelo enxerga), resolvido para id no C#.
- Falta hoje: `ActionsWindow` e `SnippetsWindow` só têm `StartNewAction()`/`StartNewSnippet()` sem dados. É preciso um
  `StartNewAction(ActionDefinition draft)` e equivalente, que carrega o rascunho no editor como "novo, não salvo".
- Mesmo uma ação aceita no editor só roda quando o usuário aciona o atalho; a IA nunca aperta "Salvar" nem dispara a ação criada.

### 2.5 Ciclo de uma pergunta (uma rodada, sem laço automático)
1. Monta mensagens: `system` (seção 3) + histórico + pergunta.
2. Envia com as declarações de ferramentas; recebe texto em *streaming* + eventuais chamadas de ferramenta.
3. Texto aparece conforme chega. Ao final, cada chamada válida vira um cartão; inválida é descartada e logada (sem a chave).
4. **Histórico válido na próxima pergunta:** a API exige que toda chamada de ferramenta do assistente tenha uma mensagem
   `tool` de resposta. Gravar no histórico `"Sugerido ao usuário; ainda não executado."` como resultado. Sem isso o segundo
   turno dá erro 400 em vários provedores.
5. Modelo sem suporte a ferramentas (comum no Ollama): detectar o erro, repetir sem ferramentas e avisar "este modelo só
   responde com texto". Opcional (v2): modo "JSON no texto" para modelos pequenos.

## 3. O arquivo de instruções (system prompt)

- Arquivo: `src/Apparatus.Core/Resources/assistant-system-prompt.md`, `EmbeddedResource` com `LogicalName`, igual a
  `windows-settings.json`. Em português, como o resto do repositório; a instrução diz para responder no idioma do usuário.
- Sobreposição do usuário: se existir `%APPDATA%\Apparatus\assistant\system_prompt.md`, ele **acrescenta** (ou substitui,
  se começar com uma linha `<!-- replace -->`). Quem for open-source pode melhorar o embutido por pull request.
- Marcadores preenchidos em tempo de execução: `{{app_version}}`, `{{windows_build}}`, `{{settings_catalog}}` (linhas
  `uri — nome` do JSON de configurações, uns 4 mil tokens), `{{user_actions}}` (só se o usuário permitiu, seção 7).
- Conteúdo: o que é o Apparatus e o que ele faz (launcher, snippets, ações, Iniciar próprio); regras de conduta
  (resposta curta, passo a passo só quando pedido, sugerir ferramenta sempre que resolver o pedido, nunca afirmar que
  executou algo, nunca inventar `ms-settings:`; se não está no catálogo, explicar o caminho manual); 4 a 6 exemplos
  pergunta → texto + chamada de ferramenta (a impressora do enunciado é o primeiro).
- Testar o prompt é parte do trabalho: um arquivo `tests/.../assistant-eval.json` com ~20 perguntas e a ferramenta
  esperada, rodado manualmente contra um modelo real (não no CI). Serve de regressão quando alguém edita o prompt.

## 4. Provedores e configuração

- `src/Apparatus.Core/Resources/assistant-providers.json` (embutido; a comunidade atualiza modelos sem mexer em C#):
  `id`, `name`, `baseUrl`, `needsKey`, `defaultModel`, `keyUrl`, `freeTier` (texto), `badge`.
  Os modelos padrão e as cotas gratuitas mudam com frequência: **conferir os nomes atuais na hora de implementar** e
  oferecer "Atualizar lista" (`GET {baseUrl}/models`, que todos os provedores listados expõem) em vez de fixar um modelo.
- **Gemini** e **Ollama** destacados com selo "Grátis". Gemini: botão "Obter chave grátis" abre `https://aistudio.google.com/apikey`.
  Ollama: sem chave, `http://localhost:11434/v1`, e mensagem clara quando não estiver rodando ("Inicie o Ollama").
- Novos campos em `AppSettings` (sem segredo): `AssistantEnabled` (padrão **false**, opt-in como o histórico da área de
  transferência), `AssistantProvider`, `AssistantBaseUrl` (só provedor personalizado), `AssistantModel` (por provedor),
  `AssistantShareActionNames` (padrão false), `AssistantPrefix`. `AssistantEnabled` deve ser tratado como específico da
  máquina em `BackupService.KeepMachineSpecific`, porque a chave não viaja.
- Tela em `SettingsWindow.xaml` + `SettingsWindow.Assistant.cs`, seção "Assistente de IA": interruptor, lista de
  provedores com selos, campo de chave (`PasswordBox`; depois de salva mostra "Chave salva" com Substituir/Remover, nunca
  a chave de volta), URL base, modelo (combo + Atualizar), **Testar conexão** (uma chamada mínima, mostra o erro traduzido),
  interruptor "Enviar os nomes das minhas ações", aviso de privacidade (`InfoBar`).
- Rede: `HttpClient` único, tempo limite de 60 s, cancelável. HTTPS obrigatório, exceto `localhost`/`127.0.0.1`/`[::1]`.
  A chave só vai para a URL base configurada, num cabeçalho `Authorization`. Nada de log de cabeçalhos ou corpos.
  Erros traduzidos: 401 chave inválida, 404 modelo inexistente, 429 limite do plano gratuito, sem conexão, Ollama desligado.

## 5. Estrutura de código

```
src/Apparatus.Core/Assistant/
  IAssistantClient.cs        interface própria (stream de texto + chamadas de ferramenta)
  ChatClientAdapter.cs       implementação sobre Microsoft.Extensions.AI (ou HttpClient, plano B)
  AssistantSession.cs        histórico em memória, regra do "tool result" sintético
  AssistantTools.cs          declarações + validação -> SuggestedAction (record puro, sem UI)
  SystemPromptBuilder.cs     carrega o .md embutido + sobreposição + marcadores
  ProviderCatalog.cs         lê assistant-providers.json
  AssistantKeyStore.cs       JSON de chaves cifrado por IKeyProtector
  AssistantErrors.cs         exceção -> mensagem em português
src/Apparatus.Core/Resources/assistant-system-prompt.md, assistant-providers.json
src/Apparatus.Core/Search/Providers/AssistantProvider.cs      (ISearchProvider, ResultKind.Assistant)
src/Apparatus.App/AssistantService.cs        fila de UI, cancelamento, ponte SuggestedAction -> executores existentes
src/Apparatus.App/LauncherWindow.xaml(.cs)   modo conversa
src/Apparatus.App/SettingsWindow.Assistant.cs
tests/Apparatus.Core.Tests/Assistant*Tests.cs
```
`Apparatus.Core` fica sem tipos do WinUI. O executor das sugestões é em `App`, perto do `ResultExecutor`
(reaproveitando os mesmos callbacks `openSettings/openSnippets/openActions`).

## 6. Fases

| Fase | Entrega | Critério de saída |
|---|---|---|
| **0. Spike** | Console descartável em `spikes/Spike.Assistant`: chat + tool call em streaming contra Gemini, Groq, OpenRouter, Ollama, OpenAI, com `Microsoft.Extensions.AI.OpenAI`. Medir o tamanho que isso soma ao instalador (hoje 80 MB) | Todos devolvem texto + chamada de ferramenta, ou decisão pelo plano B (`HttpClient`) |
| **1. Núcleo** | `IAssistantClient`, `AssistantKeyStore` (+ entropy parametrizada no DPAPI), `ProviderCatalog`, `SystemPromptBuilder`, `AssistantTools` + validadores, `AssistantSession` | Testes unitários verdes com `IChatClient` falso e `IKeyProtector` falso; nenhuma UI |
| **2. Configurações** | Seção "Assistente de IA", salvar/remover chave, listar modelos, Testar conexão, avisos de privacidade | Colar chave do Gemini, testar, reiniciar o app e a chave continuar lá; `settings.json` e o backup sem a chave |
| **3. Conversa no launcher** | `AssistantProvider`, modo conversa, streaming, cancelar com Esc, erros traduzidos, sem ferramentas ainda | Perguntar e receber resposta no Gemini e no Ollama, sem travar a digitação do launcher |
| **4. Ferramentas e cartões** | Cartões, `open_windows_setting`, `open_apparatus_screen`, `open_launcher`, `open_url`, `run_my_action`; depois `propose_action` e `propose_snippet` com rascunho nos editores | "Como altero as configurações de impressoras" mostra o cartão `ms-settings:printers`; nada executa sem clique; entradas inválidas do modelo são descartadas |
| **5. Acabamento (opcional)** | Janela de chat própria, Markdown, histórico opcional, modo "JSON no texto" para modelos pequenos | À parte |

**Decisões sobre a Fase 5 (planejamento, nada implementado):**
- Janela própria: descartada. Meio-termo **decidido (a implementar)**: opção `AssistantKeepOpen` (padrão `false`, em Configurações → Assistente, entra no backup). Ligada, e só enquanto o modo conversa estiver ativo, o launcher não se esconde ao perder o foco (`OnActivated`); fora do modo conversa tudo igual a hoje.
  - Saída: `Esc` sai do modo conversa (e depois fecha), a hotkey alterna, e um botão "×" discreto aparece no modo conversa.
  - Ao voltar pela hotkey o foco cai na caixa de texto. Executar um cartão e abrir o Menu Iniciar continuam escondendo o launcher; a janela fixa não fica por cima do Menu Iniciar.
  - Teste de entrada real (com aviso ao usuário): ligar a opção, entrar no modo conversa, clicar fora, conferir que a janela segue visível e sair pelo `Esc`.
- Markdown: desejado (versão mínima: negrito, listas, `código`, blocos de código).
- Histórico em disco: sem interesse agora; possível no futuro (teria de ser criptografado, como o da área de transferência).
- Modo "JSON no texto" para modelos locais pequenos: ideia para o futuro; o usuário não pretende usar Ollama no curto prazo.

Estimativa grosseira, só para ordem de grandeza: fases 0 a 4 são algo entre uma e duas semanas de trabalho concentrado; a
parte mais incerta é o modo conversa na janela do launcher (altura dinâmica, foco, esconder ao perder foco).

## 7. Segurança e privacidade (precisa constar na UI e no README)

1. **Opt-in e desligado por padrão.** Ao ligar, aviso: "o que você digitar aqui é enviado ao provedor escolhido".
2. **O que sai da máquina:** a pergunta, o histórico da conversa, o prompt do sistema, o catálogo de configurações e,
   *só se o usuário permitir*, os nomes das ações. **Nunca:** histórico da área de transferência, snippets, arquivos
   indexados, caminhos, nome de usuário. O `AssistantTools` não tem ferramenta que leia esses dados.
3. **Saída do modelo é dado não confiável.** Toda ferramenta é validada no C# (listas fechadas, `StepCatalog.Validate`);
   a IA nunca monta caminho nem comando livre. Sem execução automática, sem `RunPowerShell`/`RunCommand` na v1.
4. **Injeção de prompt:** na v1 nenhuma fonte externa entra no contexto (nem área de transferência, nem páginas), então a
   única entrada é o texto do próprio usuário. Qualquer recurso futuro do tipo "explique o que copiei" reabre essa
   superfície e precisa de revisão própria.
5. **Plano gratuito:** os termos de alguns provedores permitem usar o conteúdo do nível gratuito para melhorar produtos
   (verificar os termos atuais do Gemini antes de escrever o aviso). Mostrar o aviso ao lado do selo "Grátis".
6. **Segredo:** chave só em `assistant.keys` (DPAPI), nunca em log, backup ou sincronização; `AppLog` não registra
   requisições. Ollama local não envia nada para fora.
7. Atualizar a seção "Segurança e privacidade" do `README.md` e a `PENDENCIAS.md` quando entregar.

## 8. Testes

- **Unitários (`Apparatus.Core.Tests`):** validação de cada ferramenta (URI fora do catálogo, `javascript:`, passo proibido,
  nome de ação inexistente); `SystemPromptBuilder` (marcadores, sobreposição); `AssistantKeyStore` com `IKeyProtector`
  falso (ida e volta, arquivo adulterado, outro usuário); `ProviderCatalog` lê o JSON embutido; `AssistantSession`
  (resultado sintético de ferramenta, limite de tamanho do histórico); traduções de erro; garantia de que `AppSettings`
  serializado e `BackupService.Export` não contêm a chave.
- **Integração opcional:** teste que roda só se existir `APPARATUS_TEST_GEMINI_KEY` no ambiente; fora disso é ignorado.
- **Manual (lista em `FASE8.md`):** foco e Esc no modo conversa, esconder/mostrar com conversa em andamento, cartões
  por teclado, Ollama desligado, chave errada, sem internet, DPI alto. O repositório já registra que cliques reais nunca
  foram automatizados para as telas de configuração; esta seção seguirá o mesmo padrão.

## 9. Riscos

| Risco | Efeito | Mitigação |
|---|---|---|
| Chamada de ferramenta em *streaming* quebra no SDK com Gemini/Ollama | Cartões não aparecem em algum provedor | Spike da Fase 0; plano B com `HttpClient`; interface própria |
| Modelos pequenos do Ollama não suportam ferramentas | Só texto | Detectar, avisar, modo "JSON no texto" (v2) |
| Modelo alucina `ms-settings:` | Cartão que não abre nada | Allowlist = catálogo; o catálogo vai no prompt; URI fora dele é descartado |
| Janela do launcher se esconde ao perder o foco | Perde a conversa ao alternar | `AssistantSession` em memória; Fase 5 pode ter janela própria |
| Plano gratuito muda cotas/modelos | Padrões quebram | `assistant-providers.json` editável, lista de modelos pela API, mensagem clara no 429 |
| Tamanho do instalador | +alguns MB | Medir no spike; o plano B não adiciona dependências |
| Privacidade mal compreendida | Dado sensível enviado | Opt-in, aviso, nada de fontes externas, nomes de ações só com permissão |
| Interface 100% em português fixo no código | Cadeias novas seguem o padrão | Manter o padrão atual; localização continua fora de escopo (ver `PENDENCIAS.md` §3) |

## 10. Decisões que cabem a você

1. **Camada de IA:** `Microsoft.Extensions.AI` com plano B (recomendado) ou direto `HttpClient` desde o início.
2. **Chave:** DPAPI reaproveitado (recomendado) ou Gerenciador de Credenciais do Windows.
3. **Conversa:** dentro do launcher (recomendado) ou janela própria já na primeira versão.
4. **`propose_action` com `RunPowerShell`/`RunCommand`:** fora da v1 (recomendado) ou liberado com aviso vermelho e script exibido por inteiro.
5. **Nomes das ações no prompt:** opt-in (recomendado) ou sempre enviados.
6. **Idioma do prompt:** português (recomendado, por consistência) ou inglês, que costuma render um pouco melhor em modelos pequenos.

## 11. Estado da implementação

### Decisões tomadas
- Camada de IA: `Microsoft.Extensions.AI.OpenAI` atrás de `IAssistantClient`, dentro do `Apparatus.Core` (spike em [FASE0-ASSISTENTE.md](FASE0-ASSISTENTE.md)).
- Chave: DPAPI reaproveitado (`DpapiKeyProtector` agora recebe um rótulo de finalidade; o da área de transferência não mudou).
- Idioma do prompt embutido: **inglês** (`assistant-system-prompt.md`). O modelo responde no idioma do usuário.
- Conversa dentro do launcher e `propose_action` sem `RunPowerShell`/`RunCommand`: mantidos como no plano; ainda não houve decisão em contrário.

### Fase 1 (núcleo): feita, sem UI
`src/Apparatus.Core/Assistant/`: `IAssistantClient`, `ChatClientAdapter`, `AssistantEndpoint` (HTTPS obrigatório, exceto máquina local),
`AssistantErrors`, `AssistantKeyStore`, `ProviderCatalog` (+ `assistant-providers.json`), `SettingsCatalog`, `SystemPromptBuilder`
(+ `assistant-system-prompt.md`), `AssistantTools` (7 ferramentas com validadores), `AssistantSession`, `AssistantEngine`.
161 testes novos em `tests/Apparatus.Core.Tests/Assistant*Tests.cs`; a suíte inteira passa (652).

Regras que ficaram no código, além do plano:
- Uma resposta aceita no máximo 3 sugestões; as demais são descartadas.
- Passos de `propose_action` aceitam só `OpenApp`, `OpenUrl`, `OpenPath`, `SendKeys`, `Window`, `Volume`, `Media`, `OpenLauncher`, `Wait`.
  `OpenApp` recusa programas que executam comandos (`cmd`, `powershell`, `wscript`, `rundll32`...) e extensões de script/instalador;
  campos booleanos como `admin` nunca vêm do modelo. Atalho inválido ou já usado é descartado em vez de recusar a ação inteira.
- `propose_snippet` com gatilho em conflito vira snippet sem gatilho (o cartão avisa).
- Modelo que recusa o campo `tools` (HTTP 400) é repetido uma vez sem ferramentas, com aviso, e o app lembra disso.
- Pergunta sem resposta (erro, cancelamento, resposta vazia) sai do histórico, para a nova tentativa não duplicar.

### Ainda não verificado
- O esquema de `propose_action` (array de objetos com campos de texto opcionais) **não foi testado contra Gemini nem Groq reais**; o do spike tinha só campos simples. Estender o spike com um caso `propose_action` antes da Fase 4.
- `ChatClientAdapter.Create` (SDK + HttpClient reais) só foi exercitado pelo spike; os testes usam um `IChatClient` falso.
- Tamanho no instalador compactado.
- Nada de `AppSettings`/tela de configurações ainda (Fase 2): quando existir, incluir testes de que `settings.json` e o backup não contêm chave.

### Fase 2 (configurações): feita
- `AppSettings`: `AssistantEnabled` (desligado), `AssistantProvider`, `AssistantBaseUrl` (só provedor personalizado), `AssistantModels` (modelo por provedor), `AssistantShareActionNames`. Nenhum campo guarda segredo (um teste por reflexão barra nomes como `ApiKey`/`Token`).
- Backup: `AssistantEnabled` e `AssistantBaseUrl` não viajam (exportados vazios, e a importação mantém os do computador). Provedor, modelo e a preferência de enviar nomes viajam. A chave não entra em lugar nenhum além de `assistant.keys` (pasta Local, DPAPI).
- Core: `AssistantSetup` (resolve configurações + catálogo + chave, `WhatIsMissing` diz o que falta, `ModelChoices` filtra a lista de modelos e põe os `flash-lite` do Gemini na frente).
- Tela: cartão "Assistente de IA" em `SettingsWindow.Assistant.cs` (provedor com selo "Grátis", chave com Salvar/Remover e sem exibir de volta, modelo, Atualizar lista, Testar conexão, aviso de privacidade, opção de enviar nomes das ações, pasta das instruções extras).
- Correção achada no teste real: o Gemini responde 400 (não 401) para chave inválida; agora vira "chave recusada" em vez de "modelo não aceita o pedido".
- Verificação feita abrindo o app de verdade (`--settings`, perfil isolado em `APPARATUS_PROFILE_DIR`): salvar a chave, reiniciar e ela continuar lá; `assistant.keys` sem a chave em texto puro; `settings.json` sem a chave; trocar de provedor; digitar um modelo e ele ser gravado; "Testar conexão" com uma chave falsa devolveu o erro traduzido do Google.
- Não verificado: "Atualizar lista" e "Testar conexão" com uma **chave válida** (só a chave falsa foi usada), e o modo de alto DPI. Há um teste de integração opcional (`APPARATUS_TEST_GEMINI_KEY`) para quem tiver a chave.

### Fase 3 (conversa no launcher): feita, só texto
- **Entrada (decisão do usuário, no lugar do prefixo `?` do plano):** com o assistente ligado, a caixa de pesquisa mostra "Pesquisar… ou pressione Tab para perguntar à IA". **Tab** com a caixa vazia (ou quando nada correspondeu à busca) abre o modo IA; com resultados na tela, Tab continua percorrendo a lista. Não há `AssistantProvider` nem `ResultKind.Assistant`: a entrada é a tecla, não um resultado de busca.
- **Memória (decisão do usuário):** `AssistantKeepConversation` (padrão ligado) escolhe entre guardar a conversa por 15 minutos na memória ou sempre abrir do zero. Nada vai para o disco.
- **Teclas no modo IA:** Enter envia · Esc cancela a resposta, depois limpa a caixa, depois sai do modo, depois fecha o launcher · Tab (caixa vazia) volta à busca · Ctrl+N conversa nova · Ctrl+Shift+C copia a última resposta · Ctrl+, abre as configurações · setas e PageUp/PageDown rolam a conversa. Pergunta cancelada ou com erro volta para a caixa.
- **Só texto:** o motor é criado com `offerTools: false` e o prompt ganha uma nota dizendo que não há botões. Os cartões de ação são a Fase 4.
- Core: `AssistantChat` (linhas da conversa, ask/cancelar/reiniciar/expirar, erro vira linha na tela), `ChatEntry`, `AskOutcome`. App: `LauncherWindow.Assistant.cs`, `ChatTemplateSelector`.
- Verificado no app real (perfil isolado + servidor falso compatível com OpenAI fazendo o papel do Ollama): dica na caixa, Tab, pergunta e resposta em streaming (o texto cresce palavra a palavra), cancelar com Esc, erro 429 traduzido, Ctrl+N, sequência de Esc, conversa guardada ao reabrir pelo atalho global, "sempre do zero", assistente sem chave (tela explica o que falta) e assistente desligado (Tab segue como antes).
- Não verificado: resposta de um provedor real dentro do launcher (só o servidor falso), perguntas longas com rolagem, alto DPI.

### Fase 4 (botões de ação nas respostas): feita
- **Ferramentas ligadas:** o motor deixou de ser criado com `offerTools: false`; o prompt volta a descrever as ferramentas. O modelo só **sugere**: cada chamada válida vira um cartão (`ChatEntryRole.Suggestion`) logo abaixo da resposta e **nada roda antes do usuário escolher**.
- **Cartões:** título + detalhe (o que abre ou executa; ação proposta mostra os passos e o atalho). O cartão selecionado tem borda de destaque e `↵`. Resposta só com botões (sem texto) é aceita; chamada descartada sozinha (ex.: ação com PowerShell) vira o aviso "o assistente sugeriu algo que o Apparatus não permite" e a pergunta volta para a caixa.
- **Teclas:** com a caixa **vazia**, Enter executa o cartão selecionado; ↑/↓ trocam de cartão (com 2 ou mais; com um só, ↑/↓ continuam rolando a conversa); clique também executa. Com texto na caixa, Enter envia a pergunta, como antes. O rodapé mostra "↵ Executar" quando é isso que o Enter faz.
- **Perguntas seguintes:** os botões da resposta anterior saem da tela (Enter sempre se refere aos mais novos).
- **Execução (`ResultExecutor.RunSuggestion`)** usa só caminhos que já existiam: `ShellLauncher.Open` (configuração do Windows e URL), as janelas de configurações/snippets/ações, `ActionHost.Run` (ação do usuário, no aplicativo que tinha o foco) e, para o launcher com texto, o próprio launcher sai do modo IA e pesquisa o texto.
- **Rascunhos:** `StartNewAction(draft)` e `StartNewSnippet(draft)` abrem o editor já preenchido, marcado como não salvo; nada é gravado até o usuário clicar em Salvar. O atalho sugerido só vem preenchido se estiver livre; o gatilho de snippet em conflito é removido (o cartão avisa).
- **Core:** `AssistantChat` ganhou `Suggestions`, `SelectedSuggestion`, `MoveSuggestion`, `SelectSuggestion`; `ChatEntry` ganhou `Suggestion`, `Detail`, `IsSelected`. Chamadas rejeitadas só vão para o registro (`AppLog`).
- **Testes:** 701 passando (10 novos de botões: criação, seleção, rotação, texto vazio, rejeição, remoção na pergunta seguinte, cancelamento, ferramentas declaradas).
- **Verificado no app real** (perfil isolado + servidor falso com chamadas de ferramenta): três cartões numa resposta, ↑/↓ e Enter abrindo os snippets, rascunho de snippet e de ação abrindo preenchidos e sem salvar, chamada com PowerShell descartada com aviso, resposta só com botão, cartão "launcher com texto" voltando à pesquisa com "calc".
- **Não verificado:** `propose_action`/`propose_snippet` contra **Gemini e Groq reais** (o esquema com array de objetos ainda só passou pelo servidor falso); clique do mouse no cartão (só teclado); alto DPI; execução real de `run_my_action` e de abrir configuração do Windows pelo cartão (só o caminho de código, que é o dos resultados de busca).

### Perguntas de referência: executor com provedor real (opt-in)
- `AssistantEvalTests.Integration_RealProvider_AnswersTheReferenceQuestions_WhenAKeyIsInTheEnvironment` faz as 21 perguntas de `assistant-eval.json` a um provedor de verdade, pelo mesmo motor do launcher (`AssistantEngine`), e confere se alguma das sugestões é a esperada (ferramenta, e também a URI ou a tela quando o caso define). Pergunta sem ferramenta esperada (`none`) só passa se **nenhum** botão vier. Cada pergunta usa uma sessão nova; nada é executado, o motor só sugere.
- **Só roda com chave no ambiente**; sem ela o teste retorna na hora. Variáveis: `APPARATUS_TEST_EVAL_PROVIDER` (id do provedor; padrão `gemini`) e `APPARATUS_TEST_<ID>_KEY` com a chave (`APPARATUS_TEST_GEMINI_KEY`, `APPARATUS_TEST_GROQ_KEY`...), ou `APPARATUS_TEST_EVAL_KEY` para uma chave qualquer. **Para o Groq é preciso definir o provedor**: só a chave dele não troca o provedor; `APPARATUS_TEST_EVAL_MODEL` escolhe o modelo; `APPARATUS_TEST_EVAL_MIN` muda o mínimo de acertos (padrão 0,7).
- Pausa de 2 s entre perguntas e novas tentativas com espera crescente em 429/5xx, por causa dos limites do plano grátis. A chave nunca entra no relatório.
- O relatório (acertos, o esperado × obtido de cada falha, chamadas que o Apparatus descartou, avisos) vai para a saída do teste e para `assistant-eval-report.txt` ao lado da DLL de testes (em `bin/`, não versionado).
- Exemplo (PowerShell): `$env:APPARATUS_TEST_GEMINI_KEY = '<sua chave>'; dotnet test tests/Apparatus.Core.Tests --no-restore --filter "FullyQualifiedName~Integration_RealProvider" --logger "console;verbosity=detailed"`.
- Mecânica verificada também contra o servidor falso (4 de 21, o que ele sabe responder).
- **Primeiro resultado real (Gemini `gemini-3.5-flash-lite`, 2026-11-24): 20 de 21 (95%), 0 erros de conexão/limite**, em 2 min 10 s. Inclui `propose_action` e `propose_snippet`: o esquema com array de objetos passou no Gemini real, e a pergunta sem ferramenta ("capital da França") não gerou botão. A única falha, "desinstalar um programa", foi para `appwiz.cpl` em vez de `ms-settings:appsfeatures`; as duas respondem bem, então o caso passou a aceitar `appwiz.cpl` como alternativa (`"alt"` no JSON). Groq ainda não foi testado. O teste para na primeira recusa de chave/modelo/endereço em vez de repetir o erro nas 21 perguntas.
- **Segunda rodada com o Gemini (mesmo modelo, depois de aceitar `appwiz.cpl`): 20 de 21 de novo, mas a falha mudou** — "onde crio uma ação nova?" veio só com texto ("abrindo a tela de ações…") e **sem botão**, embora na primeira rodada tenha acertado. O modelo às vezes descreve o caminho em vez de chamar a ferramenta; o resultado oscila de uma rodada para outra. (Essa rodada foi tomada como Groq por engano: a variável do Groq não era lida e a do Gemini continuava definida.)
- **Groq real (`openai/gpt-oss-120b`, 2026-11-24): 19 de 21 (90%), 0 erros de conexão/limite**, em 3 min 38 s. `propose_action`, `propose_snippet` e a pergunta sem ferramenta passaram também no Groq. Falhas: (1) "abrir o gerenciador de dispositivos" → `open_launcher("devmgmt.msc")` em vez de `open_windows_setting` (o botão leva ao launcher, que provavelmente acha a página, mas é um passo a mais); (2) "editar variáveis de ambiente" → só texto, mandando o usuário digitar o comando do `rundll32` no Win+R, **sem botão**, embora a página esteja no catálogo.
- **Padrão das 3 falhas reais (1 no Gemini, 2 no Groq): o modelo descreve o caminho em vez de chamar a ferramenta, ou escolhe `open_launcher` quando o catálogo já tem a página.** Nenhuma foi uma chamada errada ou perigosa: os botões inválidos já são barrados pelo Apparatus. Ajuste de prompt possível (ainda não feito): regra explícita "se o catálogo tem a página que responde, **chame** `open_windows_setting`; não só descreva; use `open_launcher` só quando nada do catálogo servir".
- **Ajuste de prompt (2026-11-24):** a regra 2 de `assistant-system-prompt.md` agora diz que, se o catálogo tem a página, o modelo **deve chamar** `open_windows_setting` (mesmo quando a entrada parece um comando, como `devmgmt.msc`), que `open_launcher` só serve quando nada do catálogo cabe, e que o mesmo vale para `open_apparatus_screen`. Ainda sem nova rodada real para medir o efeito: comparar com 20/21 (Gemini) e 19/21 (Groq).
- **Depois do ajuste de prompt, Groq (`openai/gpt-oss-120b`), duas rodadas seguidas (2026-11-24): 21 de 21 e 21 de 21**, sem erros de conexão/limite. As duas falhas anteriores (`devmgmt.msc` via `open_launcher`; variáveis de ambiente sem botão) sumiram. Atenção: a primeira dessas rodadas foi planejada como Gemini, mas `APPARATUS_TEST_EVAL_PROVIDER=groq` continuava definida na sessão, então **o Gemini ainda não foi medido com o prompt novo** (último resultado dele: 20/21, falha em "onde crio uma ação nova?").
- **Gemini (`gemini-3.5-flash-lite`) com o prompt novo (2026-11-24): 19 de 21 (90%), 0 erros de conexão/limite.** "onde crio uma ação nova?" (a falha da rodada anterior) passou. Falharam dois casos que antes passavam: "ativar modo escuro" → `ms-settings:personalization-background` em vez de `ms-settings:colors` (página vizinha, errada), e "abra o site do github" → `propose_action` ("Abrir GitHub") em vez de `open_url`. Somando as 3 rodadas do Gemini (20, 20, 19), o caso que falha muda a cada vez: é oscilação do modelo pequeno, não regressão clara do prompt. Mesmo assim, o `propose_action` no lugar de `open_url` é pior que a resposta direta (obriga o usuário a salvar uma ação); vale ficar de olho.

### Alternativa moderna ao lado da ferramenta clássica (2026-11-24)
- Quando o modelo sugere uma ferramenta **clássica** que tem página equivalente no Windows 11, o app mostra logo depois dela um segundo cartão com a **página moderna** ("Alternativa moderna · Configurações do Windows"). Exemplo: `appwiz.cpl` (Programas e Recursos) → `ms-settings:appsfeatures` (Aplicativos instalados).
- O vínculo é dado do catálogo, não do modelo: campo opcional `"m"` em `windows-settings.json` (hoje 4 pares: `appwiz.cpl`, `mmsys.cpl`, `timedate.cpl`, `powercfg.cpl`). `SettingsCatalog` o lê (`SettingsPage.ModernUri`), `AssistantTools.ModernAlternative` monta o cartão e `AssistantEngine` o emite após o cartão clássico.
- **Não muda o prompt** nem o que o modelo vê: as linhas do catálogo no prompt continuam "uri — nome". O cartão extra **não entra no histórico** (continua uma chamada, um reconhecimento), **não conta** no limite de 3 sugestões e **não duplica** se o modelo já sugeriu a página moderna (em qualquer ordem).
- Sem equivalente moderno (Gerenciador de Dispositivos e demais `.msc`, variáveis de ambiente, Opções da Internet, rede `ncpa.cpl`, disco) nada é acrescentado. `sysdm.cpl` ficou de fora de propósito: "Sobre" só cobre parte e seria ruído para quem quer variáveis de ambiente. Para `ncpa.cpl` e disco falta a página moderna no catálogo; incluí-la aumentaria o prompt.
- Testes: 710 passando (6 novos: pares válidos, prompt inalterado, só ferramentas clássicas com par, ordem e duplicação, limite, histórico válido). Não verificado na tela do launcher (o cartão usa o mesmo caminho dos outros).

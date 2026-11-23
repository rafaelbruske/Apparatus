# Fase 0 do assistente de IA: resultado do spike

Projeto do spike: `spikes/Spike.Assistant`. Plano geral: [ASSISTENTE-IA.md](ASSISTENTE-IA.md).

Pergunta da fase: o `Microsoft.Extensions.AI.OpenAI` 11.0.1, apontado para um endereço próprio, funciona com os provedores reais
(streaming, chamada de ferramenta, histórico de dois turnos), ou é melhor escrever um cliente HTTP próprio?

## Resultado (execução real, com a chave do próprio usuário)

| Teste | Gemini `gemini-3.5-flash-lite` | Groq `openai/gpt-oss-120b` |
|---|---|---|
| `GET /models` | ok (65 modelos) | ok (11 modelos) |
| Streaming + ferramenta: impressora → `ms-settings:printers` | ok (0,8 s) | ok (0,6 s) |
| Streaming + ferramenta: brilho → `ms-settings:display` | ok (10,6 s) | ok (0,5 s) |
| Streaming sem ferramenta (só texto) | ok (33,3 s) | ok (0,6 s) |
| Sem streaming + ferramenta | **falhou: timeout de 90 s, sem resposta** | ok (0,9 s) |
| 2º turno (resultado sintético da ferramenta) | ok, mas respondeu com outra chamada e texto vazio | ok, mas devolveu o URI como texto (bloco JSON) |
| Sem ferramentas declaradas | ok | ok |

O que isso prova:

- O SDK funciona com endereço, chave e modelo trocados. Os argumentos da ferramenta, que chegam em pedaços no streaming, foram
  remontados corretamente nos dois provedores (`uri` válido, dentro do catálogo, nos casos de teste).
- O histórico com resultado sintético de ferramenta (`tool_call_id` pareado) foi aceito pelos dois.
- Os identificadores de chamada têm formatos diferentes (`call_...` no Gemini, `fc_...` no Groq). O código não pode assumir formato.

## Problemas observados (todos do lado do provedor, nenhum do SDK)

1. **Gemini gratuito é lento e instável.** `gemini-3.8-flash` respondeu 503 ("high demand"). No `3.5-flash-lite` a latência variou
   de 0,8 s a 33 s entre chamadas iguais, e uma chamada sem streaming ficou 90 s sem resposta. É uma amostra pequena, mas indica que o app precisa de:
   timeout configurável, botão de cancelar, mensagem clara para 429/503 e nenhuma tentativa automática repetida.
2. **O 2º turno é pouco confiável.** Depois do resultado "sugerido, ainda não executado", o Gemini chamou outra ferramenta e o Groq devolveu o
   URI como texto. Conclusão de projeto: a chamada de ferramenta encerra a resposta. O app mostra o texto e os botões e não pede um
   segundo turno. O resultado sintético só entra no histórico para o usuário continuar a conversa depois.
3. **Cota gratuita varia por modelo.** `gemini-omni-1.2-flash` tem `limit: 0` no plano gratuito. A tela de configurações deve listar os
   modelos (`/models`), ter um botão "Testar conexão" e mostrar o corpo do erro do provedor.
4. **Nomes de modelo mudam.** Nenhum nome fixo no código; o padrão fica no JSON de provedores, e o usuário pode trocar.

## Decisão

Continuar com `Microsoft.Extensions.AI.OpenAI` atrás da interface própria `IAssistantClient`. O plano B (HttpClient próprio) fica
descartado por enquanto: o custo dele é manter um parser de SSE e de chamadas fragmentadas, e o SDK já fez isso corretamente nos dois provedores.

Custo: cerca de 7 MB de DLLs sem compressão (`OpenAI.dll` tem 5,5 MB). O efeito no instalador compactado ainda não foi medido; medir na Fase 1.

## Não testado

- Ollama (não instalado), OpenRouter e OpenAI.
- Repetição do caso "sem streaming" no Gemini, para saber se o timeout foi pontual.
- Qualidade do português e adesão ao catálogo em perguntas fora dos dois casos testados (fica para a Fase 2, com um conjunto de perguntas).

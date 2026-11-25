# Fase 8: roteiro de verificação manual do assistente

O que o código e os testes automáticos cobrem está em `docs/ASSISTENTE-IA.md` (§11). Este roteiro é o que só se confirma com a mão no app, de preferência com uma chave real (Groq ou Gemini) e depois com o Ollama.

Como usar: Configurações → Assistente → ligar, escolher o provedor, colar a chave, **Atualizar lista**, **Testar conexão**. No launcher, `Tab` entra no modo conversa.

## Conexão
- [ ] Atualizar lista e Testar conexão com chave válida: lista de modelos aparece, teste dá "ok".
- [ ] Chave errada: mensagem clara, sem mostrar a chave.
- [ ] Sem internet: mensagem de rede, o launcher continua responsivo.
- [ ] Ollama desligado: mensagem dizendo que o servidor local não responde.
- [ ] Desligar o assistente: `Tab` volta ao comportamento normal.

## Launcher (teclado)
- [ ] `Tab` entra e sai do modo conversa; `Esc` sai do modo, depois fecha o launcher.
- [ ] O foco fica no campo ao chegar a resposta; setas/Enter percorrem os cartões.
- [ ] Esconder (hotkey) e reabrir no meio da conversa: mantém por 15 min com "Manter conversa"; começa do zero sem ela.
- [ ] Resposta longa não estoura a janela.

## Cartões e ferramentas
- [x] Rodar as 21 perguntas de `assistant-eval.json` contra o provedor real (feito em 24/11/2026: Groq 21/21 duas vezes, Gemini 19/21; resultados em `docs/ASSISTENTE-IA.md`). Para repetir, não precisa ser à mão: o teste `Integration_RealProvider_AnswersTheReferenceQuestions_WhenAKeyIsInTheEnvironment` faz isso e grava `assistant-eval-report.txt` (veja "Perguntas de referência" em `docs/ASSISTENTE-IA.md`).
- [ ] Clique do mouse num cartão executa o mesmo que Enter.
- [ ] `open_windows_setting`: abre a página certa.
- [ ] "Desinstalar um programa" (ou som, data e hora, energia): aparece o cartão clássico e, logo depois, "Alternativa moderna" com a página do Windows 11.
- [ ] `propose_action` / `propose_snippet`: abre o editor preenchido, nada é salvo sem você confirmar.
- [ ] `run_my_action` (com "compartilhar nomes das ações" ligado): cartão de uma ação real executa.
- [ ] Pergunta sem ferramenta ("capital da França"): só texto.
- [ ] Pedir algo perigoso ("apague meus arquivos", "rode powershell"): nenhum cartão executável.

## Manter a janela aberta (Configurações → Assistente)
- [ ] Opção desligada: clicar fora no modo IA fecha o launcher.
- [ ] Opção ligada: clicar fora no modo IA mantém a janela; fora do modo IA ela ainda fecha.
- [ ] Janela mantida e sem foco: o atalho devolve o foco à caixa de texto (não esconde).
- [ ] O botão × e o `Esc` (sai do modo, depois fecha) fecham a janela.
- [ ] Executar um cartão ou abrir o Menu Iniciar esconde o launcher.
- Nota: o script `tests/Apparatus.UI/ai-keep-open.ps1` não deu resultado válido em 23/11/2026 (ver PENDENCIAS).

## Aparência
- [ ] DPI alto (150% / 200%) e tema escuro/claro: texto e cartões legíveis.

## Instalador
- [x] `build\release.ps1`: tamanho medido em 25/11/2026 (versão 0.8.0): `ApparatusApp-win-Setup.exe` 81,9 MB (85,9 milhões de bytes), pacote completo 74,7 MB. O valor anterior é só "~80 MB" nas notas (sem número exato), então o custo das DLLs do assistente (~7 MB sem compressão) ficou em torno de 2 MB comprimidos. Gerado e **não instalado**: instalar é com duplo clique no Explorador, fora do aplicativo Claude.
- [ ] Instalar o `Setup.exe` (duplo clique) e conferir: abre, Alt+Espaço funciona, o assistente aparece em Configurações e a chave salva continua depois de reiniciar.

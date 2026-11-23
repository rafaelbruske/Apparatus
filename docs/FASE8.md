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
- [ ] Rodar `assistant-eval.json` à mão (20 perguntas): anotar quantas viram o cartão esperado.
- [ ] Clique do mouse num cartão executa o mesmo que Enter.
- [ ] `open_windows_setting`: abre a página certa.
- [ ] `propose_action` / `propose_snippet`: abre o editor preenchido, nada é salvo sem você confirmar.
- [ ] `run_my_action` (com "compartilhar nomes das ações" ligado): cartão de uma ação real executa.
- [ ] Pergunta sem ferramenta ("capital da França"): só texto.
- [ ] Pedir algo perigoso ("apague meus arquivos", "rode powershell"): nenhum cartão executável.

## Aparência
- [ ] DPI alto (150% / 200%) e tema escuro/claro: texto e cartões legíveis.

## Instalador
- [ ] `build\release.ps1`: comparar o tamanho do instalador com o anterior (~80 MB) para medir o efeito das DLLs do assistente (~7 MB sem compressão).

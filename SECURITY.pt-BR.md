# Política de segurança

[English](SECURITY.md) · **Português**

## Como relatar uma vulnerabilidade

Por favor, **não abra uma issue pública** com os detalhes de um problema de segurança.

1. Use o relatório privado do GitHub: **Security → Report a vulnerability** na
   [página do repositório](https://github.com/rafaelbruske/Apparatus/security/advisories/new).
2. Se essa página não estiver disponível, fale com o mantenedor em particular pelo perfil dele no GitHub e peça um jeito
   privado de enviar os detalhes.

Inclua, se puder:

- a versão do Apparatus (Configurações → Sobre) e a do Windows (`winver`);
- os passos para reproduzir o problema e o que você acha que um atacante poderia fazer com ele;
- uma prova de conceito, se tiver.

**Não anexe chaves de API reais, histórico da área de transferência nem outros dados pessoais.** Use valores falsos.

Pode escrever em português ou em inglês.

## O que esperar

O Apparatus é um projeto pessoal, mantido por uma pessoa, então não há prazo de resposta garantido. Vou tentar responder em
alguns dias, dizer se considero uma vulnerabilidade e corrigir numa nova versão. Peço que me dê tempo de lançar a correção
antes de divulgar os detalhes; terei prazer em citar você nas notas da versão, se quiser.

## Versões com suporte

O projeto está na versão 0.9.x. Só a **última versão** recebe correções; se puder, atualize antes de relatar.

## O que conta como vulnerabilidade

O Apparatus mantém algumas promessas (veja [CONTRIBUTING.pt-BR.md](CONTRIBUTING.pt-BR.md)). Quebrar uma delas é um problema
de segurança, por exemplo:

- a chave da IA sair do armazenamento criptografado com DPAPI, ou aparecer nas configurações, nos backups, nas pastas de
  sincronização ou nos logs;
- algo proposto pelo assistente de IA ser executado sem o usuário clicar, ou uma ação ou macro executar um comando fora das
  listas fechadas que o código valida;
- dados saírem do PC sem o usuário ter ligado o recurso;
- o app injetar código no Explorer ou em outros programas, ou deixar uma opção do Windows alterada depois que o recurso que
  a exigia foi desligado;
- o instalador ou a verificação de atualização poderem ser induzidos a executar código que o usuário não escolheu.

## Limites conhecidos (não são vulnerabilidades)

São decisões documentadas, listadas em [`docs/PENDENCIAS.md`](docs/PENDENCIAS.md) (seção 5):

- As extensões rodam com as permissões do usuário, sem isolamento; a proteção é o usuário ligar cada uma.
- O backup `.apparatus` e a pasta de sincronização **não são criptografados**.
- O histórico da área de transferência é protegido contra outras contas e discos roubados, não contra um programa rodando
  como o mesmo usuário.
- A gravação de macros não detecta campos de senha de navegadores.
- O instalador não é assinado, então o SmartScreen avisa na primeira execução.

Um relato sobre um desses pontos continua sendo bem-vindo como ideia de melhoria, mas por uma issue comum.

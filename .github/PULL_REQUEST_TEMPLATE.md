<!-- English or Portuguese is fine. / Pode escrever em português ou inglês. -->

## What changed and why / O que mudou e por quê

<!-- A few sentences. One fix or feature per pull request. / Poucas frases. Uma correção ou recurso por pull request. -->

## Related issue / Issue relacionada

<!-- Closes #123 — or "none" for a small fix. / ou "nenhuma" numa correção pequena. -->

## How I tested it / Como testei

- [ ] `dotnet test tests/Apparatus.Core.Tests` is green / passa
- [ ] Tried it in the running app (what I did: ...) / Experimentei no app rodando (o que fiz: ...)

**Not tested / Não testei:** <!-- appearance, real keyboard, high DPI, light theme, installed version... / aparência, teclado real, DPI alto, tema claro, versão instalada... -->

## Checklist

- [ ] New logic lives in `Apparatus.Core` with unit tests; `Native` and `App` stay thin. / Lógica nova no `Apparatus.Core`, com testes; `Native` e `App` continuam finos.
- [ ] Nothing is injected into other programs, and nothing leaves the PC unless the user turns it on. / Nada é injetado em outros programas e nada sai do PC sem o usuário ligar.
- [ ] New features that take a system key, record what the user types or copies, or send data out are **off by default**. / Recursos que tomam uma tecla do sistema, gravam o que o usuário digita ou copia, ou enviam dados para fora vêm **desligados**.
- [ ] Docs updated if behavior changed: `README.md` **and** `README.pt-BR.md`, the note in `docs/`, `docs/PENDENCIAS.md` for what was left out. / Documentação atualizada se o comportamento mudou: `README.md` **e** `README.pt-BR.md`, a nota em `docs/`, `docs/PENDENCIAS.md` para o que ficou de fora.
- [ ] New or updated package: license checked, `THIRD-PARTY-NOTICES.md` and `third-party-licenses.json` updated. / Pacote novo ou atualizado: licença conferida, `THIRD-PARTY-NOTICES.md` e `third-party-licenses.json` atualizados.
- [ ] No secrets, `bin/`, `obj/`, `artifacts/` or test screenshots in the commit. / Sem segredos, `bin/`, `obj/`, `artifacts/` nem capturas de teste no commit.

## Screenshots / Capturas de tela

<!-- For changes you can see. / Para mudanças visíveis. Remove this section if it does not apply. -->

# Assinatura de código (Windows)

Resumo: **assinar não é obrigatório para o Apparatus funcionar**, mas sem assinatura o Windows avisa ("O Windows protegeu o seu
computador" / SmartScreen) na primeira execução do instalador, e algumas funções (como o UIAccess) só existem com um executável
assinado. **Um certificado confiável custa dinheiro**; não existe autoridade pública gratuita. Há caminhos mais baratos e gratuitos
em casos específicos, descritos abaixo. Valores e regras mudam; as fontes ao final foram consultadas em novembro de 2026.

## Como funciona
1. Uma **autoridade certificadora (CA)** confirma quem você é (pessoa ou empresa) e emite um **certificado de assinatura de código**.
2. O certificado tem uma **chave privada**. Desde 2023 a chave precisa ficar em hardware (token USB, HSM ou um serviço na nuvem);
   a CA não entrega mais um arquivo `.pfx` solto para certificados públicos.
3. Na compilação, a ferramenta de assinatura (`signtool`, ou o serviço) calcula o *hash* do `.exe` e do instalador, cifra com a chave
   privada e **embute a assinatura** no arquivo, junto com o certificado e um **carimbo de data** (timestamp), que faz a assinatura
   continuar válida depois que o certificado expira.
4. No computador do usuário, o Windows confere que o arquivo não foi alterado desde a assinatura e que o certificado leva a uma CA em
   que ele confia. O nome do **editor** passa a aparecer no lugar de "Editor desconhecido".

## O que a assinatura dá, e o que não dá
- Dá: nome do editor verificado; detecção de adulteração; base para o **SmartScreen** construir reputação; requisito para UIAccess
  e para alguns antivírus pararem de desconfiar.
- **Não dá reputação instantânea.** Desde março de 2024 o certificado "EV" (mais caro) deixou de pular o SmartScreen: arquivos
  assinados, OV ou EV, precisam de um histórico de downloads (em geral semanas) até o aviso sumir. Pagar o EV só por isso não
  compensa.
- Certificado **autoassinado** (que você mesmo cria) só serve para desenvolvimento: o SmartScreen bloqueia com mais força, e para o
  computador confiar nele é preciso instalá-lo no repositório de raízes confiáveis (ação de administrador, que afeta a segurança do PC).

## Custos e caminhos (novembro de 2026)
| Caminho | Custo aproximado | Observações |
|---|---|---|
| **Azure Artifact Signing** (antigo Trusted Signing) | cerca de US$ 10/mês (plano Basic) | A mais barata e a recomendada pela Microsoft para apps fora da loja. Exige assinatura paga do Azure e validação de identidade. **Para pessoa física, só nos EUA e Canadá**; empresas nos EUA, Canadá, UE e Reino Unido. Vale conferir se o Brasil entrou. |
| **Certificado OV** de uma CA (Sectigo, DigiCert, SSL.com e outras) | de US$ 150 a 500 por ano, conforme a CA e o prazo | Validade máxima de cerca de 15 meses desde 2026. Normalmente exige empresa ou identidade validada e entrega em token/nuvem. |
| **Certificado EV** | a partir de US$ 400 por ano | Só compensa para drivers de modo kernel. Para aplicativos comuns, o mesmo efeito de reputação do OV. |
| **SignPath Foundation** | gratuito | Para projetos de **código aberto** qualificados: assinam por uma esteira gerenciada. Exige o projeto público e uma licença aberta. |
| **Microsoft Store** (pacote MSIX) | taxa única pequena de conta de desenvolvedor | A Store assina o pacote. Muda a forma de distribuição (MSIX, sandbox), o que conflitaria com ganchos globais e o acesso que o Apparatus precisa. Não recomendado aqui. |
| Sem assinatura | grátis | O SmartScreen avisa; o usuário escolhe "Mais informações → Executar assim mesmo". Funciona, é o estado atual. |

## O que fazer no Apparatus quando houver um certificado
O script `build\release.ps1` já aceita `-SignParams` e repassa ao Velopack (`vpk --signParams`), que assina todos os arquivos
executáveis do pacote e o `Setup.exe`. Para um serviço de nuvem, `--signTemplate` (comando de assinatura personalizado). Exemplos:
```powershell
# certificado em token/arquivo (signtool precisa estar instalado, Windows SDK)
.\build\release.ps1 -Version 0.8.0 -SignParams '/fd sha256 /tr http://timestamp.digicert.com /td sha256 /a'
```
Para o Azure Artifact Signing, a esteira usa `signtool` com o *dlib* do serviço; a documentação da Microsoft descreve os parâmetros.

Depois de assinar: conferir com `signtool verify /pa /v <arquivo>` e, nos primeiros downloads, esperar o SmartScreen aprender.

## Recomendação para este projeto
Enquanto o Apparatus for de uso pessoal, **não assine**: aceite o aviso do SmartScreen uma vez. Se for distribuí-lo a outras pessoas
ou precisar do UIAccess (`PENDENCIAS.md`, item 1.1), o caminho mais barato é o **Azure Artifact Signing** (se a sua conta e o seu país
forem elegíveis) ou um **OV** de uma CA. Se o código se tornar público e aberto, peça o **SignPath Foundation**.

## Fontes
- Microsoft, [Code signing options for Windows app developers](https://learn.microsoft.com/windows/apps/package-and-deploy/code-signing-options)
- Microsoft, [Azure Artifact Signing](https://azure.microsoft.com/products/artifact-signing) e o [FAQ](https://learn.microsoft.com/en-us/azure/artifact-signing/faq)
- Preços de OV/EV: páginas de [SSL.com](https://ssl.com/products/software-integrity/code-signing/ov/) e [Sectigo](https://www.sectigo.com/code-signing) (os valores variam entre as fontes)

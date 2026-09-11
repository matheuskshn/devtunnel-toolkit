# Revisão de segurança

Data: 2026-09-09. **Publicação bloqueada.**

Atualização de 2026-09-11: a [candidata Ubuntu com bibliotecas recompiladas](images/hub/UBUNTU_CVE_REMEDIATION.md)
incorpora correções para 32 dos 46 CVEs remanescentes, incluindo os oito
prioritários. O scan bruto permanece em 60 ocorrências; correções, componentes
ausentes, mitigações condicionais e pendências estão separados na matriz.
Uma revisão automatizada adicional corrigiu o stripping e os SONAMEs dos módulos
PKCS#11. A nova candidata passou nos cinco pacotes Lintian, na comparação de
símbolos/SONAME de dez bibliotecas e novamente nos testes AMD64. Não houve
promoção. A tentativa ARM64 encontrou um bloqueio do LeakSanitizer sob QEMU;
a aceitação exige executor ARM64 nativo. O workflow seletivo/manual está preparado,
mas ainda não foi enviado ao GitHub. Logins reais GitHub/Microsoft sobreviveram
a reinício e recriação com volume local, com consulta autenticada de leitura.
A revisão por outro agente identificou duas lacunas na receita/aceitação, corrigidas
e conferidas no código. Isso não comprova renovação após 24 horas ou 30 dias.

Atualização de 2026-09-10: a [avaliação separada com Ubuntu 26.04 LTS](images/hub/UBUNTU_EVALUATION.md)
passou nos smokes AMD64 e no filtro HIGH/CRITICAL do Trivy. A imagem padrão não
foi substituída; persistem alertas reclassificados e a aceitação ARM64 está
pendente. A [triagem subsequente dos CVEs Ubuntu](images/hub/UBUNTU_CVE_TRIAGE.md)
registrou 60 ocorrências restantes e oito dos 11 CVEs prioritários ainda abertos.
Os números abaixo continuam sendo os do baseline Debian analisado.

As quatro frentes de remediação local foram implementadas e verificadas:
configuração de segurança, cobertura/baseline do Sonar, refatoração e redução
das dependências das imagens. Isso não representa ausência de vulnerabilidades:
permanecem CVEs sem correção disponível na distribuição analisada, a necessidade
de root na inicialização do OpenVPN e achados de manutenção no Sonar.

Não foram criadas supressões, lista de CVEs ignoradas, VEX ou alterações manuais
de status para aprovar verificações. Não foi usado `--ignore-unfixed`.

## Resultados e ferramentas

| Verificação | Versão / escopo | Resultado final |
| --- | --- | --- |
| Testes automatizados | Hub e automação | 178 aprovados: 147 do Hub e 31 da automação; nenhum ignorado |
| Integração do Hub | Container, console web e PostgreSQL | Três smokes aprovados com a imagem final |
| Integração legada | OpenVPN, Squid e proxy de roteamento | Tráfego real entre serviços sintéticos locais aprovado |
| npm audit / outdated | Dependências de execução e desenvolvimento | Zero vulnerabilidades conhecidas; outdated sem entradas |
| Gitleaks | 8.30.1, binário oficial verificado | Zero achados no histórico de 36 commits e na árvore de trabalho |
| Semgrep OSS | 1.176.1 fixado por digest, 98 regras aplicáveis | Zero achados em 102 arquivos; políticas p/security-audit e p/javascript |
| Trivy | 0.74.0, binário oficial verificado | As cinco imagens reprovam HIGH/CRITICAL; configuração mantém um HIGH |
| c8 | 12.0.0, instrumentação V8 e LCOV | 79,39% das linhas, 86,25% dos ramos e 84,44% das funções |
| SonarQube | Community Build 26.9.0.129388; Scanner 8.0.1.6346 | 40 achados: 39 de manutenção, uma vulnerabilidade e zero bugs; gate ERROR |

As compilações, varreduras e testes de execução locais abrangem **linux/amd64**.
A matriz de CI contempla também ARM64, mas não houve aceitação local de execução
ARM64 nem execução dos novos workflows remotos. Não houve commit, push,
publicação de imagem ou alteração no ACA nesta etapa.

## 1. Configuração de segurança

### Kubernetes

O [exemplo Kubernetes](images/hub/examples/kubernetes/hub.yaml) agora:

- Desativa a montagem automática do token da ServiceAccount com
  `automountServiceAccountToken: false`; o Hub não usa a API Kubernetes.
- Define requests/limits de armazenamento efêmero em 128Mi/512Mi.
- Limita os volumes em memória de runtime e temporários a 256Mi/128Mi.
- Mantém seccomp RuntimeDefault, usuário não root, raiz somente leitura,
  capabilities removidas e proibição de elevação de privilégios.

Os valores são exemplos que precisam ser dimensionados para cada ambiente.
Os limites de volumes em memória não substituem o limite de armazenamento
efêmero de logs e demais fontes contabilizadas pelo kubelet.
Referências: [ServiceAccount](https://kubernetes.io/docs/tasks/configure-pod-container/configure-service-account/#opt-out-of-api-credential-automounting)
e [recursos dos containers](https://kubernetes.io/docs/concepts/configuration/manage-resources-containers/).

### Automação e PATH

[trusted-executable.mjs](.github/scripts/trusted-executable.mjs) resolve Git,
Docker e Skopeo apenas em diretórios de sistema predefinidos. Verifica o caminho
real, permissão de execução e propriedade root, sem escrita por grupo/outros,
tanto do executável quanto dos diretórios ancestrais. Não consulta o PATH herdado.

Um teste coloca um Git falso em um PATH gravável e confirma que ele não é
executado. Essa defesa não protege contra um administrador root que altere os
próprios binários de sistema. Shims graváveis pelo usuário não são suportados
pela automação de release.

### OpenVPN

O [Compose](compose.yml) usa uma bridge privada, publica a porta em loopback por
padrão e não utiliza `privileged: true` nem rede do host. Na inicialização,
remove todas as capabilities e concede somente NET_ADMIN, SETUID e SETGID,
com no-new-privileges. O forwarding é configurado pelo runtime no namespace
do container; o entrypoint verifica o valor e não escreve sysctls do host.

Os testes comprovaram TCP e UDP, portas personalizadas, publicação loopback,
acesso a HTTP sintético somente pela VPN, manutenção da PKI e reconexão após
restart. Os daemons ficaram com UID/GID 65534, mantendo somente CAP_NET_ADMIN
para operações de interface do [OpenVPN/SITNL](https://raw.githubusercontent.com/OpenVPN/openvpn/v2.6.14/src/openvpn/platform.c).

**Root ainda é necessário na inicialização.** Permanecem Trivy DS-0002 HIGH e
Sonar docker:S6471 MINOR, referentes à mesma condição, não a dois riscos
independentes. O serviço não é totalmente rootless. Definir USER nobody sem
redesenhar a inicialização impediria a configuração de TUN/NAT.

A migração de host networking para bridge pode exigir adaptação de serviços
presos ao loopback do host e de rotas específicas daquele namespace. Não se
afirma equivalência de conectividade para qualquer ambiente.

## 2. Cobertura real e baseline do Sonar

### Instrumentação

`npm run test:coverage`, em images/hub, compila TypeScript com source maps,
executa testes do Hub e da automação e gera coverage/lcov.info. A configuração
inclui arquivos não executados e remapeia JavaScript para TypeScript, sem
contabilizar ambos como fontes independentes.

O runner recusa JavaScript órfão em dist sem apagar artefatos locais. Um teste
com TypeScript e c8 reais verifica inclusão de fonte não executada com 0% e
ausência de duplicação após remapeamento. As VMs do frontend executam app.js e
theme.js integrais, sem truncar ou transformar a fonte, com o nome real do arquivo.

CLI e worker continuam com 0% nessa instrumentação. Smokes Docker/PostgreSQL e
testes de navegador são evidências funcionais separadas, não cobertura importada
artificialmente. O build da imagem não publica relatórios ou source maps de teste.

| Medida | Resultado |
| --- | ---: |
| c8 - linhas | 79,39% |
| c8 - ramos | 86,25% |
| c8 - funções | 84,44% |
| Sonar - linhas, arredondado | 79,4% |
| Sonar - ramos, arredondado | 86,3% |
| Sonar - cobertura combinada de linhas e condições | 80,8% |

Os percentuais combinados do Sonar não são a mesma medida que cobertura de
linhas. Não houve aviso de caminhos LCOV não resolvidos.
Referências: [c8](https://github.com/bcoe/c8) e
[cobertura JavaScript/TypeScript](https://docs.sonarsource.com/sonarqube-server/10.3/analyzing-source-code/test-coverage/javascript-typescript-test-coverage).

### Execução reproduzível

A análise final usou um projeto local novo e apenas duas análises nessa ordem:

1. Código do commit `b410ddc2b8d05caeb3c51d6da23db853ddd45c5d`, versão 0.1.0-rc.4,
   com seu contexto Git correspondente, montados somente para leitura.
2. Cópia do trabalho atual, incluindo alterações não commitadas e LCOV real,
   identificada por hash de conteúdo e versão de trabalho, usando a análise
   anterior como período SPECIFIC_ANALYSIS.

Não foram reutilizados resultados intermediários como histórico de releases.
A configuração de escopo é a mesma nas duas análises. Foram preservados hashes
da fonte selecionada, configuração, lockfile e LCOV nas evidências locais.

O scanner indexou 102 arquivos; as métricas de produção contabilizaram 33
arquivos e 7.538 linhas de código. Os caminhos relevantes de .github foram
incluídos explicitamente. Dependências, dist, cobertura, assets e dados de
runtime não são código próprio. Smokes são classificados como testes.

Servidor e scanner ficaram em rede Docker interna, com telemetria desabilitada
e projeto privado. Nenhum código foi enviado ao SonarQube Cloud ou a uma
instância corporativa. Imagens utilizadas:

- `sonarqube@sha256:aa7146fd72ef79ea8ca06315ca3121db9bb1c24a83ba5255e8df67b43dee6e04`.
- `sonarsource/sonar-scanner-cli@sha256:23ca0f137965d9dff2198074043fd48d386280bc5d0ccac8c8349cea4cf096a9`.

Os analisadores incluem JavaScript/TypeScript 13.8.0.44569, IaC 2.16.0.22905,
HTML 3.31 e Text/Secrets 2.49.0.12346, com perfis Sonar way. A análise de
dependências do Sonar foi pulada; npm audit e Trivy cobrem essa camada.
O TypeScript interno 6.0.3 do analisador não substitui o compilador 7.0.2 do projeto.

Há avisos de blame ausente nos arquivos modificados/não versionados. Por isso,
as métricas de código novo são provisórias para esta cópia de trabalho e precisam
ser confirmadas no commit/PR definitivo. Não representam defeitos necessariamente
introduzidos apenas nesta remediação: o trabalho já continha funcionalidades não
commitadas em relação ao baseline.

### Resultados e gate

| Comparação | Total | Manutenção | Vulnerabilidades | Bugs |
| --- | ---: | ---: | ---: | ---: |
| Revisão anterior do trabalho, antes da remediação | 261 | 255 | 4 | 2 |
| Baseline do commit selecionado no projeto novo | 81 | 74 | 5 | 2 |
| Trabalho final após remediação | 40 | 39 | 1 | 0 |

O primeiro comparativo mede a evolução da revisão do trabalho. O segundo é o
baseline Git usado para o período de código novo. Não são a mesma referência.

Os 40 achados finais se dividem em 21 MAJOR e 19 MINOR. Não há achados CRITICAL,
bugs ou Security Hotspots nos perfis utilizados. A classificação de segurança
é B; confiabilidade e manutenção são A. Essas notas não anulam achados individuais.

| Condição do gate | Resultado | Limite |
| --- | ---: | ---: |
| Cobertura no código considerado novo | 85,5%, aprovado | Pelo menos 80% |
| Duplicação no código considerado novo | 0,0%, aprovado | No máximo 3% |
| Achados considerados novos | 28, reprovado | Zero |

O gate permanece **ERROR**, sem aceite manual.
Os conceitos de [Quality Gate](https://docs.sonarsource.com/sonarqube-community-build/quality-standards-administration/managing-quality-gates/introduction-to-quality-gates)
e [Security Hotspots](https://docs.sonarsource.com/sonarqube-community-build/user-guide/security-hotspots)
não equivalem a uma lista de CVEs.

## 3. Refatoração e validação funcional

- Backend separado em roteamento público, conta e aplicação, com handlers
  menores. Host/Origin, autenticação, CSRF, troca obrigatória de senha e RBAC
  mantêm sua ordem. POST revalida a sessão após receber o body; GET autenticado
  não ganhou uma espera intermediária antes de produzir sua projeção.
- Parser SOCKS incremental separado da lógica de sockets. Preservados códigos
  de erro, limites, TCP CONNECT, encaminhamento pelo Squid, política de
  destinos/portas e isolamento por listener.
- Configuração e restauração de estado decompostas sem relaxar validação.
- Frontend separado em renderizadores, formulários e etapas de atualização.
  Helpers agora se chamam escapeHtml e statusBadge. Escaping HTML não foi
  substituído por codificação de URL.
- Plataformas de release são validadas como exatamente as duas strings
  linux/amd64 e linux/arm64, sem coerção ou ordenação implícita.
- Três regex de saída CLI/D-Bus com possível custo superlinear foram trocadas
  por parsing linear. Os testes incluem banners, whitespace Unicode e entrada
  adversarial de 1MiB; 10.000 casos diferenciais curtos mantiveram os resultados
  anteriores. O alcance era saída de subprocessos limitada a cerca de 1MiB,
  não uma entrada HTTP direta; timers não interrompem processamento síncrono.
- Condicionais ambíguas receberam chaves, retornos finais redundantes foram
  removidos e funções de automação foram decompostas preservando a verificação
  de identidade, imutabilidade de tags, digests e ordem de promoção.

Não restam achados S3776, S1874, S2871 ou S8786 na análise final. Os antigos avisos
S1874 referiam-se a helpers locais com nomes iguais a globais obsoletos, não a
79 bibliotecas vulneráveis. A refatoração removeu a ambiguidade sem suprimir a regra.

Permanecem 39 achados de manutenção, como readonly, expressões aninhadas,
preferências de linguagem e organização de Dockerfiles. Seis S8431 sobre tag
e digest foram mantidos: a combinação documenta o canal de atualização usado
pelo Dependabot e fixa a imagem imutável. Não foi enfraquecida a pinagem para
reduzir a contagem. O bootstrap do frontend mantém tratamento por .catch;
a preferência estilística por top-level await não foi suprimida.

### Evidências de execução

Os três smokes do Hub validaram duas sessões SDK com Squid real, HTTP/SOCKS,
DNS, negações, listeners isolados, keyrings/D-Bus independentes, restauração após
restart, CLI, execução não root/raiz somente leitura e encerramento.

A suíte PostgreSQL comprovou estado criptografado, restauração em diretório novo,
exclusão mútua entre containers, checkpoints interrompidos, rotação de chave,
remoção, SIGKILL/substituição e bloqueio preventivo quando a posse ou conexão
com o banco é perdida. A suíte web validou autenticação local, recuperação por
CLI, configuração criptografada e persistência após reinício.

Os 22 testes de frontend e a validação com Playwright exercitaram login local,
CSP e carregamento como módulo, SSE, foco/seleção preservados, rascunhos em modal,
salvamento/read-back de porta SOCKS, segredo não retornado ao formulário, logs,
scroll, pausa, reconexão após offline e temas. A viewport mobile de 390x844 foi
verificada. Os dados dos serviços de teste eram sintéticos; autenticações externas
reais e relay Microsoft real não foram exercitados nesta etapa.

## 4. Dependências das imagens

As imagens usam Debian 13 fixado por digest e atualização dos pacotes da
distribuição. O Hub utiliza Node 24.21.0 LTS; Node 26 Current não foi adotado.
O suporte de execução local a Node 22 permanece, e a ferramenta de cobertura
exige Node 22.12+.
[Calendário do Node](https://nodejs.org/en/about/previous-releases).

As dependências diretas foram atualizadas nas linhas suportadas: Dev Tunnels
SDK 1.3.56, SSH 3.12.42, ldapts 9.0.0, openid-client 6.8.8, pg 8.23.0,
TypeScript 7.0.2 e tipos do Node 22.20.2. O override restrito de uuid para 11.1.1
preserva a API CommonJS v4 utilizada pelo SDK e corrige o alerta conhecido.
[CVE-2026-41907](https://github.com/advisories/GHSA-w5hq-g745-h8pq).

O CLI Dev Tunnels 1.0.2030 exige checksum e versão verificados; os metadados
.deps.json permanecem disponíveis para scanners. As Actions estão fixadas por
SHAs completos e o Dependabot cobre npm, workflows e Dockerfiles.

Reduções aplicadas:

- Toolkit remove curl e nove dependências sem uso após o download verificado.
- OpenVPN remove procps e dependências dispensáveis.
- Hub seleciona dbus-x11 e systemd-standalone-sysusers como provedores oficiais
  menores, preservando D-Bus e keyrings privados.
- Squid também seleciona o provedor sysusers menor, evitando a pilha completa
  de inicialização sem necessidade.

Não foram removidos à força Perl/GCR/GTK ou pacotes necessários ao Squid e ao
armazenamento de credenciais. O inventário apt foi preservado, com smokes dos
serviços antes de aceitar as alternativas. Não foram misturadas bibliotecas
do Debian instável com a base estável.

### Achados remanescentes

A base Trivy utilizada foi atualizada em 2026-09-09T19:10:00Z. As imagens foram
identificadas como Debian 13.6. Todos os resultados abaixo foram repetidos por
referência imutável, com todas as severidades registradas.

| Imagem | Ocorrências antes | Ocorrências finais | IDs distintos finais | CRITICAL | HIGH | Com correção disponível na distribuição |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Hub | 388 | 367 | 178 | 14 | 81 | 0 |
| Toolkit | 356 | 294 | 166 | 5 | 63 | 0 |
| Squid | 270 | 263 | 106 | 12 | 68 | 0 |
| OpenVPN | 203 | 201 | 102 | 3 | 51 | 0 |
| Proxy de roteamento | 176 | 176 | 92 | 3 | 50 | 0 |

São ocorrências de pares pacote/alerta, não vulnerabilidades exploráveis distintas.
A soma caiu de 1.393 para 1.301, mas repete CVEs entre imagens. O Hub anterior à
migração do Debian 12 tinha 504 ocorrências; esse é outro ponto de comparação,
não o início da última redução de pacotes.

As cinco verificações HIGH/CRITICAL retornaram código 1. A varredura de
configuração mantém oito achados, sendo um HIGH, DS-0002 do OpenVPN.
A imagem final do Hub foi testada e varrida pelo ID local imutável
`sha256:19275f82aa59327f36be6351766e41328771738a8e81d5b8152aad916ab86fb8`.

As 367 ocorrências do Hub são de pacotes do sistema operacional. O scanner não
reportou alertas npm/.NET incorporados nessa imagem, mas isso não comprova ausência
de vulnerabilidades no binário autocontido do CLI. Metadados .deps.json não
substituem auditoria binária ou SBOM completo dos componentes incorporados.

### Triagem dos alertas críticos

As 14 ocorrências críticas do Hub correspondem a cinco IDs. As diferenças entre
severidade do scanner, classificação do fornecedor e alcance na aplicação não
autorizam alterar silenciosamente a política de publicação.

| Alerta / pacote | Situação e limite da mitigação |
| --- | --- |
| [CVE-2026-58016 / GLib](https://security-tracker.debian.org/tracker/CVE-2026-58016) | Pacote estável afetado; Debian classifica como menor/sem DSA. XML D-Bus malformado é o vetor. Barramentos privados reduzem exposição, mas não comprovam ausência de caminhos não confiáveis em toda a cadeia nativa. |
| [CVE-2026-13221 / Perl](https://security-tracker.debian.org/tracker/CVE-2026-13221) | Menor/sem DSA. Exige regex Perl muito grande controlada pelo atacante. APIs do Hub não executam Perl arbitrário; o pacote instalado continua sem patch. |
| [CVE-2026-42496 / Perl Archive::Tar](https://security-tracker.debian.org/tracker/CVE-2026-42496) | Correção adiada enquanto regressões upstream são tratadas. A persistência do Hub não extrai tar em Perl; isso é análise de alcance, não correção do pacote. |
| [CVE-2026-8376 / Perl](https://security-tracker.debian.org/tracker/CVE-2026-8376) | Específico de compilações 32 bits; as arquiteturas previstas são amd64/arm64. Candidato a avaliação VEX por arquitetura, não exceção automática nem validação ARM64 realizada. |
| [CVE-2026-6653 / libxml2](https://security-tracker.debian.org/tracker/CVE-2026-6653) | Pacote estável afetado; menor/sem DSA. Não há endpoint de upload XML no Hub, mas não foi demonstrado que toda biblioteca nativa transitiva esteja fora do alcance de entradas maliciosas. |

O [CVE-2026-32748 do Squid](https://security-tracker.debian.org/tracker/CVE-2026-32748)
exige listener ICP não zero. ICP foi desativado porque não é necessário ao
HTTP CONNECT/SOCKS. Isso é mitigação, não patch. A correção upstream não estava
disponível no pacote Debian estável analisado.

As correções anteriores de umask privada, validação de nomes de clientes,
recusa de PKI incompleta, bloqueio de injeção na configuração Squid e tags
AES-GCM de 16 bytes permanecem. Nenhuma delas remove os CVEs de pacotes acima.

## Reprodução e critérios restantes

Os comandos estão em [SECURITY.md](SECURITY.md) e
[.github/README.md](.github/README.md). Para cobertura, executar em images/hub:

```sh
npm ci --ignore-scripts
npm run test:coverage
npm run check:public
npm audit --audit-level=low
```

[sonar-project.properties](sonar-project.properties) define o escopo e caminho
LCOV. Fornecer SONAR_HOST_URL e SONAR_TOKEN fora do repositório; informar versão
e revisão correspondentes à fonte realmente analisada. A configuração não
conecta automaticamente o projeto a um Sonar remoto.

Antes de publicar:

1. Resolver os achados HIGH/CRITICAL ou obter uma decisão de risco explícita,
   individual e baseada em evidências. Nenhum aceite é feito por este relatório.
2. Redesenhar a inicialização OpenVPN se a política proibir root no startup.
3. Tratar os achados de manutenção restantes e confirmar baseline, blame e gate
   no commit/PR definitivo.
4. Validar ARM64 e executar os workflows remotos. CI verde não substitui prova
   de execução e conectividade no ambiente de destino.
5. Varrer os digests exatos dos candidatos a release antes da promoção.
   Reconstruções posteriores não são automaticamente iguais à imagem local varrida.

As evidências brutas e detalhes específicos de execução permanecem fora da árvore
pública. Os laboratórios descartáveis foram encerrados. Testes aprovados e
scanners sem achados em uma camada não anulam os bloqueios encontrados em outra.

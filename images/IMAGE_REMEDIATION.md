# Remediação das imagens

Validação local AMD64 de 2026-09-11. A implementação e a integração local foram
verificadas; a aceitação do novo conjunto no CI nativo e a publicação ainda não
foram executadas. Não interpretar aprovação do filtro HIGH/CRITICAL como ausência de
vulnerabilidades.

## Alterações

- As cinco imagens passam a usar Ubuntu 26.04 LTS fixado por digest.
  O Hub padrão incorpora a receita Ubuntu validada; `Dockerfile.ubuntu` permanece
  como entrada de compatibilidade idêntica, protegida por teste.
- O Toolkit reutiliza a receita de Expat, GLib e p11-kit da candidata do Hub,
  com fontes verificadas, regressões, pacotes registrados e fontes de licença
  incluídas. Um teste impede divergência entre as duas receitas nativas.
- Todas selecionam GNU coreutils pelo gerenciador de pacotes, após validar o
  plano exato de substituição. O supervisor Pebble não utilizado é removido
  somente após verificar que não pertence a um pacote.
- Os contextos das imagens legadas são a raiz do repositório. O `.dockerignore`
  permite apenas entradas de build explícitas. A seleção de imagens acompanha
  os consumidores dos scripts compartilhados.
- O instalador de Trivy/Gitleaks e a matriz de segurança suportam runners nativos
  AMD64/ARM64, com versões e checksums fixados. Não há bypass do LeakSanitizer.
- Docker, Hub e releases usam um workflow compartilhado com runners nativos,
  publicação por digest e união dos manifests. As duas arquiteturas, os labels,
  o SBOM e a proveniência são obrigatórios antes da promoção de tags. Os digests
  exatos são varridos antes da promoção; candidatos reutilizados também passam
  por uma nova varredura antes da finalização da release.

Um simples upgrade no Debian estável não fecha todos os achados. Exemplos
documentados pelo mantenedor são [util-linux](https://security-tracker.debian.org/tracker/CVE-2026-76642),
[GLib](https://security-tracker.debian.org/tracker/CVE-2026-58016) e
[Perl](https://security-tracker.debian.org/tracker/CVE-2026-13221).
Trocar a distribuição também altera a fonte de severidade do scanner. Por isso,
os números abaixo não demonstram, isoladamente, correção de cada CVE anterior.
As correções próprias de bibliotecas têm evidência separada na
[matriz de remediação do Hub](hub/UBUNTU_CVE_REMEDIATION.md).

## Resultados do Trivy 0.74.0

As colunas MEDIUM e LOW contam ocorrências por pacote. Não foram usados VEX,
supressões nem `--ignore-unfixed`.

| Imagem local | HIGH/CRITICAL | CVEs distintos | Ocorrências | MEDIUM | LOW |
| --- | ---: | ---: | ---: | ---: | ---: |
| Toolkit | 0 | 45 | 58 | 44 | 14 |
| Squid | 0 | 7 | 13 | 6 | 7 |
| Tinyproxy | 0 | 5 | 9 | 5 | 4 |
| OpenVPN | 0 | 7 | 11 | 5 | 6 |
| Hub padrão Ubuntu | 0 | 46 | 60 | 44 | 16 |

Identificadores locais das imagens examinadas, não referências publicadas:

- Toolkit: `sha256:a9962fd1f04772d24564db7aa27941ee87ac7604f92a3f773002890936af544f`.
- Squid: `sha256:6ebb7c4c66e7178bab7275c07aea957863781f3a9bdb055faa2790b3bc32061b`.
- Tinyproxy: `sha256:365fe4fc6f518918ffcc018a1287824df59407572294aefba6a9e78400a51de6`.
- OpenVPN: `sha256:e1e60c3bf2a29ced445ab76a87474be30e1b5171856ed67f620565d484dbfccf`.
- Hub padrão: `sha256:f92c243ab04678eae14a72627ac43ec3f041329dcaafeb9828ed6d386e3d60b2`.

## Validação funcional

- Toolkit: CLI, D-Bus e Secret Service em execução offline, UID/GID 1000, raiz
  somente leitura e nenhuma capability. Probes de Expat/GLib contra as bibliotecas
  instaladas também aprovados. Não houve novo teste de login real nessa imagem.
- Squid/Tinyproxy: tráfego HTTP real entre serviços sintéticos, upstream seletivo,
  rota direta, portas personalizadas, auditoria e processos não root.
- OpenVPN: TLS cliente/servidor em TCP e UDP, TUN/NAT no namespace privado,
  backend acessível somente pela VPN, PKI preservada e reconexão após restart.
  O servidor e o cliente sintético executam como UID/GID 10001 e nenhuma
  capability. O processo que mantém o namespace também descarta UID 0 e todas
  as capabilities depois da preparação. Inicialização idempotente, rejeição de
  execução root/sem no-new-privileges e migração de um volume sintético verificadas.
  O Compose real subiu saudável e preservou a PKI ao recriar os dois serviços.
  Nesse teste, o mount opcional do certificado do host foi retirado por um
  override temporário, pois o daemon local não enxergava o caminho padrão.
- Hub padrão: HTTP/SOCKS5, console, arquivos e PostgreSQL, incluindo recuperação
  após SIGKILL, persistência, isolamento, perda de ownership e falha fechada.
  Probes contra Expat, GLib e libelf instalados também aprovados.
- 196 testes automatizados aprovados, incluindo contratos de versão, seleção,
  políticas de publicação, identidade dos manifests e falhas de registry.
  Sintaxe do Compose e Actionlint aprovados. Semgrep 1.176.1: zero achados em
  119 arquivos com 107 regras; uma execução explícita também cobriu os novos
  scripts de publicação sem depender da seleção de arquivos do Git.
- `npm audit`: zero vulnerabilidades conhecidas. Gitleaks 8.30.1: zero achados
  na árvore de trabalho. O verificador de conteúdo público e a revisão de
  marcadores internos dos 37 arquivos alterados também passaram.

Os builds legados agora usam `docker build -f images/<imagem>/Dockerfile .`.
O Toolkit usa `docker build -f Dockerfile .`; o Hub usa
`docker build -f images/hub/Dockerfile images/hub`.
Os arquivos Compose já apontam para os contextos correspondentes.

## Alertas de Dockerfile e pendências

DS-0017 foi corrigido: `apt-get update` e a instalação aparecem no mesmo `RUN`,
entre `validate-plan` e `verify-installed`. O helper não instala pacotes
implicitamente e recusa chamadas sem fase válida. O Trivy não reporta mais esse
alerta.

DS-0002 foi corrigido no Dockerfile do OpenVPN: o usuário padrão é 10001:10001.
O Compose separa a preparação de TUN/NAT em `openvpn-network`, que começa como
root com quatro capabilities e sem montar a PKI, depois mantém o namespace como
usuário não root e sem capabilities. Isso não elimina a fase privilegiada de
preparação nem torna o deployment compatível com Docker rootless.
Não foram adicionados setuid, file capabilities ou bypass de no-new-privileges.

O novo contrato exige backup e migração explícita de volumes antigos para
UID/GID 10001. O servidor reinicia independentemente, mas a substituição ou
recuperação do namespace exige recriar os dois serviços juntos. Reinícios
automáticos do Docker não propagam a operação aos dependentes do Compose.
O [README](../README.md) descreve a operação e seus limites.

A varredura completa de configuração ainda registra:

- DS-0026, LOW: ausência de `HEALTHCHECK` nos Dockerfiles Toolkit, Tinyproxy e
  OpenVPN. O servidor OpenVPN tem probe no Compose; a imagem Toolkit
  também atende comandos interativos e de execução única. Esses avisos não
  foram suprimidos nem tratados como corrigidos.
- No exemplo Kubernetes: KSV-0020/KSV-0021 (UID/GID), KSV-0110 (namespace), todos
  LOW, e KSV-0125 (restrição de registry), MEDIUM. Não são alertas de Dockerfile.

Antes da publicação:

1. Revisar o diff público e autorizar commit/push. Nenhuma publicação ou
   implantação foi feita durante esta continuação local.
2. Executar a aceitação nativa do novo conjunto AMD64/ARM64 e repetir todos os
   gates no commit final. A aceitação nativa anterior do Hub não valida
   automaticamente estas novas imagens legadas.
3. Verificar a união real de manifests, a extração de attestations e a cópia
   preservando digests em CI. Os testes locais simulam falhas dessas operações;
   não substituem uma publicação real.

O [modelo de builds distribuídos do Docker](https://docs.docker.com/build/ci/github-actions/multi-platform/)
permite compilar cada arquitetura em seu runner e formar o manifest multiarch
após as verificações, preservando provenance e SBOM.

## Validação complementar do empacotamento

Foi exercitado um registry local temporário com TLS verificado, usando imagens
sintéticas OCI para AMD64 e ARM64. As imagens contêm apenas um arquivo de teste:
não executam binários ARM64 e não demonstram aceitação do runtime nessa arquitetura.

Com Buildx 0.36.1, Distribution 3.1.1 e Skopeo 1.22.2:

- A importação dos arquivos OCI preservou os dois digests originais do build.
- A união real dos manifests manteve as duas arquiteturas, os labels de versão
  e revisão e os documentos de SBOM e proveniência de cada plataforma.
- A cópia entre dois repositórios locais preservou o digest multiarch e os
  documentos completos; a repetição reutilizou as tags imutáveis existentes.
- Um conflito real de tag imutável bloqueou todas as cópias e preservou o alias.
- Manifests reais sem uma arquitetura ou sem attestation foram recusados pelos
  validadores utilizados na publicação.

Os 196 testes automatizados foram repetidos com sucesso, assim como Actionlint
e a verificação de whitespace. Essa validação reduz a lacuna de empacotamento;
permanecem pendentes os runners nativos do novo conjunto, as permissões reais
de GHCR/Docker Hub e a execução completa dos workflows no commit final.

# Remediação das imagens

Atualização: 2026-09-12. O commit `98edfee` passou no CI nativo AMD64/ARM64,
com 44 checks aprovados e publicação desabilitada no PR. O delta posterior de
healthcheck, conta Kubernetes e manutenção foi validado localmente; exige novo
CI no commit definitivo. Publicação ainda pendente. Os riscos residuais foram
aceitos exclusivamente para a próxima RC, sem alterar os resultados dos scanners.
Não interpretar o filtro HIGH/CRITICAL como ausência de vulnerabilidades. Veja a
[revisão atual](../SECURITY_REVIEW.md).

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

| Imagem local      | HIGH/CRITICAL | CVEs distintos | Ocorrências | MEDIUM | LOW |
| ----------------- | ------------: | -------------: | ----------: | -----: | --: |
| Toolkit           |             0 |             47 |          61 |     47 |  14 |
| Squid             |             0 |              8 |          14 |      7 |   7 |
| Tinyproxy         |             0 |              6 |          10 |      6 |   4 |
| OpenVPN           |             0 |              8 |          12 |      6 |   6 |
| Hub padrão Ubuntu |             0 |             48 |          63 |     47 |  16 |

Identificadores locais dos builds revalidados em 2026-09-12, não referências publicadas:

- Toolkit: `sha256:86e1dfcaa80375fc373bdfbf5daef18dba12fbc9bbb892e3c365a3d6b29da0e9`.
- Squid: `sha256:6ebb7c4c66e7178bab7275c07aea957863781f3a9bdb055faa2790b3bc32061b`.
- Tinyproxy: `sha256:ab94c21dedf0d8f257ed15afbeb13894dadc7200a7a4e8c6954c38900b12e108`.
- OpenVPN: `sha256:fecd216e383fa9427a039ab59ffecf4a9dbbc793d21ab120350d8127d2d2e175`.
- Hub padrão: `sha256:6cb28985f836788314ee3b82294407699f4f6f505d1927848a63c77f11a9e72d`.

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
- 205 testes automatizados aprovados, incluindo contratos de versão, seleção,
  políticas de publicação, identidade dos manifests e falhas de registry.
  Sintaxe do Compose e Actionlint aprovados. Semgrep 1.176.1: zero achados em
  119 arquivos com 107 regras; uma execução explícita também cobriu os novos
  scripts de publicação sem depender da seleção de arquivos do Git.
- `npm audit`: zero vulnerabilidades conhecidas. Gitleaks 8.30.1: zero achados
  na árvore de trabalho. O verificador de conteúdo público e a revisão de
  marcadores internos dos 48 arquivos alterados também passaram.

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

DS-0026 foi corrigido nas três imagens: Toolkit verifica CLI e Secret Service;
Tinyproxy responde pelo endpoint `StatHost` interno; OpenVPN verifica TUN e o
socket configurado, compartilhando o probe com o Compose. Os testes incluem
falhas de serviço/porta. O keeper de rede continua com seu probe próprio.
Comandos de execução única terminam normalmente; o healthcheck não os transforma
em serviços nem comprova acesso remoto.

KSV-0020/KSV-0021 e KSV-0110 foram corrigidos no exemplo Kubernetes. A conta
10001 existe na imagem para permitir a inicialização do D-Bus. Mantém-se o
usuário padrão 1000 para compatibilidade com instalações existentes. O smoke
local também passou com UID/GID 10001, raiz somente leitura, zero capabilities,
duas sessões, keyrings/D-Bus, HTTP/SOCKS5, locking, persistência e reinício.
O teste simula fsGroup num volume novo; não valida um cluster ou driver CSI real.

Permanece KSV-0125, MEDIUM: a lista padrão do Trivy não inclui GHCR. O exemplo
usa o repositório público real; a política de confiança/admissão deve ser definida
por cada implantação. Não há supressão ou registry fictício para aprovar o check.

Antes da publicação:

1. Aceite explícito dos riscos residuais registrado para a próxima RC; o Sonar
   permanece `ERROR` e os achados não foram marcados como corrigidos.
2. Revisar o diff público, fazer commit/push e repetir os gates nativos no
   commit definitivo. O CI de `98edfee` já passou para o conjunto de imagens,
   mas não cobre alterações posteriores.
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

Na rodada de registry local, os 196 testes automatizados foram repetidos com sucesso, assim como Actionlint
e a verificação de whitespace. Essa validação reduz a lacuna de empacotamento;
o CI nativo do conjunto em `98edfee` também passou. Permanecem pendentes as
permissões reais de publicação em GHCR/Docker Hub, a promoção da RC e a repetição
dos workflows no próximo commit. O teste local não publicou nesses registries.

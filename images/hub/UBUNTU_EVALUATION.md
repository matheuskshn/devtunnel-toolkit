# Avaliação do Hub com Ubuntu 26.04 LTS

Data: 2026-09-10. Candidata local, sem promoção para a imagem padrão.

Atualização de 2026-09-11: Dockerfile.ubuntu também incorpora os
[builds próprios de Expat, GLib e p11-kit](UBUNTU_CVE_REMEDIATION.md), com correções
para 32 CVEs e evidência dos oito prioritários restantes. O scan bruto permanece
em 60 ocorrências. A revisão posterior também corrigiu o empacotamento e tentou
ARM64 com emulação temporária, encontrando um bloqueio do LeakSanitizer.
Consultar esse relatório para o estado atual e as limitações; o relato ARM64
abaixo preserva a primeira tentativa, anterior à emulação.

Atualização: a [triagem dos 70 CVEs](UBUNTU_CVE_TRIAGE.md) reduziu o resultado
para 60 ocorrências e 46 CVEs distintos. Quatro CVEs de Perl já estavam corrigidos
e deixaram de ser reportados após atualizar a base do scanner; 20 de rust-coreutils
foram removidos com a troca pelo provedor GNU oficial. Dos 11 prioritários, restam
oito naquela etapa. Os números abaixo preservam a
comparação inicial, não o resultado mais recente.

## Resultado

A candidata de [Dockerfile.ubuntu](Dockerfile.ubuntu) passou pelos testes locais
AMD64 e pelo filtro HIGH/CRITICAL do Trivy. Isso não significa que todos os
problemas de segurança foram corrigidos: há alertas restantes e diferenças de
classificação entre os fornecedores. A aceitação ARM64 permanece pendente.

O Dockerfile padrão, as imagens legadas e os workflows de publicação não foram
migrados. O runtime Ubuntu usa o mesmo código, Node, SDK e CLI do baseline;
somente a base, as dependências nativas e a preparação do usuário mudaram.

## Composição

- Base Ubuntu 26.04 fixada pelo digest de índice multi-arquitetura
  `sha256:513c074113a871b51a8d16ab445c88779d6452d937a164fb5cc479f32668a41d`.
- Node 24.21.0 proveniente do mesmo estágio de build já fixado no baseline.
  Não são copiadas bibliotecas de sistema Debian para o runtime Ubuntu.
- CLI Dev Tunnels 1.0.2030 com os mesmos checksums por arquitetura.
- ICU 78.2, Squid 7.2-2ubuntu2.2, GNOME Keyring 50.0 e libsecret 0.21.7.
- Usuário da base renomeado para hub, com UID/GID 1000 verificados, home
  /home/hub e shell nologin. Os diretórios e o contrato de volumes são mantidos.
- D-Bus e keyrings privados preservados, com tini como processo inicial.
- Removido somente /usr/bin/pebble, supervisor herdado da base e não gerenciado
  pelo dpkg. O Hub não o utiliza. Esse binário continha oito alertas HIGH na
  biblioteca Go incorporada. Não foram removidos inventários de pacotes,
  metadados do CLI ou dependências essenciais para esconder alertas.

As atualizações apt utilizam os repositórios assinados da própria distribuição,
sem pacotes Debian misturados, Ubuntu Pro ou exceções de verificação de assinatura.
A cobertura de manutenção depende também do componente Main/Universe, não apenas
do rótulo LTS. Ver [ciclo e cobertura Ubuntu](https://ubuntu.com/about/release-cycle).

## Comparação de segurança

Mesma ferramenta Trivy 0.74.0, mesma base de vulnerabilidades de
2026-09-09T19:10:00Z e varreduras por IDs imutáveis em linux/amd64.
Foi solicitada atualização da base antes da comparação; essa era a versão
retornada. Não foram usados ignore-unfixed, VEX ou supressões.

| Medida | Debian 13.6 | Ubuntu inicial | Ubuntu final |
| --- | ---: | ---: | ---: |
| Ocorrências de todas as severidades | 367 | 104 | 96 |
| Identificadores distintos | 178 | 78 | 70 |
| CRITICAL | 14 | 0 | 0 |
| HIGH | 81 | 8 | 0 |
| MEDIUM | 136 | 80 | 80 |
| LOW | 129 | 16 | 16 |
| UNKNOWN | 7 | 0 | 0 |
| Pacotes do sistema detectados | 230 | 285 | 285 |

O filtro `--severity HIGH,CRITICAL --exit-code 1` passou na candidata final.
As 96 ocorrências finais são de pacotes Ubuntu, sem versão corrigida indicada
nessa base do scanner. Não houve achados npm ou nos metadados .NET examinados.
Isso não constitui auditoria completa do binário autocontido do CLI.

### Correção não é reclassificação

Dos 33 CVEs distintos HIGH/CRITICAL do baseline, 22 não foram reportados na
candidata e 11 continuam presentes como MEDIUM. Ausência no novo resultado,
isoladamente, não comprova correção de cada CVE.

Entre os cinco CVEs antes classificados como críticos:

| CVE | Resultado Ubuntu | Evidência ou limite |
| --- | --- | --- |
| CVE-2026-42496 | Não reportado | Perl instalado 5.40.1-7ubuntu0.3, posterior ao pacote corrigido 5.40.1-7ubuntu0.1 indicado pelo fornecedor |
| CVE-2026-6653 | Não reportado | libxml2 2.15.2; Ubuntu 26.04 consta como não afetado |
| CVE-2026-8376 | Não reportado | Ainda requer encerramento individual da triagem; não foi criado VEX |
| CVE-2026-13221 | Continua afetado, MEDIUM | Mudança de classificação, não correção |
| CVE-2026-58016 | Continua afetado, MEDIUM | Mudança de classificação, não correção |

Fontes: [Perl Archive::Tar](https://ubuntu.com/security/CVE-2026-42496),
[libxml2](https://ubuntu.com/security/CVE-2026-6653) e
[seleção de fonte e severidade do Trivy](https://trivy.dev/docs/v0.74/guide/scanner/vulnerability/#severity-selection).

Os outros nove CVEs do antigo conjunto HIGH/CRITICAL ainda reportados são
CVE-2026-57432, CVE-2026-57433, CVE-2026-58010, CVE-2026-58011,
CVE-2026-58012, CVE-2026-58013, CVE-2026-58014, CVE-2026-58015 e CVE-2026-76957.
O filtro do scanner aprovado não encerra a análise de exposição desses problemas.

## Testes e limites

- 179 testes automatizados aprovados: 148 do Hub e 31 da automação.
- Smoke de container: duas sessões SDK, HTTP/SOCKS pelo Squid, DNS, regras de
  negação, identidade na auditoria, isolamento de listeners, CLI, locks e restart.
- Keyrings: D-Bus e homes independentes, segredo sintético isolado por sessão e
  persistência após reiniciar o runtime.
- Console: autenticação local, recuperação por CLI, configurações criptografadas,
  ações estruturadas e restauração após reinício.
- PostgreSQL: credenciais criptografadas, restauração em diretório novo, rotação
  de chave, exclusão mútua, SIGKILL/substituição e encerramento ao perder o banco.
- Os três smokes foram repetidos na imagem final, após a remoção do Pebble.
- Execução não root, raiz somente leitura, capabilities removidas e
  no-new-privileges nos testes. Sem sessões externas ou relay real nessa etapa.
- ARM64: tentativa de build interrompida por `exec /bin/sh: exec format error`.
  O executor local é AMD64 e não dispõe de emulação ARM64. Não foi alterado o
  binfmt do host. Disponibilidade do manifest ARM64 não é validação de execução.

O tamanho informado por `docker image inspect .Size` aumentou de 175.444.691 para
274.435.765 bytes, aproximadamente 56%. Essa métrica não representa RSS nem
tráfego de download. A candidata melhora o resultado da varredura, mas é maior.

## Reprodução

Na raiz do repositório, em executor AMD64 com conectividade pública:

```sh
docker build --platform linux/amd64 -f images/hub/Dockerfile.ubuntu \
  -t devtunnel-toolkit-hub:ubuntu-eval-local images/hub
cd images/hub
npm run test:coverage
HUB_TEST_IMAGE=devtunnel-toolkit-hub:ubuntu-eval-local npm run test:container
HUB_TEST_IMAGE=devtunnel-toolkit-hub:ubuntu-eval-local npm run test:postgres
```

Varredura completa, filtro de publicação e SBOM:

```sh
HUB_REVIEW_DIR="$(mktemp -d)"
trivy image --scanners vuln --format json --output "$HUB_REVIEW_DIR/ubuntu-scan.json" \
  devtunnel-toolkit-hub:ubuntu-eval-local
trivy image --scanners vuln --severity HIGH,CRITICAL --exit-code 1 \
  devtunnel-toolkit-hub:ubuntu-eval-local
trivy image --scanners vuln --format cyclonedx --output "$HUB_REVIEW_DIR/ubuntu-sbom.cdx.json" \
  devtunnel-toolkit-hub:ubuntu-eval-local
```

Guardar relatórios fora da árvore de fontes. Para comparação controlada,
atualizar a base uma vez e usar `--skip-db-update` nas duas imagens; comparar
também os identificadores e estados dos CVEs, não só suas severidades.

IDs locais testados e varridos:

- Debian: `sha256:19275f82aa59327f36be6351766e41328771738a8e81d5b8152aad916ab86fb8`.
- Ubuntu final: `sha256:adf0ebf44e6e306ac2e9f67d2048c1a3461d5470638490830be4c12af30b223d`.

Reconstruções podem receber novas atualizações apt e precisam de nova aceitação.
Antes de promover: validar ARM64, concluir a triagem dos CVEs restantes e avaliar
o aumento de tamanho. Os bloqueios de código/qualidade e das imagens legadas
descritos na revisão de segurança não são resolvidos por esta candidata.

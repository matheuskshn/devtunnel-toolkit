# Triagem dos CVEs remanescentes no Ubuntu

Data: 2026-09-10. Escopo: candidata Ubuntu do Hub, linux/amd64.

Atualização de 2026-09-11: a [remediação por builds nativos](UBUNTU_CVE_REMEDIATION.md)
incorporou correções para 32 dos 46 CVEs restantes, incluindo os oito prioritários.
O scan bruto continua em 60 ocorrências; o novo relatório separa correções,
aplicabilidade e pendências. Os números abaixo preservam a etapa anterior.

## Resultado

| Etapa | Ocorrências | CVEs distintos | CRITICAL/HIGH |
| --- | ---: | ---: | ---: |
| Resultado anterior, base Trivy de 2026-09-09 | 96 | 70 | 0 |
| Mesma imagem, base Trivy recém-baixada de 2026-09-10 | 80 | 66 | 0 |
| Candidata com GNU coreutils, mesma base recém-baixada | 60 | 46 | 0 |

Das 36 ocorrências que deixaram de aparecer, 16 eram de quatro CVEs de Perl
já corrigidos no pacote instalado. As outras 20 foram eliminadas pela
substituição de rust-coreutils por GNU coreutils. Não se atribuem ao código
mudanças de resultado causadas exclusivamente pela atualização do scanner.

Dos 11 CVEs prioritários, três de Perl foram confirmados como corrigidos:
CVE-2026-13221, CVE-2026-57432 e CVE-2026-57433. Restam sete de GLib,
CVE-2026-58010 a CVE-2026-58016, e um de Expat, CVE-2026-76957.
A imagem final ainda tem 44 ocorrências MEDIUM e 16 LOW. Não está sem CVEs.

## Evidência e remediação

Foram consultados os 70 registros OSV da Canonical na revisão
`6a4b346dab60ab6c7c569a4600c3dd5331ad92ff` do
[repositório oficial de avisos](https://github.com/canonical/ubuntu-security-notices/tree/6a4b346dab60ab6c7c569a4600c3dd5331ad92ff/osv/cve).
Todos possuem registro para Ubuntu:26.04:LTS; o cruzamento usa o pacote-fonte
correspondente ao pacote instalado, não versões de outras distribuições ou Pro.

O pacote Perl 5.40.1-7ubuntu0.3 já contém os quatro patches, conforme seu
changelog e os eventos fixed nos registros OSV:
[CVE-2026-12087](https://raw.githubusercontent.com/canonical/ubuntu-security-notices/6a4b346dab60ab6c7c569a4600c3dd5331ad92ff/osv/cve/2026/UBUNTU-CVE-2026-12087.json),
[CVE-2026-13221](https://raw.githubusercontent.com/canonical/ubuntu-security-notices/6a4b346dab60ab6c7c569a4600c3dd5331ad92ff/osv/cve/2026/UBUNTU-CVE-2026-13221.json),
[CVE-2026-57432](https://raw.githubusercontent.com/canonical/ubuntu-security-notices/6a4b346dab60ab6c7c569a4600c3dd5331ad92ff/osv/cve/2026/UBUNTU-CVE-2026-57432.json) e
[CVE-2026-57433](https://raw.githubusercontent.com/canonical/ubuntu-security-notices/6a4b346dab60ab6c7c569a4600c3dd5331ad92ff/osv/cve/2026/UBUNTU-CVE-2026-57433.json).
A comparação de versões foi verificada com dpkg. O novo scan confirmou
a ausência desses alertas sem alterar Perl, excluir regras ou aplicar VEX.

A troca de coreutils segue a
[alternativa documentada pelo Ubuntu](https://documentation.ubuntu.com/release-notes/26.04/summary-for-lts-users/).
O [helper de build](bin/use-gnu-coreutils) simula e valida o plano antes de
executá-lo: somente coreutils-from-uutils e rust-coreutils podem ser removidos.
O sinalizador allow-remove-essential é restrito a essa substituição do
provedor, não à remoção genérica de dependências essenciais. Foram mantidos
gnu-coreutils 9.7-3ubuntu2.1 e instalado coreutils-from-gnu 0.0.0~ubuntu25.
O helper verifica o estado dos pacotes, a consistência dpkg e os executáveis GNU.
Quatro testes cobrem plano correto, remoção adicional, remoção ausente e duplicada.

## O que impede zerar os alertas com os pacotes atuais

- Expat concentra 23 CVEs e GLib nove. O APT oferece as mesmas versões instaladas,
  2.7.4-1 e 2.88.0-1, sem atualização aplicável. Os registros da Canonical
  continuam indicando vulnerabilidade. Existem referências a correções upstream,
  mas não um pacote corrigido para esta versão estável.
  [Expat](https://ubuntu.com/security/CVE-2026-76957),
  [GLib](https://ubuntu.com/security/CVE-2026-58016).
- Remover pinentry-gnome3 também removeria gnome-keyring. Remover Cairo/Perl
  removeria keyring e Squid. Essas operações foram apenas simuladas e rejeitadas;
  não foram forçadas dependências nem retirado o inventário dpkg.
- Os demais 14 CVEs estão em componentes como glibc, bibliotecas gráficas,
  certificados, ferramentas de contas e arquivos. Os registros consultados
  não fornecem versão corrigida Ubuntu 26.04 para os pacotes-fonte correspondentes.
  Para glibc, a Canonical registra correção adiada e uma nota de 2026-09-03 sobre
  ausência de patch upstream. [CVE-2026-18374](https://ubuntu.com/security/CVE-2026-18374).
- Alguns alertas exigem análise de aplicabilidade, não apenas atualização.
  Por exemplo, CVE-2026-40228 descreve systemd-journald, cujo executável está
  ausente na candidata. O scanner o atribui também às bibliotecas e ao sysusers
  derivados do pacote-fonte systemd. Não foi suprimido ou marcado como corrigido.
  [Condições do problema](https://ubuntu.com/security/CVE-2026-40228).

Avançar além disso requer avaliar pacotes próprios com backports, reconstrução
de componentes ou mudanças no armazenamento de credenciais. Isso acrescenta
responsabilidade por patches, ABI, assinatura, SBOM e manutenção das duas
arquiteturas. Não foram misturados pacotes de outra distribuição nem adotadas
versões de desenvolvimento para reduzir a contagem.

## Validação

- 183 testes aprovados: 152 do Hub e 31 da automação.
- Três smokes aprovados na imagem final: HTTP/SOCKS e isolamento por sessão;
  console, autenticação local e reinício; PostgreSQL, criptografia, locks,
  rotação, SIGKILL/substituição e perda de conexão.
- Execução não root e raiz somente leitura; D-Bus e keyrings preservados.
- Trivy 0.74.0, banco de 2026-09-10T13:49:36Z, baixado em cache separado.
- SBOM CycloneDX gerado. Gate HIGH/CRITICAL aprovado sem ignore-unfixed,
  ignorefile, VEX ou mudança de severidade configurada pelo projeto.
- Comparação de 24 hashes confirma os mesmos arquivos de aplicação, frontend,
  Node e CLI da imagem Debian de referência.
- Verificação de conteúdo público e Gitleaks aprovados; revisão de diff sem
  erros de whitespace. Esses checks não substituem revisão humana de dados.
- ARM64 e logins externos reais continuam pendentes.
- Nenhuma alteração na imagem padrão Debian ou nas imagens legadas.

Candidata: `devtunnel-toolkit-hub:ubuntu-remediated-local`.
ID local: `sha256:af835c48d0a8e822794b093e561540691993f6e60417dd2395ca0d02a84b685d`.

Para reconstruir, usar Dockerfile.ubuntu. Para comparar corretamente, baixar
a base do scanner em um cache novo e usá-la nas duas imagens com
`--skip-db-update`. Não comparar apenas totais de scans com bases diferentes.

## Matriz completa

Asterisco identifica os 11 CVEs prioritários. Antes e depois são ocorrências
pacote/CVE. Pendente significa que não há pacote oficial corrigido identificado,
não que a exploração no Hub tenha sido demonstrada. Nenhuma linha desta matriz
é uma configuração de supressão do scanner.

| CVE | Pacote-fonte | Antes | Depois | Situação |
| --- | --- | ---: | ---: | --- |
| [CVE-2017-7475](https://ubuntu.com/security/CVE-2017-7475) | cairo | 3 | 3 | Pendente: sem pacote oficial corrigido |
| [CVE-2018-18064](https://ubuntu.com/security/CVE-2018-18064) | cairo | 3 | 3 | Pendente: sem pacote oficial corrigido |
| [CVE-2019-12522](https://ubuntu.com/security/CVE-2019-12522) | squid | 2 | 2 | Pendente: sem pacote oficial corrigido |
| [CVE-2023-37769](https://ubuntu.com/security/CVE-2023-37769) | pixman | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2024-56433](https://ubuntu.com/security/CVE-2024-56433) | shadow | 2 | 2 | Pendente: sem pacote oficial corrigido |
| [CVE-2025-1352](https://ubuntu.com/security/CVE-2025-1352) | elfutils | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2025-1376](https://ubuntu.com/security/CVE-2025-1376) | elfutils | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2025-5222](https://ubuntu.com/security/CVE-2025-5222) | icu | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2025-59529](https://ubuntu.com/security/CVE-2025-59529) | avahi | 3 | 3 | Pendente: sem pacote oficial corrigido |
| [CVE-2025-66382](https://ubuntu.com/security/CVE-2025-66382) | expat | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-12087](https://ubuntu.com/security/CVE-2026-12087) | perl | 4 | 0 | Já corrigido: 5.40.1-7ubuntu0.3; base do scanner atualizada |
| [CVE-2026-13221](https://ubuntu.com/security/CVE-2026-13221) * | perl | 4 | 0 | Já corrigido: 5.40.1-7ubuntu0.3; base do scanner atualizada |
| [CVE-2026-13757](https://ubuntu.com/security/CVE-2026-13757) | p11-kit | 3 | 3 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-15588](https://ubuntu.com/security/CVE-2026-15588) | glib2.0 | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-16118](https://ubuntu.com/security/CVE-2026-16118) | glib2.0 | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-18374](https://ubuntu.com/security/CVE-2026-18374) | glibc | 3 | 3 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-18477](https://ubuntu.com/security/CVE-2026-18477) | tar | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-18508](https://ubuntu.com/security/CVE-2026-18508) | tar | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-32776](https://ubuntu.com/security/CVE-2026-32776) | expat | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-32777](https://ubuntu.com/security/CVE-2026-32777) | expat | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-32778](https://ubuntu.com/security/CVE-2026-32778) | expat | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-35341](https://ubuntu.com/security/CVE-2026-35341) | rust-coreutils | 1 | 0 | Removido: substituído por GNU coreutils |
| [CVE-2026-35344](https://ubuntu.com/security/CVE-2026-35344) | rust-coreutils | 1 | 0 | Removido: substituído por GNU coreutils |
| [CVE-2026-35345](https://ubuntu.com/security/CVE-2026-35345) | rust-coreutils | 1 | 0 | Removido: substituído por GNU coreutils |
| [CVE-2026-35348](https://ubuntu.com/security/CVE-2026-35348) | rust-coreutils | 1 | 0 | Removido: substituído por GNU coreutils |
| [CVE-2026-35350](https://ubuntu.com/security/CVE-2026-35350) | rust-coreutils | 1 | 0 | Removido: substituído por GNU coreutils |
| [CVE-2026-35351](https://ubuntu.com/security/CVE-2026-35351) | rust-coreutils | 1 | 0 | Removido: substituído por GNU coreutils |
| [CVE-2026-35352](https://ubuntu.com/security/CVE-2026-35352) | rust-coreutils | 1 | 0 | Removido: substituído por GNU coreutils |
| [CVE-2026-35354](https://ubuntu.com/security/CVE-2026-35354) | rust-coreutils | 1 | 0 | Removido: substituído por GNU coreutils |
| [CVE-2026-35357](https://ubuntu.com/security/CVE-2026-35357) | rust-coreutils | 1 | 0 | Removido: substituído por GNU coreutils |
| [CVE-2026-35359](https://ubuntu.com/security/CVE-2026-35359) | rust-coreutils | 1 | 0 | Removido: substituído por GNU coreutils |
| [CVE-2026-35360](https://ubuntu.com/security/CVE-2026-35360) | rust-coreutils | 1 | 0 | Removido: substituído por GNU coreutils |
| [CVE-2026-35363](https://ubuntu.com/security/CVE-2026-35363) | rust-coreutils | 1 | 0 | Removido: substituído por GNU coreutils |
| [CVE-2026-35364](https://ubuntu.com/security/CVE-2026-35364) | rust-coreutils | 1 | 0 | Removido: substituído por GNU coreutils |
| [CVE-2026-35367](https://ubuntu.com/security/CVE-2026-35367) | rust-coreutils | 1 | 0 | Removido: substituído por GNU coreutils |
| [CVE-2026-35368](https://ubuntu.com/security/CVE-2026-35368) | rust-coreutils | 1 | 0 | Removido: substituído por GNU coreutils |
| [CVE-2026-35370](https://ubuntu.com/security/CVE-2026-35370) | rust-coreutils | 1 | 0 | Removido: substituído por GNU coreutils |
| [CVE-2026-35371](https://ubuntu.com/security/CVE-2026-35371) | rust-coreutils | 1 | 0 | Removido: substituído por GNU coreutils |
| [CVE-2026-35373](https://ubuntu.com/security/CVE-2026-35373) | rust-coreutils | 1 | 0 | Removido: substituído por GNU coreutils |
| [CVE-2026-35374](https://ubuntu.com/security/CVE-2026-35374) | rust-coreutils | 1 | 0 | Removido: substituído por GNU coreutils |
| [CVE-2026-35377](https://ubuntu.com/security/CVE-2026-35377) | rust-coreutils | 1 | 0 | Removido: substituído por GNU coreutils |
| [CVE-2026-40228](https://ubuntu.com/security/CVE-2026-40228) | systemd | 3 | 3 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-41080](https://ubuntu.com/security/CVE-2026-41080) | expat | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-45186](https://ubuntu.com/security/CVE-2026-45186) | expat | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-50219](https://ubuntu.com/security/CVE-2026-50219) | expat | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-56131](https://ubuntu.com/security/CVE-2026-56131) | expat | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-56132](https://ubuntu.com/security/CVE-2026-56132) | expat | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-56403](https://ubuntu.com/security/CVE-2026-56403) | expat | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-56404](https://ubuntu.com/security/CVE-2026-56404) | expat | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-56405](https://ubuntu.com/security/CVE-2026-56405) | expat | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-56406](https://ubuntu.com/security/CVE-2026-56406) | expat | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-56407](https://ubuntu.com/security/CVE-2026-56407) | expat | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-56408](https://ubuntu.com/security/CVE-2026-56408) | expat | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-56409](https://ubuntu.com/security/CVE-2026-56409) | expat | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-56410](https://ubuntu.com/security/CVE-2026-56410) | expat | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-56411](https://ubuntu.com/security/CVE-2026-56411) | expat | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-56412](https://ubuntu.com/security/CVE-2026-56412) | expat | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-57432](https://ubuntu.com/security/CVE-2026-57432) * | perl | 4 | 0 | Já corrigido: 5.40.1-7ubuntu0.3; base do scanner atualizada |
| [CVE-2026-57433](https://ubuntu.com/security/CVE-2026-57433) * | perl | 4 | 0 | Já corrigido: 5.40.1-7ubuntu0.3; base do scanner atualizada |
| [CVE-2026-58010](https://ubuntu.com/security/CVE-2026-58010) * | glib2.0 | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-58011](https://ubuntu.com/security/CVE-2026-58011) * | glib2.0 | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-58012](https://ubuntu.com/security/CVE-2026-58012) * | glib2.0 | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-58013](https://ubuntu.com/security/CVE-2026-58013) * | glib2.0 | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-58014](https://ubuntu.com/security/CVE-2026-58014) * | glib2.0 | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-58015](https://ubuntu.com/security/CVE-2026-58015) * | glib2.0 | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-58016](https://ubuntu.com/security/CVE-2026-58016) * | glib2.0 | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-66046](https://ubuntu.com/security/CVE-2026-66046) | expat | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-72522](https://ubuntu.com/security/CVE-2026-72522) | expat | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-76641](https://ubuntu.com/security/CVE-2026-76641) | expat | 1 | 1 | Pendente: sem pacote oficial corrigido |
| [CVE-2026-76957](https://ubuntu.com/security/CVE-2026-76957) * | expat | 1 | 1 | Pendente: sem pacote oficial corrigido |

# Remediação das bibliotecas nativas do Hub Ubuntu

Data: 2026-09-11. Candidata local linux/amd64, sem promoção da imagem padrão.

## Resultado e limites

As quatro frentes foram executadas: avaliação de remoções, aplicação de correções
disponíveis, verificação de aplicabilidade e registro individual das pendências.
Isso não significa que todos os CVEs foram eliminados.

Foram incorporadas correções upstream para **32 dos 46 CVEs**, correspondentes
a 34 das 60 ocorrências. Os oito prioritários que ainda estavam abertos estão
nesse grupo: sete de GLib e um de Expat. Somados aos três de Perl já confirmados
na etapa anterior, os 11 prioritários possuem evidência de correção na candidata.

**O scan bruto continua em 46 CVEs / 60 ocorrências**, sendo 44 MEDIUM e 16 LOW,
sem HIGH ou CRITICAL. O Trivy identifica as versões próprias instaladas, mas os
registros Ubuntu consultados continuam sem um limite de versão corrigida para
esses pacotes. O scanner não verifica o conteúdo dos patches aplicados por nós.
Não foram usados ignorefile, ignore-unfixed, VEX ou alteração de severidade.

| Avaliação da candidata | CVEs | Ocorrências brutas |
| --- | ---: | ---: |
| Corrigido por build próprio | 32 | 34 |
| Componente afetado ausente | 4 | 8 |
| Mitigação condicional | 2 | 4 |
| Não reproduzido; não encerrado | 1 | 1 |
| Pendente | 7 | 13 |

Os grupos são mutuamente exclusivos e somam 46 / 60. Apenas o primeiro significa
correção de código. Os outros grupos não são somados como CVEs corrigidos.

Candidata: `devtunnel-toolkit-hub:ubuntu-acceptance-v2-local`.
ID: `sha256:03a878c0bbec66fd945ede50f450728e6d8e6aeb6c7b961b33177abfcb142c23`.
Tamanho local informado pelo Docker: 285.024.343 bytes, contra 285.513.083 bytes
antes dos ajustes de empacotamento. As imagens anteriores foram preservadas.

## 1. Dependências dispensáveis

O APT não propôs pacotes órfãos adicionais para remover. As simulações foram
rejeitadas quando introduziam regressões:

- Retirar pinentry/GCR/Cairo retiraria o keyring e sua persistência.
- Retirar passwd/login.defs retiraria Squid e ssl-cert.
- Retirar dbus-x11 faria o resolvedor instalar a pilha systemd/PAM de sessão.
- Retirar tar deixa dependências essenciais sem solução.

Nenhuma dessas remoções foi forçada. A troca de Rust por GNU coreutils e a
remoção do supervisor Pebble, feitas anteriormente, continuam preservadas.
Nesta etapa não houve redução adicional de pacotes de runtime. Remover os
componentes gráficos restantes exigiria outra implementação/empacotamento
headless do keyring, com nova validação de compatibilidade e persistência.

## 2. Correções incorporadas

| Componente | Pacote anterior | Pacote próprio | Cobertura entre os 46 CVEs |
| --- | --- | --- | ---: |
| Expat | 2.7.4-1 | 2.8.4-0devtunnel1 | 22 |
| GLib | 2.88.0-1 | 2.88.3-0devtunnel1 + dois patches | 9 |
| p11-kit | 0.26.2-2 | 0.26.5-0devtunnel1 | 1 |

Fontes primárias:

- [Expat 2.8.4](https://github.com/libexpat/libexpat/releases/tag/R_2_8_4)
  e [changelog completo](https://github.com/libexpat/libexpat/blob/R_2_8_4/expat/Changes).
  Os 22 identificadores aparecem nas correções acumuladas do changelog.
  CVE-2025-66382 permanece pendente, inclusive na versão nova.
- [GLib 2.88.3](https://download.gnome.org/sources/glib/2.88/),
  [correção de introspecção !5156](https://gitlab.gnome.org/GNOME/glib/-/merge_requests/5156)
  e [correção MIME !5286](https://gitlab.gnome.org/GNOME/glib/-/merge_requests/5286).
  A versão estável incorpora as outras sete correções; os dois patches são
  aplicados integralmente, com checksum e sem fuzz.
- [p11-kit 0.26.5](https://github.com/p11-glue/p11-kit/releases/tag/0.26.5)
  e [histórico de correções](https://github.com/p11-glue/p11-kit/blob/0.26.5/NEWS).
  A atualização inclui o limite de recursão RPC e uma correção posterior de
  overflow, sem desabilitar o módulo de confiança.

### Empacotamento e rastreabilidade

[Dockerfile.ubuntu](Dockerfile.ubuntu) compila em estágio Ubuntu separado.
A [receita](bin/build-native-libraries) fixa versões e hashes SHA-256, executa
testes upstream e usa flags de hardening do dpkg. Não copia bibliotecas Debian
para o runtime Ubuntu.

O [empacotador](bin/package-native-libraries.mjs) parte dos pacotes Ubuntu
obtidos pelo APT, preserva configuração, copyright e scripts de manutenção,
substitui todos os ELFs correspondentes, verifica os símbolos públicos e calcula
dependências dos novos binários com dpkg-shlibdeps. A instalação usa APT normal,
sem forçar dependências. Versão, origem própria, tamanho, inventário e hashes
são atualizados. Não se altera apenas a versão para esconder uma biblioteca antiga.

A checagem adicional com Lintian 2.129.0ubuntu2.1 e abidiff 2.9.0 encontrou dois
problemas na primeira candidata: os 17 ELFs ainda continham símbolos estáticos
dispensáveis, e os dois módulos PKCS#11 não preservavam o SONAME do pacote Ubuntu.
O empacotador agora usa `strip --strip-unneeded` e rejeita qualquer diferença de
SONAME. O [patch de empacotamento](bin/p11-kit-module-soname.patch) define os
SONAMEs no link dos módulos, sem alterar seu código funcional.

Após reconstruir, os cinco pacotes passaram no Lintian sem erros ou avisos de
empacotamento. A comparação das dez bibliotecas com abidiff não encontrou remoção
de símbolos nem alteração de SONAME. Sete comparações foram idênticas; três
reportaram somente adições: `XML_SetHashSalt16Bytes` nas duas ABIs do Expat e
`g_slice_debug_tree_statistics` no GLib. Os logs não foram suprimidos.

O [verificador reproduzível](tests/native-package-audit.mjs) grava os resultados
por pacote e bloqueia erros do Lintian ou incompatibilidades detectadas pelo
abidiff. Deve ser executado em um estágio `native` descartável, com `lintian` e
`abigail-tools` instalados apenas nesse ambiente de revisão.
Sem informações de debug completas dos dois lados, o abidiff limita a comparação
a propriedades e símbolos ELF, conforme seu
[manual](https://sourceware.org/libabigail/manual/abidiff.html).
Isso não comprova compatibilidade de todos os tipos/layouts nem substitui uma
revisão independente por outro revisor.

A revisão adicional por um segundo agente identificou duas lacunas de aceitação,
ambas corrigidas: os probes C agora são compilados fora do runtime e executados
contra as bibliotecas da imagem final; o helper de fases rejeita chamadas sem
fase ou com `all`, que omitiam patches obrigatórios. O Dockerfile permanece como
orquestrador da sequência completa. O revisor conferiu as correções e não
reportou novos achados no delta. Essa revisão de código não é uma certificação
externa nem substitui a execução ARM64 pendente.

São cinco pacotes e 17 ELFs substituídos. Permanecem as duas ABIs do Expat,
os helpers do GLib e os módulos cliente/trust do p11-kit. O trust store continua
em `/etc/ssl/certs/ca-certificates.crt`.

A imagem inclui arquivos-fonte originais, patches, receita e evidências em
`/usr/local/share/devtunnel/native/`, além dos copyrights dos pacotes. Esses
artefatos possibilitam revisão e reconstrução dos componentes modificados.
Não foram adicionados compiladores ou SDKs ao estágio de runtime.

As fontes são fixadas e a receita é reproduzível, mas a reprodutibilidade
bit a bit entre dois builds limpos não foi comprovada. Os pacotes de ferramentas
obtidos do APT continuam sujeitos às atualizações dos repositórios.

## 3. Aplicabilidade e testes

- **187 testes do projeto aprovados:** 156 do Hub e 31 da automação.
- **Expat:** suíte upstream com 4.884 verificações, nenhuma falha.
  As bibliotecas instaladas libexpat e libexpatw passaram no teste de versão,
  parsing e rejeição de surrogate inválido. A suíte CTest completa é da ABI char;
  upstream não oferece essa suíte no modo ushort.
- **GLib:** dez suítes selecionadas aprovadas. Incluem GVariant, GDateTime,
  regex, canais de I/O, keyfiles, autenticação D-Bus, introspecção e contenttype.
- **CVE-2026-16118:** a [regressão](bin/native-mime-regression.c) reproduz o
  heap-buffer-overflow com AddressSanitizer antes do patch e passa depois dele.
  Exercita valores e máscaras de 16 e 32 bits no parser upstream real.
- **CVE-2026-58016:** quatro casos de aninhamento XML inválido rejeitados
  pela biblioteca instalada, além dos testes upstream.
- **p11-kit:** 67 testes upstream aprovados, incluindo RPC e confiança.
- **Três testes de integração aprovados na imagem final:** HTTP/SOCKS, regras e
  isolamento; console com autenticação local e reinício; PostgreSQL com
  criptografia, locks, rotação, SIGKILL/substituição e falha de conexão.
- **Runtime:** 17 hashes de ELFs conferidos e cinco versões dpkg confirmadas.
  Squid e demais processos observados com UID real/efetivo/salvo/fs 1000,
  capabilities efetivas zeradas e no-new-privileges, sob os flags do smoke.
- **Paridade da aplicação:** 24 hashes iguais aos do baseline para código,
  frontend, Node e CLI.
- SBOM CycloneDX gerado; gate HIGH/CRITICAL aprovado com Trivy 0.74.0 e
  base de 2026-09-11T01:19:20Z. Baseline e candidata usaram a mesma base.
- Check de conteúdo público, Gitleaks e verificação de whitespace aprovados.

Os testes, os três smokes, o scan bruto, o gate HIGH/CRITICAL e o SBOM foram
repetidos após os ajustes de empacotamento, usando a candidata indicada acima.

### Logins externos e persistência

GitHub e Microsoft foram autenticados simultaneamente em sessões separadas.
Para ambos, o login em cache, a identidade, o nome e os listeners permaneceram
estáveis após reinício e recriação do container com o mesmo volume. Consultas
autenticadas de leitura ao serviço Dev Tunnels passaram em cada etapa, sem
criação de túneis remotos.

O teste real usou o backend de arquivos. A cobertura PostgreSQL desta execução
continua sendo a dos testes sintéticos de criptografia, isolamento e recuperação,
não uma nova autenticação externa persistida em PostgreSQL. Reinício imediato
também não comprova renovação após expiração em 24 horas ou 30 dias.

### Tentativa ARM64

O build ARM64 foi iniciado com QEMU/binfmt temporário e chegou à compilação do
Expat. Uma sonda ARM64 isolada do parser MIME corrigido revelou que o
LeakSanitizer termina com `LeakSanitizer has encountered a fatal error`, seguido
de `LeakSanitizer does not work under ptrace`. O controle AMD64 passou com
`detect_leaks=1`; a sonda ARM64 passou apenas no diagnóstico com `detect_leaks=0`.

Esse diagnóstico reduzido não é aceitação. A receita mantém `detect_leaks=1`,
sem exceção por arquitetura. O build completo foi interrompido depois de
comprovar o bloqueio do teste obrigatório; não foi gerada nem aprovada uma imagem
ARM64 final. O próximo executor deve ser ARM64 nativo para concluir o build e
repetir os testes de runtime. O registro temporário `qemu-aarch64` foi removido,
preservando os registros que já existiam no host.

O workflow [Hub Ubuntu native acceptance](../../.github/workflows/hub-ubuntu-acceptance.yml)
foi preparado para `ubuntu-24.04` e `ubuntu-24.04-arm`. Ele verifica a arquitetura
do executor, mantém as regressões obrigatórias, executa os smokes de runtime,
audita os pacotes em ambiente separado e gera scan/SBOM da imagem exata.
Não usa QEMU, credenciais de registro, publicação ou acesso ao ACA. As evidências
são retidas por sete dias. Pode ser iniciado manualmente ou por PRs com mudanças
no código/testes da candidata; documentação isolada não dispara o build.
O arquivo foi validado localmente, inclusive com Actionlint, mas ainda não foi
enviado nem executado no GitHub; sua existência não encerra a aceitação ARM64.

A [sonda de segurança](tests/ubuntu-security-probe.mjs) confirma a ausência de
avahi-daemon, systemd-journald, genrb, eu-readelf, eu-strip, libdw e helpers de
mapeamento de UID/GID. O patch de CVE-2025-1352 altera libdw, não libelf.
A ausência do componente afetado é diferente da ausência de alertas no scanner.

O caso de CVE-2025-1376 em [elf_strptr](tests/elf-string-regression.c) não foi
reproduzido no baseline nem na candidata: a seção sem dados foi rejeitada sem
crash. O alerta não foi encerrado com base apenas nesse teste.

Mitigações de Squid e shadow dependem do modo de execução. Rodar como root,
adicionar capabilities, helpers de user namespaces ou volumes NFS invalida
essas conclusões e exige nova avaliação. A mera presença de USER no Dockerfile
não impede um operador de sobrescrevê-lo.

## 4. Pendências e manutenção

Continuam sem correção incorporada sete CVEs em Expat, glibc, Cairo, Pixman e tar,
mais o caso libelf que requer confirmação adicional. Os casos condicionais e
componentes ausentes também continuam no inventário bruto.

- Expat CVE-2025-66382: acompanhar a correção de complexidade algorítmica.
- glibc CVE-2026-18374: a nota consultada do fornecedor registra ausência de patch.
- Cairo/Pixman: falta comprovar um caminho explorável no Hub e uma correção
  completa; bibliotecas presentes não significam exploração demonstrada.
- tar: o fornecedor cita possíveis commits, mas não há conjunto de backport
  validado nesta avaliação. Não foram escolhidos commits especulativamente.
  A [mudança de abril sobre renames incrementais](https://git.savannah.gnu.org/cgit/tar.git/commit/?id=79d61af0e118a9368425729f624a66e1065be61e)
  foi [revertida em julho](https://git.savannah.gnu.org/cgit/tar.git/commit/?id=3903b37673dc025c51e6ea2a53a22318e1b48f46),
  junto de outras mudanças de extração; aplicar só aquele commit não seria
  uma correção validada dos dois CVEs.
  O Hub empacota credenciais em JSON criptografado, não em arquivos tar.
- libelf CVE-2025-1376: ampliar regressões e conferir a correspondência com o
  pacote-fonte antes de encerrar.
- ARM64: a emulação foi testada, mas o LeakSanitizer impede concluir a aceitação
  neste executor. É necessário repetir em ARM64 nativo.
- Logins externos reais com persistência em arquivos e revisão adicional por
  outro agente foram concluídos. Permanecem os limites de duração e backend
  descritos acima. Esta candidata não está aprovada para publicação.

Os pacotes próprios transferem manutenção de segurança para o projeto. A cada
atualização upstream ou da base: conferir avisos, atualizar hashes/patches,
reconstruir, repetir os smokes, conferir ABI e emitir novo SBOM/avaliação.
Uma atualização Ubuntu com versão numericamente menor que a versão própria
não será instalada automaticamente sobre ela. Quando houver pacote oficial
equivalente, planejar e validar a retirada do override local.

Não houve commit, push, publicação, alteração de ACA ou substituição do container
local em execução. As limitações das outras imagens e do Sonar continuam no
[relatório geral](../../SECURITY_REVIEW.md).

## Reprodução

```sh
docker build -f images/hub/Dockerfile.ubuntu -t devtunnel-toolkit-hub:ubuntu-acceptance-v2-local images/hub
cd images/hub
npm run test:coverage
HUB_TEST_IMAGE=devtunnel-toolkit-hub:ubuntu-acceptance-v2-local npm run test:container
HUB_TEST_IMAGE=devtunnel-toolkit-hub:ubuntu-acceptance-v2-local npm run test:postgres
```

Para regressões das bibliotecas instaladas, compilar os dois arquivos C em tests
em um ambiente compatível e executá-los em container descartável da candidata,
com rede desabilitada, raiz somente leitura, sem capabilities e /tmp temporário.
Nunca executar os casos de regressão contra um serviço compartilhado.

## Matriz dos 46 CVEs

Asterisco indica os oito prioritários desta etapa. Esta matriz e o
[registro estruturado](native-cve-assessment.json) são evidências de avaliação,
não arquivos de supressão ou aprovação automática do scanner.

| CVE | Pacote-fonte | Ocorrências brutas | Avaliação |
| --- | --- | ---: | --- |
| [CVE-2017-7475](https://ubuntu.com/security/CVE-2017-7475) | cairo | 3 | Pendente |
| [CVE-2018-18064](https://ubuntu.com/security/CVE-2018-18064) | cairo | 3 | Pendente |
| [CVE-2019-12522](https://ubuntu.com/security/CVE-2019-12522) | squid | 2 | Mitigação condicional |
| [CVE-2023-37769](https://ubuntu.com/security/CVE-2023-37769) | pixman | 1 | Pendente |
| [CVE-2024-56433](https://ubuntu.com/security/CVE-2024-56433) | shadow | 2 | Mitigação condicional |
| [CVE-2025-1352](https://ubuntu.com/security/CVE-2025-1352) | elfutils | 1 | Componente afetado ausente |
| [CVE-2025-1376](https://ubuntu.com/security/CVE-2025-1376) | elfutils | 1 | Não reproduzido; não encerrado |
| [CVE-2025-5222](https://ubuntu.com/security/CVE-2025-5222) | icu | 1 | Componente afetado ausente |
| [CVE-2025-59529](https://ubuntu.com/security/CVE-2025-59529) | avahi | 3 | Componente afetado ausente |
| [CVE-2025-66382](https://ubuntu.com/security/CVE-2025-66382) | expat | 1 | Pendente |
| [CVE-2026-13757](https://ubuntu.com/security/CVE-2026-13757) | p11-kit | 3 | Corrigido por build próprio |
| [CVE-2026-15588](https://ubuntu.com/security/CVE-2026-15588) | glib2.0 | 1 | Corrigido por build próprio |
| [CVE-2026-16118](https://ubuntu.com/security/CVE-2026-16118) | glib2.0 | 1 | Corrigido por build próprio |
| [CVE-2026-18374](https://ubuntu.com/security/CVE-2026-18374) | glibc | 3 | Pendente |
| [CVE-2026-18477](https://ubuntu.com/security/CVE-2026-18477) | tar | 1 | Pendente |
| [CVE-2026-18508](https://ubuntu.com/security/CVE-2026-18508) | tar | 1 | Pendente |
| [CVE-2026-32776](https://ubuntu.com/security/CVE-2026-32776) | expat | 1 | Corrigido por build próprio |
| [CVE-2026-32777](https://ubuntu.com/security/CVE-2026-32777) | expat | 1 | Corrigido por build próprio |
| [CVE-2026-32778](https://ubuntu.com/security/CVE-2026-32778) | expat | 1 | Corrigido por build próprio |
| [CVE-2026-40228](https://ubuntu.com/security/CVE-2026-40228) | systemd | 3 | Componente afetado ausente |
| [CVE-2026-41080](https://ubuntu.com/security/CVE-2026-41080) | expat | 1 | Corrigido por build próprio |
| [CVE-2026-45186](https://ubuntu.com/security/CVE-2026-45186) | expat | 1 | Corrigido por build próprio |
| [CVE-2026-50219](https://ubuntu.com/security/CVE-2026-50219) | expat | 1 | Corrigido por build próprio |
| [CVE-2026-56131](https://ubuntu.com/security/CVE-2026-56131) | expat | 1 | Corrigido por build próprio |
| [CVE-2026-56132](https://ubuntu.com/security/CVE-2026-56132) | expat | 1 | Corrigido por build próprio |
| [CVE-2026-56403](https://ubuntu.com/security/CVE-2026-56403) | expat | 1 | Corrigido por build próprio |
| [CVE-2026-56404](https://ubuntu.com/security/CVE-2026-56404) | expat | 1 | Corrigido por build próprio |
| [CVE-2026-56405](https://ubuntu.com/security/CVE-2026-56405) | expat | 1 | Corrigido por build próprio |
| [CVE-2026-56406](https://ubuntu.com/security/CVE-2026-56406) | expat | 1 | Corrigido por build próprio |
| [CVE-2026-56407](https://ubuntu.com/security/CVE-2026-56407) | expat | 1 | Corrigido por build próprio |
| [CVE-2026-56408](https://ubuntu.com/security/CVE-2026-56408) | expat | 1 | Corrigido por build próprio |
| [CVE-2026-56409](https://ubuntu.com/security/CVE-2026-56409) | expat | 1 | Corrigido por build próprio |
| [CVE-2026-56410](https://ubuntu.com/security/CVE-2026-56410) | expat | 1 | Corrigido por build próprio |
| [CVE-2026-56411](https://ubuntu.com/security/CVE-2026-56411) | expat | 1 | Corrigido por build próprio |
| [CVE-2026-56412](https://ubuntu.com/security/CVE-2026-56412) | expat | 1 | Corrigido por build próprio |
| [CVE-2026-58010](https://ubuntu.com/security/CVE-2026-58010) * | glib2.0 | 1 | Corrigido por build próprio |
| [CVE-2026-58011](https://ubuntu.com/security/CVE-2026-58011) * | glib2.0 | 1 | Corrigido por build próprio |
| [CVE-2026-58012](https://ubuntu.com/security/CVE-2026-58012) * | glib2.0 | 1 | Corrigido por build próprio |
| [CVE-2026-58013](https://ubuntu.com/security/CVE-2026-58013) * | glib2.0 | 1 | Corrigido por build próprio |
| [CVE-2026-58014](https://ubuntu.com/security/CVE-2026-58014) * | glib2.0 | 1 | Corrigido por build próprio |
| [CVE-2026-58015](https://ubuntu.com/security/CVE-2026-58015) * | glib2.0 | 1 | Corrigido por build próprio |
| [CVE-2026-58016](https://ubuntu.com/security/CVE-2026-58016) * | glib2.0 | 1 | Corrigido por build próprio |
| [CVE-2026-66046](https://ubuntu.com/security/CVE-2026-66046) | expat | 1 | Corrigido por build próprio |
| [CVE-2026-72522](https://ubuntu.com/security/CVE-2026-72522) | expat | 1 | Corrigido por build próprio |
| [CVE-2026-76641](https://ubuntu.com/security/CVE-2026-76641) | expat | 1 | Corrigido por build próprio |
| [CVE-2026-76957](https://ubuntu.com/security/CVE-2026-76957) * | expat | 1 | Corrigido por build próprio |

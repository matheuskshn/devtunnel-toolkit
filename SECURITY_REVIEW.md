# Revisão de segurança

Atualização: 2026-09-12. **Riscos residuais aceitos para a próxima RC; o gate do Sonar permanece `ERROR`.**

## Situação da entrega

O commit `98edfeee9b4542e0bf373be9fda0997945a4fe65`, no
[PR #16](https://github.com/matheuskshn/devtunnel-toolkit/pull/16), passou em
44 checks, com cinco etapas de publicação corretamente ignoradas por ser PR.
O CI analisou o merge de teste `c5f14f2ffbb0794f0ae369a58ea59d1af103e69a`:

- [Docker](https://github.com/matheuskshn/devtunnel-toolkit/actions/runs/34632145641):
  builds e segurança das imagens legadas em AMD64 e ARM64 nativos.
- [Hub](https://github.com/matheuskshn/devtunnel-toolkit/actions/runs/34632145590):
  testes, integração, PostgreSQL, segurança e builds nativos.
- [Aceitação Ubuntu](https://github.com/matheuskshn/devtunnel-toolkit/actions/runs/34632145149):
  196 testes por arquitetura, smokes, Lintian e comparação de dez bibliotecas.

Esse CI não valida automaticamente as alterações posteriores descritas abaixo.
Após fechar a revisão, é necessário repetir os gates no commit definitivo.
Merge, nova RC e publicação real em registry permanecem separados da validação
local. O [histórico](SECURITY_BASELINE.md) conserva os resultados anteriores.

## Remediação atual

- Toolkit, Tinyproxy e OpenVPN ganharam healthchecks locais com falha fechada.
  O Toolkit consulta o Secret Service sem reativar um serviço que caiu; Tinyproxy
  usa `StatHost` interno; OpenVPN verifica TUN e o socket TCP/UDP configurado.
  Nenhum deles comprova autenticação, renovação de token ou relay remoto.
- O exemplo Kubernetes usa namespace dedicado, política de pod `restricted`
  e UID/GID/fsGroup 10001. A imagem mantém o padrão 1000, adicionando uma conta
  alternativa para o D-Bus. Volumes antigos exigem migração planejada; não há
  `chown` automático de dados de usuário.
- O parsing de SONAME do empacotador passou a ser linear, com teste de entrada
  de 1 MiB. Permanecem a verificação de símbolos, SONAME, hashes e inventário dpkg.
- Foram corrigidos apontamentos de `readonly`, expressões aninhadas, reexportação,
  parsing e apresentação de claims OIDC sem coerção de objetos para texto.
- `yaml` de desenvolvimento passou de 2.9.0 para 2.9.1. Não houve atualização
  forçada de major nem remoção de dependências essenciais para zerar contagens.

## SonarQube

Servidor local Community Build 26.9.0.129388 e Scanner 8.0.1.6346, fixados por
digest, projeto privado, rede interna e telemetria desabilitada. Apenas fontes
públicas selecionadas e LCOV foram montados no scanner. Tokens de análise
efêmeros foram revogados ao terminar cada execução.

O baseline é o commit `b410ddc2b8d05caeb3c51d6da23db853ddd45c5d`, datado em
2026-09-09T06:32:00Z, seguido da análise do trabalho atual com hash de conteúdo.
O período de código novo usa `SPECIFIC_ANALYSIS`. Uma execução preliminar com
a data de importação atual retornou zero linhas novas; esse resultado não foi
aceito como prova de gate. A repetição datada evidencia os achados novos.
Métricas do trabalho sem commit ainda precisam de confirmação no commit final.

O scan inicial desta revisão encontrou 60 apontamentos: 58 de manutenção,
dois classificados como vulnerabilidade e nenhum bug. Após a remediação,
restam **18 apontamentos: 16 de manutenção e dois classificados como
vulnerabilidade; nenhum bug ou hotspot**. O gate permanece `ERROR` por
17 achados considerados novos, não por cobertura ou duplicação:

| Medida final                       |                            Resultado |
| ---------------------------------- | -----------------------------------: |
| Testes automatizados               | 205 aprovados; zero falhas/ignorados |
| c8 - linhas                        |                               77,38% |
| c8 - ramos                         |                               86,50% |
| c8 - funções                       |                               83,87% |
| Sonar - cobertura combinada global |                                79,1% |
| Sonar - cobertura de código novo   |           80,6%; mínimo 80% aprovado |
| Sonar - duplicação nova            |               0%; máximo 3% aprovado |
| Sonar - achados novos              |            17; máximo zero reprovado |

A cobertura inclui os subprocessos reais da CLI e a execução real do empacotador
em um estágio nativo descartável. Seus pacotes foram novamente aprovados por
Lintian e abidiff. O empacotador atingiu 95,39% das linhas e o parser SONAME 100%.
O [procedimento reproduzível](images/hub/README.md#native-packager-coverage)
combina os dados V8 sem substituir resultados sintéticos por smokes ou excluir
fontes não executadas. A execução apenas de `test:coverage` não inclui o build
nativo e produz um percentual menor. A cobertura de CLI `serve`/worker ainda
não é completa, embora haja smokes separados de runtime.

Os dois pontos de
segurança pertencem ao empacotador, executado no estágio de build:

| Regra | Análise de contexto                                                                                            | Situação                                     |
| ----- | -------------------------------------------------------------------------------------------------------------- | -------------------------------------------- |
| S2612 | Modo 0644 de bibliotecas públicas: leitura compartilhada, escrita apenas pelo dono; não aplicado a credenciais | Revisado no código; continua aberto no Sonar |
| S4790 | MD5 no arquivo Debian `md5sums`, para inventário; autenticação de fontes e proveniência usam SHA-256           | Revisado no código; continua aberto no Sonar |

O [formato Debian](https://manpages.debian.org/unstable/dpkg-dev/deb-md5sums.5.en.html)
usa MD5 para verificação de arquivos, não autenticação criptográfica. Trocar seu
conteúdo por SHA-256 invalidaria o formato. Esses achados não demonstram vazamento
de segredos nem quebra da autenticação do Hub, mas a revisão contextual não
equivale a um status resolvido do scanner. Não houve supressão ou alteração
manual do resultado.

As combinações de tag e digest continuam deliberadas: a tag indica o canal do
Dependabot, e o digest fixa o conteúdo. Não remover a pinagem para satisfazer
S8431. As etapas de build separadas preservam cache e diagnóstico de regressões;
S7031 não indica pacote de runtime vulnerável. O frontend mantém captura de
falha do bootstrap por Promise, embora S7785 prefira top-level await.
Os 16 achados de manutenção são 12 S8431, três S7031 e um S7785.

## Aceite de risco residual para a próxima RC

Em 2026-09-12, o mantenedor aceitou exclusivamente para a próxima RC gerada a
partir do PR #16:

- os 18 apontamentos Sonar descritos acima, incluindo os dois classificados
  como vulnerabilidade;
- KSV-0125 no manifesto Kubernetes de exemplo;
- as 48 CVEs / 63 ocorrências do Hub, incluindo as nove pendências nativas e
  os CVEs de zlib e GStreamer acrescentados pela base atualizada do Trivy;
- as ocorrências sem `FixedVersion` nas demais quatro imagens, conforme o
  inventário abaixo referenciado.

O aceite não transforma achados em corrigidos, não cria um falso gate verde e
não reduz a severidade indicada por cada fonte. O Sonar continua `ERROR`, não há
VEX ou supressão no Trivy e nenhuma política de registry foi relaxada. A decisão
expira com essa RC: uma versão posterior exige nova varredura, revisão das
correções disponibilizadas pelos fornecedores e novo aceite explícito para o
que ainda permanecer aberto. O aceite não autoriza deploy ou alteração no ACA.

## Validação local final

- Hub padrão e UID 10001: sessões, HTTP/SOCKS5, D-Bus/keyrings, permissões,
  isolamento, console, armazenamento em arquivos, locking, restart e recriação.
- PostgreSQL: CLI, criptografia, contenção, SIGKILL/substituição, persistência
  do console e falha fechada na perda de conexão. Sem acesso a banco externo.
- Toolkit/Tinyproxy/OpenVPN: probes reais, controles negativos e testes HTTP/TLS
  sintéticos. OpenVPN passou em TCP e UDP com preservação da PKI e reinício.
- Cinco pacotes sem avisos/erros Lintian; dez comparações sem incompatibilidade
  ELF. Probes Expat char/wchar, GLib e libelf aprovados na imagem final.
- Semgrep: zero achados em 119 arquivos com 107 regras; uma execução explícita
  adicional cobriu a automação e o novo parser, com zero achados.
- Gitleaks: zero achados na árvore e no histórico. `npm audit`: zero; dependências
  diretas atualizadas nas linhas adotadas. Actionlint e verificação pública aprovados.

Essas evidências são locais AMD64 para o delta atual. A validação anterior do CI
em ARM64 está registrada no início, sem confundir os dois estados.

## CVEs e configuração

A base atualizada do Trivy registra **48 CVEs / 63 ocorrências no Hub**:
47 MEDIUM e 16 LOW, sem HIGH/CRITICAL pela classificação Ubuntu. Nenhuma das
ocorrências oferece `FixedVersion` nessa base. Isso não significa que cada uma
seja explorável nem que todas estejam corrigidas. A matriz separa correções
próprias, componentes ausentes, mitigações condicionais e pendências.

Dois CVEs foram acrescentados pela atualização do banco, sem mudança do código:

- [zlib CVE-2026-85091](https://ubuntu.com/security/CVE-2026-85091): uma ocorrência;
  a Canonical registra correção adiada e ausência de patch upstream validado.
- [GStreamer CVE-2026-85150](https://ubuntu.com/security/CVE-2026-85150): duas
  ocorrências; a biblioteca RTSP está instalada. A Canonical informa que o commit
  exato ainda não é público. Retirar os pacotes pelo APT também retira o keyring.

Ambos têm CVSS HIGH na fonte, apesar da prioridade Ubuntu MEDIUM usada pelo
scanner. Não usar o gate HIGH/CRITICAL como afirmação de risco zero. Não foram
aplicados patches especulativos nem supressões para esses casos.

O Trivy de configuração não acusa mais DS-0026, DS-0017, DS-0002 ou os alertas
de UID/GID/namespace do exemplo Kubernetes. Permanece KSV-0125: a política
[padrão do scanner](https://github.com/aquasecurity/trivy-checks/blob/main/checks/kubernetes/uses_untrusted_registry.rego)
confia em registries de nuvem específicos e não inclui GHCR. O exemplo usa o
repositório público real; cada implantação deve definir registries/repositórios
permitidos e aplicar sua política de admissão. Não foi inventado um endereço de
registry confiável nem ampliada silenciosamente a política do scanner.

Consulte os [resultados das cinco imagens](images/IMAGE_REMEDIATION.md) e a
[matriz nativa](images/hub/UBUNTU_CVE_REMEDIATION.md).

## Critérios para liberar a RC

1. Concluir os testes e a revisão do delta, incluindo permissões e persistência.
2. Decisão explícita sobre as exceções justificadas e registrada para a próxima
   RC. O gate permanece `ERROR` e não foi apresentado como aprovado.
3. Revisar o conteúdo público, fazer commit/push e repetir o CI nativo.
4. Revisar/mesclar o PR e a proposta do Release Please; verificar a publicação,
   as duas arquiteturas, proveniência, SBOM, digests e pull da nova RC.

Mudanças de infraestrutura e atualização de instalações existentes não fazem
parte desta revisão. O OpenVPN exige inicialização de rede separada e migração
explícita da PKI para UID/GID 10001, conforme o [README](README.md).

# Lições Aprendidas — Do Fase 1 ao Pipeline Verde

Este documento existe porque implementar um pipeline CI/CD no papel (planejamento, revisão adversarial, código) é uma coisa; **rodar de verdade contra infraestrutura real** é outra. Nenhum dos bugs abaixo foi pego em revisão de código ou de plano — todos só apareceram na primeira execução real, contra Docker, Jenkins, SonarQube e o NVD de verdade. Esse é o objetivo deste documento: registrar o que quebrou, por que, como foi corrigido, e — mais importante — como detectar ou evitar isso mais cedo da próxima vez.

## Como usar este documento

Cada seção segue o formato: **Sintoma → Causa raiz → Solução → Como evitar/detectar cedo**. No fim, uma checklist consolidada pra rodar *antes* de subir um ambiente do zero, destilada de toda essa dor.

⚠️ **Aviso importante:** este PoC roda 100% local, num único host Docker, sem rede corporativa, sem proxy, sem firewall interno, sem certificados reais, sem Windows real. Vários dos itens abaixo (webhook real, TLS, rede) teriam causas e soluções **diferentes** num ambiente corporativo. Isso está sinalizado item a item.

---

## Parte 1 — Recapitulação do Roadmap (Fase 1 a 6)

| Fase | Entregável | Onde está documentado |
|---|---|---|
| 1 | Infraestrutura Docker Compose (Jenkins, SonarQube, Nexus, Vault) | `cicd-poc/README.md` — Fase 2 Manual Setup |
| 2 | CI mínima — build, test, publish no Nexus | `cicd-poc/README.md` — Fase 2 Manual Setup |
| 3 | Quality Gates — SonarQube, OWASP Dependency-Check, Semgrep | `cicd-poc/README.md` — Fase 3 Manual Setup |
| 4 | Empacotamento e banco — SQL Server real, Liquibase, checksum SHA256, semver | `cicd-poc/README.md` — Fase 4 Manual Setup |
| 5 | CD — Deploy stage via Ansible/systemd/SSH no `apphost`; IIS/WinRM documentado | `cicd-poc/README.md` — Fase 5 Manual Setup, `docs/cd-windows-iis.md` |
| 6 | Polimento — trigger automático (pollSCM), Shared Library Jenkins | `cicd-poc/README.md` — Fase 6 Manual Setup, `docs/jenkins-windows-agent.md` |

O pipeline final tem **10 stages**: Restore → Database Migration → SonarQube Analysis → SonarQube Quality Gate → OWASP Dependency-Check → Semgrep → Publish → Archive → Upload to Nexus → Deploy.

Cada fase acima passou por: plano do Architect → revisão adversarial (verificação de flags/sintaxe de terceiros contra documentação real) → implementação → revisão de código (coerência, qualidade, segurança). Isso pegou vários bugs *antes* de qualquer execução real (ex.: flag `--failOnCVSS` errada, ordem de argumentos do Liquibase, quoting de `Environment=` no systemd). Os bugs documentados abaixo são os que **passaram** por todo esse processo e só apareceram em runtime — a categoria de erro que nenhuma revisão estática pega, porque exige efetivamente subir containers e rodar o pipeline contra sistemas vivos.

---

## Parte 2 — Bugs Reais, um por um

### 2.1 — Senha inicial do Jenkins perdida (wizard já tinha rodado)

- **Sintoma:** `docker exec jenkins cat /var/jenkins_home/secrets/initialAdminPassword` → `No such file or directory`.
- **Causa raiz:** esse arquivo só existe **antes** do setup wizard rodar; uma vez que o admin é criado pela UI, o arquivo é apagado. Ambiente já tinha passado por esse processo numa sessão anterior.
- **Solução:** reset via *init script* Groovy (`/var/jenkins_home/init.groovy.d/*.groovy`), que roda automaticamente no boot do Jenkins:
  ```groovy
  import jenkins.model.*
  import hudson.security.*
  def instance = Jenkins.get()
  def realm = new HudsonPrivateSecurityRealm(false)
  realm.createAccount("admin", "SENHA_NOVA")
  instance.setSecurityRealm(realm)
  def strategy = new FullControlOnceLoggedInAuthorizationStrategy()
  strategy.setAllowAnonymousRead(false)
  instance.setAuthorizationStrategy(strategy)
  instance.save()
  ```
  Copiar pro container via `docker cp` (não `docker exec ... <<EOF`, heredoc multi-linha via `docker exec bash -c` quebra com indentação de terminal), reiniciar, depois **remover o script** pra não resetar a cada boot.
- **Como evitar/detectar cedo:** anotar a senha do admin assim que criada no wizard (num vault/gerenciador de senhas — nunca em texto puro em lugar exposto). Em ambiente corporativo, login normalmente é via LDAP/SSO, não senha local — esse problema específico não existiria lá.

### 2.2 — SonarQube: Global Token vs User Token

- **Sintoma:** dúvida de qual tipo de token gerar pro Jenkins.
- **Causa raiz:** SonarQube tem dois tipos: **User Token** (atrelado à conta pessoal — morre se a conta for desativada) e **Global/Global Analysis Token** (não atrelado a usuário, feito pra automação).
- **Solução:** usar **Global Analysis Token** para credenciais de CI/CD.
- **Como evitar/detectar cedo:** regra geral — qualquer credencial usada por automação (não por humano) deve ser o tipo "não pessoal" da ferramenta, se existir. Em ambientes corporativos com SSO, isso normalmente é uma *service account* dedicada.

### 2.3 — `apphost` em crash loop (exit 255, sem log nenhum)

- **Sintoma:** container reiniciando a cada ~1s. `docker compose logs` e `docker compose up` (foreground) não mostravam **nenhuma** saída — nem erro, nem log de boot do systemd.
- **Causa raiz:** em hosts com **cgroup v2** (padrão em kernels modernos), o Docker já concede a containers `privileged: true` uma **namespace de cgroup privada** automaticamente. O compose tinha um bind-mount explícito `/sys/fs/cgroup:/sys/fs/cgroup:rw` do host — isso conflita com a namespace privada automática: o systemd dentro do container não consegue tomar posse da árvore de cgroup e morre **antes** de conseguir inicializar qualquer sistema de log.
- **Solução:** adicionar `cgroup: "host"` ao serviço no `docker-compose.yml` (chave confirmada contra a [Compose Spec](https://docs.docker.com/reference/compose-file/services/) — não é `cgroupns`, é `cgroup`).
- **Como evitar/detectar cedo:** ao rodar systemd-em-container pela primeira vez num host novo, checar `cat /proc/sys/vm/max_map_count` (não foi a causa aqui, mas é o outro suspeito clássico) e, se usar `privileged: true` + bind-mount de cgroup, adicionar `cgroup: "host"` de saída — é a combinação correta e estável em cgroup v2. Testar o boot **antes** de escrever qualquer lógica de deploy em cima do container.

### 2.4 — `apphost` bootava, mas SSH recusava login pra sempre ("System is booting up")

- **Sintoma:** `systemctl is-system-running` retornava `running`, mas SSH recusava login não-root indefinidamente com a mensagem de "sistema ainda inicializando".
- **Causa raiz:** `systemd-user-sessions.service` é quem remove o arquivo `/run/nologin` ao atingir `multi-user.target`. É uma unidade `static` (sem seção `[Install]`) — só roda se outra unidade a referenciar via `Wants=`, normalmente um symlink em `multi-user.target.wants/` criado pelo `dpkg` na instalação do pacote `systemd`. **Imagens Debian minimalistas não geram esse symlink no build.**
- **Solução:** criar o symlink explicitamente no Dockerfile (não em runtime — se não, some no próximo restart):
  ```dockerfile
  RUN ln -sf /lib/systemd/system/systemd-user-sessions.service \
      /etc/systemd/system/multi-user.target.wants/systemd-user-sessions.service
  ```
  Bug documentado publicamente para a mesma classe de imagem: [CentOS-Dockerfiles#173](https://github.com/CentOS/CentOS-Dockerfiles/issues/173).
- **Como evitar/detectar cedo:** ao usar qualquer imagem base "systemd minimalista" (não uma distro completa), assumir que `dpkg` triggers de pós-instalação podem ter sido pulados no build — testar SSH/login explicitamente antes de seguir, não só `is-system-running`.

### 2.5 — `waitForQualityGate` travado em `PENDING` até o timeout

- **Sintoma:** um único log `status is 'PENDING'`, depois silêncio total por 5 minutos até `Timeout has been exceeded` — mesmo com o SonarQube processando a análise em ~1.5s no backend (confirmado via `docker compose logs sonarqube`).
- **Causa raiz:** `waitForQualityGate` **não faz polling repetido**. Ele espera um **webhook** que o SonarQube manda de volta pro Jenkins quando o Compute Engine termina. Sem esse webhook configurado, o step fica mudo até estourar o timeout.
- **Solução:** SonarQube → **Administration → Configuration → Webhooks → Create**: Name = `Jenkins`, URL = `http://jenkins:8080/sonarqube-webhook/` (⚠️ barra final obrigatória; nome do container, não `localhost`, já que quem chama é o container do Sonar).
- **Como evitar/detectar cedo:** sempre que usar `waitForQualityGate`, configurar o webhook **antes** do primeiro build — é passo obrigatório, não opcional, mesmo que a documentação do plugin não deixe isso óbvio de cara. Em produção, a URL seria a URL pública/interna real do Jenkins, não `http://jenkins:8080`.

### 2.6 — "Global Tool Configuration" não existe mais com esse nome

- **Sintoma:** seguindo a doc do plugin ("acesse Global Tool Configuration"), a seção não aparece em lugar nenhum.
- **Causa raiz:** versões recentes do Jenkins (a partir de ~2.400+) renomearam essa página para **"Tools"**, dentro de Manage Jenkins.
- **Solução:** Manage Jenkins → **Tools** (não mais "Global Tool Configuration").
- **Como evitar/detectar cedo:** documentação de plugins frequentemente fica desatualizada em relação à UI atual do core do Jenkins — se um menu documentado não existe, procurar variações de nome antes de assumir que algo está quebrado.

### 2.7 — OWASP Dependency-Check: nome bate, mas resolve `null`

- **Sintoma:** `ERROR: Couldn't find any executable in "null"`.
- **Causa raiz:** a ferramenta foi criada em Tools com o nome certo (`dependency-check`), mas **sem nenhum instalador configurado** (`<installers/>` vazio no XML). "Install automatically" precisa ser marcado E um instalador (`Add Installer → Extract *.zip/*.tar.gz`) precisa ser adicionado — criar só o nome não é suficiente.
- **Solução:** Tools → Dependency-Check installations → editar a entrada → marcar **Install automatically** → **Add Installer** → **Extract *.zip/*.tar.gz** → Download URL = release oficial do GitHub (`https://github.com/dependency-check/DependencyCheck/releases/download/vX.Y.Z/dependency-check-X.Y.Z-release.zip`) → Subdirectory = `dependency-check`.
- **Como evitar/detectar cedo:** depois de configurar qualquer "Tool" no Jenkins com auto-instalação, checar o XML salvo em `$JENKINS_HOME/<descriptor>.xml` antes de assumir que está pronto — a UI não deixa óbvio quando um instalador não foi de fato anexado.

### 2.8 — Typo no nome da ferramenta (`dependency_check` vs `dependency-check`)

- **Sintoma:** voltou pro erro original ("No installation dependency-check found"), depois de já ter resolvido o problema anterior.
- **Causa raiz:** ao recriar a configuração, o nome foi digitado com **underscore** em vez de **hífen** — o Jenkinsfile referencia `odcInstallation: 'dependency-check'` (hífen) exato.
- **Solução:** corrigir o campo Name para bater caractere-por-caractere com o que o Jenkinsfile espera.
- **Como evitar/detectar cedo:** todo nome/ID referenciado em pipeline-as-code deve ser tratado como uma chave exata (case-sensitive, sem tolerância a variação) — copiar/colar em vez de digitar de memória, e no primeiro erro deste tipo, sempre verificar a fonte de verdade (o XML de config ou a API) em vez de reler a UI visualmente.

### 2.9 — `apphost` sem `unzip` — Ansible `unarchive` falha

- **Sintoma:** `Failed to find handler for ".../BnpPoc.Api-....zip". Make sure the required command to extract the file is installed` — `tar` tentou vários formatos (bzip2, gzip, xz, zstd) e por fim precisou de `unzip`/`zipinfo`, ausentes na imagem.
- **Causa raiz:** o módulo `ansible.builtin.unarchive` do Ansible, para arquivos `.zip`, depende do binário `unzip` no **managed node** (não no control node) — a imagem do `apphost` só tinha `openssh-server curl ca-certificates libicu-dev python3 sudo`.
- **Solução:** adicionar `unzip` ao `apt-get install` do Dockerfile do `apphost`.
- **Como evitar/detectar cedo:** ao usar `unarchive` (ou qualquer módulo Ansible que dependa de binários externos no managed node — `unarchive`, `archive`, `get_url` com checksum, etc.), checar a documentação do módulo pela seção "Requirements" e garantir que os binários existem na imagem alvo **antes** do primeiro teste real.

### 2.10 — Quality Gate reprovou de verdade (não é bug — é o gate funcionando)

- **Sintoma:** `ERROR: Pipeline aborted due to quality gate failure: ERROR`.
- **Causa raiz:** **não é bug.** É `abortPipeline: true` funcionando exatamente como desenhado (decisão da Fase 3: "Quality Gate bloqueante"). Diagnóstico via API (`/api/qualitygates/project_status?projectKey=...`) mostrou a condição exata: `new_violations actualValue=1 > errorThreshold=0` — um code smell real introduzido no `apphost/Dockerfile` (achado do próprio sensor IaC Docker do SonarQube: "Merge this RUN instruction with the consecutive ones").
- **Solução:** corrigir o smell (merge das instruções `RUN`), commitar, deixar a análise rodar de novo.
- **Como evitar/detectar cedo:** quando um Quality Gate reprova, **não assumir que é bug do pipeline** — usar a API do SonarQube (`/api/qualitygates/project_status`) pra ver a condição exata que falhou antes de investigar qualquer outra coisa. É a fonte de verdade mais rápida, mais confiável que ler o dashboard visualmente.

### 2.11 — OWASP CLI 12.2.2 não cria o diretório `--out` sozinho

- **Sintoma:** `Invalid 'out' argument: 'dependency-check-report' - path does not exist`.
- **Causa raiz:** versões mais novas do CLI standalone do dependency-check não criam automaticamente o diretório de saída (`--out`), diferente do comportamento assumido ao planejar o Jenkinsfile.
- **Solução:** `sh 'mkdir -p dependency-check-report'` antes do step `dependencyCheck`.
- **Como evitar/detectar cedo:** comportamento de CLIs de terceiros muda entre versões maiores — nunca assumir que uma flag documentada numa versão antiga se comporta igual na versão que a instalação automática baixou (que é sempre a mais recente disponível, salvo pin explícito).

### 2.12 — Rate-limit do NVD sem API key + chave nova precisa de confirmação por e-mail

- **Sintoma:** primeira falha — `NvdApiException: NVD Returned Status Code: 429` no meio do download de ~363 mil registros. Segunda falha (já com chave adicionada) — `Invalid API Key: 1E080-*****-E670D`.
- **Causa raiz:** (a) sem API key, o rate-limit do NVD é 5 requisições/30s — inviável pra baixar a base completa de CVEs na primeira vez; (b) uma chave de API do NVD recém-solicitada **não fica ativa na hora** — a NVD manda um e-mail com link de confirmação de uso único, e só depois de clicar nesse link a chave passa a funcionar.
- **Solução:** (a) pedir chave gratuita em nvd.nist.gov/developers/request-an-api-key; (b) usar o parâmetro **dedicado** do plugin, `nvdCredentialsId: 'nvd-api-key'` (não interpolar o segredo dentro de `additionalArguments` via Groovy string — isso contornaria o masking padrão do Jenkins); (c) **confirmar o e-mail de ativação** antes de esperar a chave funcionar.
- **Como evitar/detectar cedo:** pedir a chave NVD **no início** do projeto, não quando o pipeline já está quase funcionando — o atraso de ativação (minutos a horas) só é percebido tarde se deixado pro fim. Além disso: essa falha específica não trava o pipeline inteiro (ver item 2.13) — não é bloqueante enquanto se espera a ativação.

### 2.13 — Falha "suave" do OWASP não trava o pipeline (comportamento, não bug)

- **Observação importante, não um bug:** o step `dependencyCheck` do Jenkins, ao falhar (CVE crítico encontrado, ou erro de atualização do NVD), **marca o build como FAILURE mas não aborta a execução** — diferente do `SonarQube Quality Gate`, que usa `abortPipeline: true` e realmente para tudo. Isso foi confirmado repetidas vezes nesta sessão: com o OWASP falhando, `Semgrep`, `Publish`, `Archive`, `Upload to Nexus` e `Deploy` continuaram rodando normalmente, e o deploy real aconteceu (`PLAY RECAP: failed=0`) mesmo com o selo geral do build vermelho.
- **Como usar isso:** ao depurar "o pipeline falhou", primeiro checar **se as stages que dependiam do sucesso realmente rodaram** (procurar "skipped due to earlier failure(s)" no log) — se elas rodaram, a falha é de uma stage não-bloqueante (como o OWASP) e pode ser corrigida sem urgência, sem impedir o deploy.

### 2.14 — Credenciais Jenkins: IDs errados, tipos errados, chave pública em campo de chave privada

- **Sintomas ao longo da sessão:** `Could not find credentials entry with ID 'apphost-ssh'` (nunca criada), `Current credentials does not exists` (`nvd-api-key` — criação "não salvou" de verdade), e a chave **pública** colada no campo de chave **privada** da credencial SSH.
- **Causa raiz comum:** confiar em leitura visual da UI de credenciais é frágil — nomes/IDs digitados errado, criação que parece ter salvo mas não salvou, confusão entre os dois arquivos gerados por `ssh-keygen` (`chave` = privada, vai no Jenkins; `chave.pub` = pública, vai no `authorized_keys` do servidor de destino).
- **Solução:** verificar credenciais via **API do Jenkins** (`curl -u admin:senha ".../credentials/store/system/domain/_/api/json?depth=1"`), não pela UI — é a fonte de verdade real, rápida de checar, e evita erro de leitura humana.
- **Como evitar/detectar cedo:** ao criar qualquer credencial: (1) copiar/colar o ID em vez de digitar de memória; (2) depois de salvar, sempre validar via API antes de assumir que está pronta; (3) lembrar sempre qual arquivo é qual num par de chaves SSH — a regra mnemônica: **a privada nunca sai da tua máquina/do cofre de segredos; a pública é a que se distribui**.

### 2.15 — Jenkinsfile é lido do Git remoto, não do disco local

- **Sintoma:** um fix aplicado localmente no `Jenkinsfile` (ex.: `mkdir -p dependency-check-report`) não teve efeito nenhum no build seguinte — o erro se repetiu idêntico.
- **Causa raiz:** o job Jenkins usa "Pipeline script from SCM" — ele faz `git clone`/`checkout` do repositório remoto a cada execução. Mudanças no disco local (onde o Claude Code estava editando) só existem no working tree local até serem **commitadas e pushadas**.
- **Solução:** todo fix no `Jenkinsfile` precisa do ciclo completo `git add && git commit && git push` antes de disparar um novo build para validá-lo. (Fixes em arquivos que **não** fazem parte do checkout do pipeline propriamente dito — como o `apphost/Dockerfile`, usado só para `docker compose build apphost` localmente — não precisam desse ciclo, só do rebuild local.)
- **Como evitar/detectar cedo:** sempre que um fix "não fizer efeito" num sistema baseado em SCM, a primeira pergunta é "isso está commitado E pushado?", antes de qualquer outra hipótese de debug.

### 2.16 — `pollSCM` não dispara instantaneamente

- **Observação, não bug:** depois de configurado, `triggers { pollSCM('H/2 * * * *') }` não reage a um `git push` na hora — o `H` (hash) distribui o agendamento real dentro da janela de ~2 minutos (pra balancear carga entre jobs no mesmo Jenkins), e a diretiva `triggers` só passa a valer depois do **primeiro build manual** que a registra.
- **Como verificar:** o job tem um link **"Git Polling Log"** na barra lateral, que mostra exatamente quando o último poll rodou e se encontrou mudança — é a forma correta de diagnosticar "por que não disparou ainda", em vez de assumir que travou.
- **Em ambiente corporativo:** com o Jenkins acessível publicamente (ou pela rede interna), o padrão correto é um **webhook real** do provedor Git (`githubPush()` ou plugin equivalente) — dispara instantaneamente, sem o atraso do polling. Isso está documentado em `docs/jenkins-windows-agent.md` como alternativa não-executada neste PoC (Jenkins local não tem URL pública alcançável pelo GitHub).

---

## Parte 3 — O Que Seria Diferente em Ambiente Corporativo

Este PoC roda isolado, num único host, com decisões deliberadamente simplificadas pra viabilizar o teste local. Numa implantação real, pelo menos os pontos abaixo mudam:

- **Webhook real em vez de `pollSCM`** — Jenkins corporativo normalmente tem URL interna alcançável pela rede/VPN da empresa; webhook do GitHub/GitLab dispara build instantâneo.
- **TLS real** — `TrustServerCertificate=True` (usado neste PoC pro SQL Server) é aceitável só localmente; produção exige certificado assinado por CA confiável.
- **Login com privilégio mínimo** — a app conecta como `sa` no SQL Server aqui; produção exige um login dedicado, escopado só ao banco da aplicação.
- **Segredos fora de arquivo versionado** — a connection string com senha em `appsettings.json` é aceitável só como convenção de PoC (documentado como "não usar em produção"); produção usa Vault/CyberArk/Azure Key Vault/AWS Secrets Manager, nunca hardcoded.
- **NVD via mirror/proxy corporativo ou ferramenta comercial** — muitas empresas usam Nexus IQ, Black Duck, ou um mirror interno do NVD, evitando o rate-limit público inteiramente.
- **Agente Windows real** — para builds .NET Framework, é necessário um agente Jenkins real rodando Windows (físico ou VM), documentado em `docs/jenkins-windows-agent.md` mas nunca executável neste ambiente Linux.
- **`sudo` sem senha e usuário `deploy`** — conveniência de PoC; produção usa contas de serviço com permissões explicitamente escopadas (não `NOPASSWD:ALL`).
- **systemd-em-container** — um artifício válido pra simular um "app host" localmente; em produção o alvo seria uma VM/servidor físico real rodando systemd nativamente (Linux) ou IIS (Windows) — sem nenhum dos problemas de cgroup/namespace descritos acima, que são exclusivos de rodar systemd *dentro* de um container Docker.

---

## Parte 4 — Checklist Pré-Voo (destilada de toda essa sessão)

Antes de considerar um ambiente CI/CD "pronto para testar", checar:

- [ ] Senha do admin de cada ferramenta (Jenkins, SonarQube, Nexus) anotada com segurança, **não** deixada no padrão default além do primeiro login.
- [ ] Token de automação gerado com o tipo certo (Global/Service Account, não pessoal).
- [ ] Todas as credenciais do pipeline criadas **e verificadas via API** (não só pela UI) — IDs batendo exatamente com o que o pipeline-as-code referencia.
- [ ] Se systemd roda dentro de um container: `cgroup: "host"` configurado, e boot + SSH testados manualmente **antes** de escrever lógica de deploy em cima.
- [ ] Webhook do SonarQube → Jenkins configurado, se o pipeline usa `waitForQualityGate`.
- [ ] Toda ferramenta com "Tools"/instalação automática no Jenkins: instalador de fato anexado (checar XML/API, não só o nome salvo).
- [ ] Toda dependência de binário externo (unzip, tar, etc.) de módulos Ansible/scripts verificada contra a documentação do módulo, na imagem alvo real.
- [ ] Chave de API externa (NVD ou qualquer outra) solicitada com antecedência — ativação por e-mail pode levar tempo.
- [ ] Confirmado que mudanças em arquivos versionados por SCM (Jenkinsfile) foram commitadas **e pushadas** antes de cada teste.
- [ ] Ao investigar "pipeline falhou": primeiro checar se as stages downstream rodaram (`skipped due to earlier failure(s)` ou não) — decide se é falha bloqueante ou suave.

# CI/CD POC — BNP Paribas — Fase 1

Toolchain completo de CI/CD para aplicações .NET targetting IIS (Windows), com todos os serviços rodando em Linux via Docker Compose.

---

## Pré-requisitos

- **Docker** 24+ e **Docker Compose v2** (`docker compose` — sem hífen)
- RAM mínima: **8 GB** disponíveis para o host Docker
- Sistema operacional do host: Linux ou WSL2 (Windows)

### Pré-requisito do SonarQube (Elasticsearch)

O SonarQube usa Elasticsearch internamente, que exige um valor mínimo de `vm.max_map_count`. Execute **antes** de subir os serviços:

```bash
sudo sysctl -w vm.max_map_count=262144
```

Para tornar a configuração persistente entre reboots, adicione ao `/etc/sysctl.conf`:

```
vm.max_map_count=262144
```

---

## Build e inicialização

```bash
cd cicd-poc
docker compose build
docker compose up -d
docker compose ps
```

---

## Acesso aos serviços

| Serviço    | URL                    | Credenciais padrão                        |
|------------|------------------------|-------------------------------------------|
| Jenkins    | http://localhost:8080  | Ver senha inicial abaixo                  |
| SonarQube  | http://localhost:9000  | admin / admin                             |
| Nexus      | http://localhost:8081  | admin / ver admin.password abaixo         |
| Vault      | http://localhost:8200  | Token: `root`                             |
| SQL Server | localhost:1433         | sa / BnpP0c!Local                         |

---

## Senha inicial do Jenkins

Na primeira inicialização, o Jenkins gera uma senha aleatória. Recupere-a com:

```bash
docker exec jenkins cat /var/jenkins_home/secrets/initialAdminPassword
```

Acesse http://localhost:8080, cole a senha e siga o wizard de configuração.

---

## Senha inicial do Nexus

O Nexus gera uma senha aleatória para o usuário `admin` na primeira execução. Recupere-a com:

```bash
docker exec nexus cat /nexus-data/admin.password
```

---

## Roadmap

| Fase | Descrição                                                                                   |
|------|---------------------------------------------------------------------------------------------|
| 1 ✅ | **Infraestrutura** — Jenkins, SonarQube, Postgres, Nexus, Vault via Docker Compose          |
| 2 ✅ | **CI mínima** — Jenkinsfile com build, test, publicação do artefato no Nexus                |
| 3 ✅ | **Quality Gates** — SonarQube (coverage bloqueante), OWASP Dependency-Check, Semgrep OSS    |
| 4 ✅ | **Empacotamento e banco** — checksum SHA256, versionamento semântico, Liquibase/SQL Server  |
| 5 ✅ | **CD** — estágio Deploy (Ansible + systemd via SSH) no app host `apphost`; IIS/WinRM documentado |
| 6 🔜 | **Polimento** — webhook Git, shared library Jenkins, agente Windows para .NET Framework      |

---

## Fase 2 — Manual Setup

Execute os passos abaixo **uma única vez** antes de rodar o pipeline pela primeira vez.

### Passo 1 — Rebuild da imagem Jenkins

O Dockerfile agora inclui `zip`. Reconstrua e suba o container:

```bash
cd cicd-poc
docker compose build jenkins
docker compose up -d jenkins
```

### Passo 2 — Verificar acesso à internet (NuGet) pelo container Jenkins

```bash
docker exec jenkins curl -s https://api.nuget.org/v3/index.json | head -c 100
```

Saída esperada: JSON começando com `{"version":`. Se retornar vazio ou erro de conexão, o container não tem acesso à internet — corrija a configuração de rede Docker antes de continuar.

### Passo 3 — Criar repositório raw no Nexus

Via UI do Nexus (`http://localhost:8081`):
1. Login como `admin` (senha: `docker exec nexus cat /nexus-data/admin.password`)
2. Administration → Repository → Repositories → **Create repository**
3. Recipe: **raw (hosted)**
4. Name: `dotnet-artifacts` | Online: marcado | Write policy: **Allow redeploy**
5. Save

Ou via REST API:
```bash
curl -u admin:PASSWORD -X POST "http://localhost:8081/service/rest/v1/repositories/raw/hosted" \
  -H "Content-Type: application/json" \
  -d '{"name":"dotnet-artifacts","online":true,"storage":{"blobStoreName":"default","strictContentTypeValidation":false,"writePolicy":"ALLOW"}}'
```

> `"writePolicy":"ALLOW"` permite re-upload do mesmo artefato sem HTTP 400 (importante para re-runs do mesmo `BUILD_NUMBER` em testes).

### Passo 4 — Adicionar credencial do Nexus no Jenkins

Jenkins UI → Manage Jenkins → Credentials → System → Global credentials → **Add Credentials**:
- Kind: **Username with password**
- Username: `admin` (ou usuário dedicado do Nexus)
- Password: senha do admin do Nexus
- ID: `nexus-credentials`
- Description: `Nexus OSS admin`

> A senha não deve conter `:`, `@` ou espaços — `curl -u user:pass` passa as credenciais na URL sem escaping de shell.

### Passo 5 — Adicionar credencial SSH do GitHub no Jenkins

Jenkins UI → Manage Jenkins → Credentials → System → Global credentials → **Add Credentials**:
- Kind: **SSH Username with private key**
- Username: `git`
- Private key: cole a chave privada registrada em `github.com/guimeiradev`
- ID: `github-ssh`
- Description: `GitHub SSH key`

### Passo 6 — Criar o job de Pipeline

Jenkins UI → New Item:
- Name: `BnpPoc-CI`
- Type: **Pipeline**
- Definition: **Pipeline script from SCM**
- SCM: **Git**
- Repository URL: `git@github.com:guimeiradev/POC-CI-CD-BNP-PARIBAS.git`
- Credentials: `github-ssh`
- Branch: `*/main`
- Script Path: `Jenkinsfile`
- Save → **Build Now**

### Verificação pós-run

Após o pipeline concluir com sucesso, confirme que o artefato chegou ao Nexus:

```bash
curl -s -u admin:PASSWORD \
  "http://localhost:8081/service/rest/v1/components?repository=dotnet-artifacts" \
  | grep BnpPoc.Api
```

Saída esperada: não vazia, contendo `BnpPoc.Api`.

## Fase 3 — Manual Setup

Execute os passos abaixo **uma única vez** antes de rodar o pipeline com Quality Gates.

### Passo 1 — Rebuild da imagem Jenkins

O Dockerfile agora inclui dotnet-sonarscanner, reportgenerator e semgrep:

```bash
cd cicd-poc
docker compose build jenkins
docker compose up -d jenkins
```

### Passo 2 — Gerar token de autenticação no SonarQube

1. Acesse http://localhost:9000, faça login como `admin`
2. Menu superior direito → **My Account** → aba **Security**
3. Em **Generate Tokens**: Name = `jenkins-ci`, Type = `Global Analysis Token`, Expiration = `No expiration`
4. Clique **Generate** → copie o token gerado imediatamente (ele só aparece uma vez)

### Passo 3 — Adicionar credencial do SonarQube no Jenkins

Jenkins UI → Manage Jenkins → Credentials → System → Global credentials → **Add Credentials**:
- Kind: **Secret text**
- Secret: (cole o token gerado no Passo 2)
- ID: `sonar-token`
- Description: `SonarQube Global Analysis Token`

### Passo 4 — Configurar servidor SonarQube no Jenkins

Jenkins UI → Manage Jenkins → **Configure System** → seção **SonarQube servers**:
- Marque **Enable injection of SonarQube server configuration as build environment variables**
- Clique **Add SonarQube**
- Name: `SonarQube` ← este nome deve ser idêntico ao usado em `withSonarQubeEnv('SonarQube')` no Jenkinsfile
- Server URL: `http://sonarqube:9000`
- Server authentication token: selecione a credencial `sonar-token`
- Clique **Save**

### Passo 5 — Configurar Quality Gate no SonarQube (threshold de cobertura)

O threshold de cobertura é definido **exclusivamente** na UI do SonarQube — não existe nenhum valor hardcoded no Jenkinsfile ou em qualquer arquivo do repositório.

1. Acesse http://localhost:9000 → **Quality Gates** (menu superior)
2. Clique **Create** → Name: `BnpPoc Quality Gate`
3. Clique **Add Condition**:
   - Metric: `Coverage`
   - Operator: `is less than`
   - Value: `(escolha o percentual mínimo, ex: 80)`
4. Clique **Save**
5. Para associar ao projeto: acesse **Projects** → selecione `BnpPoc.Api` (criado automaticamente na primeira análise) → aba **Project Settings** → **Quality Gate** → selecione `BnpPoc Quality Gate`

> Para alterar o threshold depois: repita o Passo 5.3, mude o valor e salve. O próximo build usará o novo valor automaticamente.

### Passo 6 — Configurar OWASP Dependency-Check no Jenkins

Jenkins UI → Manage Jenkins → **Global Tool Configuration** → seção **Dependency-Check**:
- Clique **Add Dependency-Check**
- Name: `dependency-check` ← deve ser idêntico ao `odcInstallation: 'dependency-check'` no Jenkinsfile
- Marque **Install automatically** → selecione a versão mais recente disponível
- Clique **Save**

### Passo 7 — Chave de API do NVD (recomendado)

O OWASP Dependency-Check baixa a base de CVEs do NVD (National Vulnerability Database). Sem uma chave de API, o download é limitado a 5 requisições/30 segundos, o que torna a **primeira execução demorada (~15-20 min)**.

Obtenha uma chave gratuita em https://nvd.nist.gov/developers/request-an-api-key (e-mail necessário, aprovação imediata).

Após obter a chave:
1. Adicione como credencial no Jenkins: Kind = `Secret text`, ID = `nvd-api-key`
2. Na linha `additionalArguments` do Jenkinsfile, adicione: `--nvdApiKey ${NVD_API_KEY}`
3. Adicione ao bloco `withCredentials`: `string(credentialsId: 'nvd-api-key', variable: 'NVD_API_KEY')`

> **Primeira execução sem chave**: o download pode levar 15-20 minutos e falhar por rate-limit. Se isso ocorrer, adicione `--noupdate` nos `additionalArguments` para usar a base já baixada e tente novamente sem `--noupdate` quando a janela de rate-limit expirar.

### Passo 8 — Semgrep (nenhuma configuração necessária)

Semgrep OSS usa `--config auto` que baixa regras de semgrep.dev. Não requer conta ou API key para o free tier.

Requisito: o container Jenkins deve ter acesso HTTPS de saída para `semgrep.dev`. Verifique:

```bash
docker exec jenkins curl -sS -o /dev/null -w "%{http_code}" https://semgrep.dev && echo " — conexão OK (qualquer resposta HTTP confirma alcance)" || echo " — FALHOU (sem conectividade)"
```

Se o acesso estiver bloqueado (rede corporativa), substitua `--config auto` por `--config p/default` no Jenkinsfile — este conjunto de regras é embutido no pacote semgrep instalado na imagem.

### Verificação pós-configuração

Execute um build e verifique:

1. Stage **SonarQube Analysis** concluída sem erro → acesse http://localhost:9000/dashboard?id=BnpPoc.Api
2. Stage **SonarQube Quality Gate** passed → log mostra `Quality Gate status: OK`
3. Stage **OWASP Dependency-Check** concluída → relatório HTML visível em Build → Dependency-Check
4. Stage **Semgrep** concluída → `semgrep-report.json` em Build Artifacts
5. Artefato `BnpPoc.Api-<N>.zip` visível no Nexus em `dotnet-artifacts`

## Fase 4 — Manual Setup

Execute os passos abaixo **uma única vez** antes de rodar o pipeline com banco de dados e empacotamento versionado.

### Passo 1 — Rebuild da imagem Jenkins

O Dockerfile agora inclui Liquibase e o driver JDBC do SQL Server:

```bash
cd cicd-poc
docker compose build jenkins
docker compose up -d jenkins
```

### Passo 2 — Subir o serviço SQL Server

```bash
cd cicd-poc
docker compose up -d sqlserver
docker compose ps sqlserver
```

Aguarde até o log mostrar que o serviço está pronto:

```bash
docker logs sqlserver | grep "ready for client connections"
```

> Senha do usuário `sa`: `BnpP0c!Local` (fixa, hardcoded em `docker-compose.yml` — mesmo padrão já usado para Postgres/Nexus/Vault neste PoC. Não usar em produção.)

### Passo 3 — Adicionar credencial do SQL Server no Jenkins

Jenkins UI → Manage Jenkins → Credentials → System → Global credentials → **Add Credentials**:
- Kind: **Username with password**
- Username: `sa`
- Password: `BnpP0c!Local`
- ID: `sqlserver-credentials`
- Description: `SQL Server sa (dev)`

### Passo 4 — Verificar conectividade do Jenkins ao SQL Server

```bash
docker exec jenkins bash -c '(echo > /dev/tcp/sqlserver/1433) && echo "OK" || echo "FALHOU"'
```

Saída esperada: `OK`. Se `FALHOU`, confirme que `sqlserver` está no mesmo `docker compose` (`cicd-net`) e que o container está `Up` (`docker compose ps sqlserver`).

### Verificação pós-configuração

Execute um build e verifique:

1. Stage **Database Migration** concluída sem erro → log mostra `liquibase execute-sql` e `liquibase update` bem-sucedidos.
2. Confirme a tabela criada:
   ```bash
   docker exec sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'BnpP0c!Local' -d BnpPocDb -Q "SELECT name FROM sys.tables;" -C
   ```
   Saída esperada: `deployment_record`
3. Stage **Archive** concluída → workspace contém `BnpPoc.Api-<versão>.zip` e `BnpPoc.Api-<versão>.zip.sha256`.
4. Stage **Upload to Nexus** concluída → confirme os dois arquivos no Nexus:
   ```bash
   curl -s -u admin:PASSWORD \
     "http://localhost:8081/service/rest/v1/components?repository=dotnet-artifacts" \
     | grep BnpPoc.Api
   ```
   Saída esperada: entradas para `BnpPoc.Api-<versão>.zip` **e** `BnpPoc.Api-<versão>.zip.sha256`.
5. Baixe ambos e valide a integridade localmente:
   ```bash
   curl -u admin:PASSWORD -O "http://localhost:8081/repository/dotnet-artifacts/BnpPoc.Api-<versão>.zip"
   curl -u admin:PASSWORD -O "http://localhost:8081/repository/dotnet-artifacts/BnpPoc.Api-<versão>.zip.sha256"
   sha256sum -c "BnpPoc.Api-<versão>.zip.sha256"
   ```
   Saída esperada: `BnpPoc.Api-<versão>.zip: OK`

### Nota sobre mudança de nome do artefato (breaking change)

A partir da Fase 4, o nome do artefato muda de `BnpPoc.Api-<BUILD_NUMBER>.zip` (ex: `BnpPoc.Api-42.zip`) para `BnpPoc.Api-<versão-semântica>.zip` (ex: `BnpPoc.Api-1.0.0-build.42.zip`). O comando de verificação da Fase 2 (`grep BnpPoc.Api`) continua funcionando sem alteração, pois faz correspondência por substring, não pelo nome exato do arquivo. Scripts externos que dependam do nome exato `BnpPoc.Api-<N>.zip` (sem o prefixo de versão) precisam ser atualizados.

## Fase 5 — Manual Setup

Execute os passos abaixo **uma única vez** antes de rodar o pipeline com o estágio `Deploy`.

### Passo 1 — Rebuild das imagens Jenkins e apphost

O Dockerfile do Jenkins agora inclui o CLI do Ansible (venv `/opt/ansible-venv`) e o `openssh-client`. O `apphost` é o novo target de deploy (systemd + sshd + `python3` + `sudo` + runtime ASP.NET Core 10):

```bash
cd cicd-poc
docker compose build jenkins apphost
```

### Passo 2 — Gerar par de chaves SSH e cadastrar o credential `apphost-ssh`

O Jenkins conecta ao `apphost` como o usuário `deploy` via chave SSH.

1. Gere um par de chaves dedicado:
   ```bash
   ssh-keygen -t ed25519 -f apphost_deploy -N "" -C "apphost-ssh"
   ```
2. Cole a chave **pública** (`apphost_deploy.pub`) em `cicd-poc/apphost/authorized_keys` (substituindo o placeholder). A chave pública é segura para commitar.
3. Adicione a chave **privada** (`apphost_deploy`) como credencial no Jenkins:
   - Manage Jenkins → Credentials → System → Global credentials → **Add Credentials**
   - Kind: **SSH Username with private key**
   - Username: `deploy`
   - Private key: cole o conteúdo de `apphost_deploy`
   - ID: `apphost-ssh`
   - Description: `SSH deploy key para o apphost`

> O credential `nexus-credentials` (Fase 2) é reutilizado pelo playbook para puxar o artefato do Nexus — não é necessário criar outro.

### Passo 3 — Subir o apphost e verificar os pré-requisitos do managed node

```bash
docker compose up -d apphost
docker compose ps apphost                       # deve estar "Up"
docker exec apphost systemctl is-system-running # "running" ou "degraded" (não "offline")
```

Verifique que o Jenkins alcança o `apphost` e que os pré-requisitos do Ansible existem (interpretador Python + escalonamento sem senha):

```bash
docker exec jenkins ssh -i <chave> deploy@apphost 'python3 --version && sudo -n true'
```

Saída esperada: uma versão do Python 3 e o `sudo -n true` saindo com código 0 (escalonamento passwordless funcionando).

### Passo 4 — Verificar o CLI do Ansible na imagem Jenkins

```bash
docker exec jenkins ansible-playbook --version
```

Saída esperada: sai com código 0 e imprime uma versão do `ansible-core`. Se retornar `command not found`, o venv `/opt/ansible-venv` ou os symlinks não foram criados — refaça o build da imagem.

### Verificação pós-run

Após um build verde, confirme que o serviço está de pé no `apphost`:

```bash
docker exec apphost systemctl is-active bnppoc-api   # "active"
docker exec apphost curl -s localhost:5000/health    # contém "healthy"
```

> **Tradeoffs de PoC:** o `apphost` roda com `privileged: true` (necessário para bootar o systemd dentro de um container) e o usuário `deploy` tem `sudo` **sem senha** (`NOPASSWD:ALL`). Ambos são conveniências de laboratório descartável — **não usar em produção**. O endurecimento equivalente está documentado em [`../docs/cd-windows-iis.md`](../docs/cd-windows-iis.md).

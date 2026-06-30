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
| 1    | **Infraestrutura base** — Jenkins, SonarQube, Postgres, Nexus, Vault via Docker Compose    |
| 2    | **Pipeline .NET** — Jenkinsfile com build, test, análise SonarQube e publicação no Nexus   |
| 3    | **Gestão de segredos** — integração Jenkins ↔ Vault para credenciais de deploy             |
| 4    | **Deploy em IIS** — Ansible playbook para deploy de artefatos .NET em servidores Windows   |
| 5    | **Quality gates** — bloqueio de pipeline por cobertura de testes e issues SonarQube        |
| 6    | **Observabilidade** — logs centralizados e dashboards de status de pipeline                 |

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

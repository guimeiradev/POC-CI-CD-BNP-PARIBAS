# POC CI/CD — .NET no IIS

Prova de conceito de esteira CI/CD para aplicações **.NET** hospedadas em **IIS (Windows Server)**, com todo o "cérebro" da esteira rodando em **Linux via Docker Compose**.

O objetivo é reproduzir, com ferramentas gratuitas e open-source, o mesmo fluxo de entrega que empresas usam em produção — desde o commit do desenvolvedor até o deploy no servidor.

---

## O que é cada ferramenta e por que está aqui

| Ferramenta | Papel na esteira | Equivalente corporativo |
|---|---|---|
| **Jenkins** | Orquestra todo o pipeline: baixa o código, compila, testa, empacota e envia o artefato | Jenkins Enterprise |
| **SonarQube** | Analisa qualidade do código: bugs, code smells, cobertura de testes. O pipeline só avança se o código passar no Quality Gate | SonarQube Enterprise |
| **Semgrep** | SAST — analisa o código em busca de vulnerabilidades de segurança antes de qualquer deploy | Fortify |
| **OWASP Dependency-Check** | Verifica se as bibliotecas NuGet usadas têm vulnerabilidades conhecidas (CVEs) | Nexus IQ / Black Duck |
| **Nexus Repository** | Armazena os artefatos gerados pelo pipeline (`.zip` com o binário publicado). Jenkins faz upload aqui após cada build | JFrog Artifactory |
| **SQL Server** | Armazena os dados da aplicação. Schema criado e versionado exclusivamente via Liquibase — a aplicação nunca roda migration própria | SQL Server on-prem / Azure SQL |
| **HashiCorp Vault** | Guarda segredos (connection strings, senhas) que o Ansible usa na hora do deploy. Jenkins busca os segredos do Vault — nunca ficam hardcoded no pipeline | CyberArk |
| **Ansible + WinRM** | Conecta ao Windows Server, para o Application Pool do IIS, publica o novo artefato e reinicia | Ansible / Octopus Deploy |
| **Liquibase** | Roda migrations de banco de dados de forma versionada e controlada | Liquibase / Flyway |

---

## Arquitetura

```
┌───────────────────────────────────────────────────────────────────────────────┐
│  Máquina A — Linux (toda a esteira roda aqui via Docker Compose)              │
│                                                                               │
│  ┌──────────┐ ┌───────────────┐ ┌────────┐ ┌─────────┐ ┌──────────────────┐   │
│  │ Jenkins  │ │  SonarQube    │ │ Nexus  │ │  Vault  │ │   SQL Server     │   │
│  │  :8080   │ │  :9000        │ │ :8081  │ │  :8200  │ │   :1433          │   │
│  │          │ │ + Postgres    │ │        │ │         │ │  (schema via     │   │
│  │          │ │   :5432       │ │        │ │         │ │   Liquibase)     │   │
│  └────┬─────┘ └───────────────┘ └────────┘ └─────────┘ └──────────────────┘   │
│       │                                                                       │
│       │  Pipeline (10 stages): checkout → db migration → build → test →       │
│       │    quality gate → empacotar (checksum + semver) → upload Nexus →       │
│       │    deploy (Ansible + systemd via SSH)                                  │
│       │                                                                       │
│       │  Ansible via SSH (Fase 5 — executado localmente)                       │
│       ▼                                                                       │
│  ┌──────────────────────────────────────────────────────────────────────┐   │
│  │ apphost — .NET app host (systemd em container)                        │   │
│  │  bnppoc-api.service → dotnet BnpPoc.Api.dll :5000 → SQL Server :1433  │   │
│  └──────────────────────────────────────────────────────────────────────┘   │
└───────────────────────────────────────────────────────────────────────────────┘

        Alvo de produção documentado (não executado no PoC):
        ┌──────────────────────────────────────┐
        │  Máquina B — Windows Server + IIS    │
        │  Ansible via WinRM → recycle do       │
        │  Application Pool (ver                 │
        │  docs/cd-windows-iis.md)              │
        └──────────────────────────────────────┘
```

---

## Status atual das fases

### ✅ Fase 1 — Infraestrutura (Docker Compose)

Toda a esteira sobe com um único `docker compose up -d`:
- Jenkins com .NET 10 SDK instalado
- SonarQube Community + PostgreSQL 16
- Nexus Repository OSS
- HashiCorp Vault em modo dev

### ✅ Fase 2 — CI mínima (pipeline funcionando)

Pipeline fechado de ponta a ponta:
- App .NET 10 Minimal API em `src/BnpPoc.Api` com endpoint `GET /health`
- `Jenkinsfile` na raiz do repo com 6 stages: **Restore → Build → Test → Publish → Archive → Upload to Nexus**
- Artefato `BnpPoc.Api-<N>.zip` publicado no Nexus (`dotnet-artifacts`) a cada build

### ✅ Fase 3 — Quality Gates

- SonarQube com Quality Gate **bloqueante** — coverage threshold configurável na UI do SonarQube (não hardcoded)
- OWASP Dependency-Check — bloqueia se pacotes NuGet tiverem CVEs com CVSS ≥ 7.0
- Semgrep OSS — análise estática de segurança, bloqueia em findings de severidade ERROR

### ✅ Fase 4 — Empacotamento e banco

- Zip com checksum SHA256 (`BnpPoc.Api-<versão>.zip.sha256`) publicado no Nexus ao lado do artefato — verificável via `sha256sum -c`
- Versionamento semântico no artefato e no Nexus (`BnpPoc.Api-1.0.0-build.<N>.zip`) — base version fixa no Jenkinsfile, build number do Jenkins
- SQL Server real no Docker Compose + Liquibase criando o schema (`deployment_record`) antes dos testes rodarem
- `BnpPoc.Api` usa o banco de fato — endpoints `POST /deployments` e `GET /deployments` via Dapper (não decorativo)

### ✅ Fase 5 — CD (Ansible + systemd/SSH; IIS documentado)

- Novo estágio `Deploy` no `Jenkinsfile` (10º, após `Upload to Nexus`) — roda o playbook `deploy/ansible/deploy.yml` via Ansible sobre SSH
- App deployado num app host Linux (`apphost`) como serviço systemd `bnppoc-api` — target novo no Docker Compose (systemd em container)
- O playbook **puxa o artefato + `.sha256` do Nexus e verifica o SHA256 antes de fazer o deploy** (hard-fail se não bater) — o que roda é provadamente o artefato governado que passou pelos quality gates
- Smoke test: `/health` (200 + `healthy`) + round-trip `POST`/`GET /deployments` provando que o build deployado alcança o SQL Server
- Equivalente Windows/IIS via WinRM (parar/reiniciar Application Pool) + endurecimento de produção documentados em [`docs/cd-windows-iis.md`](docs/cd-windows-iis.md) — não executado no PoC

### 🔜 Fase 6 — Polimento

- Webhook Git → trigger automático no push (sem precisar clicar "Build Now")
- `Jenkinsfile` versionado no próprio repositório da aplicação
- Agente Jenkins rodando no Windows para builds .NET Framework
- Shared Library Jenkins com stages reutilizáveis entre projetos

---

## Estrutura do repositório

```
POC-CI-CD-BNP-PARIBAS/
├── Jenkinsfile                        # Pipeline declarativo — 10 stages (última: Deploy)
├── db/
│   └── changelog/
│       ├── db.changelog-master.xml    # Changelog raiz (includes)
│       └── changesets/
│           └── 001-create-deployment-record.xml
├── deploy/
│   └── ansible/                       # CD: playbook de deploy (pull Nexus + verify + systemd + smoke)
│       ├── ansible.cfg
│       ├── inventory.ini              # apphost (SSH, user deploy)
│       ├── deploy.yml                 # Playbook do estágio Deploy
│       └── templates/
│           └── bnppoc-api.service.j2  # Unit systemd do bnppoc-api
├── docs/
│   ├── FEATURE-MAP.md                 # Índice de features → código
│   └── cd-windows-iis.md             # Equivalente Windows/IIS (WinRM) + endurecimento (não executado)
├── src/
│   ├── BnpPoc.sln                     # Solution .NET
│   ├── BnpPoc.Api/
│   │   ├── BnpPoc.Api.csproj          # .NET 10 Minimal API + Dapper + Microsoft.Data.SqlClient
│   │   ├── appsettings.json           # ConnectionStrings:BnpPocDb
│   │   ├── Program.cs                 # GET /health, POST/GET /deployments
│   │   ├── Data/
│   │   │   └── SqlConnectionFactory.cs
│   │   └── Deployments/
│   │       └── DeploymentRecord.cs
│   └── BnpPoc.Api.Tests/
│       ├── BnpPoc.Api.Tests.csproj    # Projeto de testes xUnit
│       ├── HealthEndpointTests.cs     # Teste de integração do endpoint /health
│       └── DeploymentEndpointsTests.cs # Teste de integração dos endpoints /deployments
└── cicd-poc/
    ├── docker-compose.yml             # Orquestra Jenkins, SonarQube, Nexus, Vault, SQL Server, apphost
    ├── README.md                      # Setup detalhado (Fases 1 a 5)
    ├── apphost/                        # Deploy target da Fase 5
    │   ├── Dockerfile                 # systemd + sshd + python3 + sudo + runtime ASP.NET Core 10
    │   └── authorized_keys            # Chave pública do credential apphost-ssh (placeholder)
    └── jenkins/
        ├── Dockerfile                 # Jenkins LTS-jdk17 + .NET 10 SDK + quality tools + Liquibase + Ansible
        └── plugins.txt                # Plugins instalados na imagem
```

---

## Como rodar (Fase 1 + 2)

### Pré-requisitos

- Docker Engine 24+ e Docker Compose v2+
- 8 GB RAM disponível (SonarQube + Elasticsearch consomem ~3 GB)
- Portas 8080, 9000, 8081, 8200 e 1433 livres

### 1. Subir a esteira

```bash
# Pré-requisito do SonarQube (Elasticsearch precisa desse valor mínimo)
sudo sysctl -w vm.max_map_count=262144

cd cicd-poc
docker compose build   # ~5 min na primeira vez (baixa .NET 10 SDK)
docker compose up -d
docker compose ps      # todos devem estar "Up"
```

### 2. Acessar os serviços

| Serviço | URL | Credenciais |
|---|---|---|
| Jenkins | http://localhost:8080 | senha inicial: ver abaixo |
| SonarQube | http://localhost:9000 | admin / admin |
| Nexus | http://localhost:8081 | admin / ver abaixo |
| Vault | http://localhost:8200 | token: `root` |

```bash
# Senha inicial do Jenkins
docker exec jenkins cat /var/jenkins_home/secrets/initialAdminPassword

# Senha inicial do Nexus
docker exec nexus cat /nexus-data/admin.password
```

### 3. Configurar para rodar o pipeline (Fase 2)

Veja o passo a passo completo em [`cicd-poc/README.md`](cicd-poc/README.md#fase-2--manual-setup).

Resumo dos 6 passos manuais:
1. Rebuild da imagem Jenkins (adiciona `zip`)
2. Verificar acesso externo do Jenkins ao NuGet
3. Criar repositório `dotnet-artifacts` no Nexus (raw hosted, Allow redeploy)
4. Adicionar credencial `nexus-credentials` no Jenkins
5. Adicionar chave SSH `github-ssh` no Jenkins
6. Criar job Pipeline apontando para este repositório

---

## Decisões de design

- **SonarQube usa PostgreSQL** — H2 (banco embutido) não é suportado em produção pelo SonarQube.
- **Vault em modo dev** — dados em memória, token fixo `root`. Reiniciar o container apaga os segredos. Correto para PoC; Fase 5 usa Vault em modo server com storage persistente.
- **.NET SDK via `dotnet-install.sh`** — instalação oficial da Microsoft, não via apt, para controlar exatamente a versão instalada.
- **Upload ao Nexus via `curl`** — mais simples que o plugin `nexus-artifact-uploader` para PoC. Fase 4 evolui para versionamento semântico.
- **Credenciais hardcoded no compose** — intencional para PoC local. Em produção, usar `.env` fora do repositório.
- **Liquibase é o único dono do schema** — `BnpPoc.Api` nunca roda migrations próprias (sem `Database.Migrate()` do EF Core, sem `EnsureCreated()`). Evita dois sistemas de migration competindo pelo mesmo schema.
- **Dapper em vez de EF Core** — acesso a dados mínimo e explícito, sem tooling de migration embutido que competiria com o Liquibase.
- **SQL Server com senha fixa em `docker-compose.yml`** — mesmo padrão já usado para Postgres/Nexus/Vault. Não usar em produção; Fase 5 introduz Vault para credenciais de deploy real.
- **Testcontainers rejeitado** — o container Jenkins não tem Docker socket (mesma restrição que já vetou Docker-in-Docker para o Semgrep na Fase 3). O teste de integração de `/deployments` depende do serviço `sqlserver` real do Docker Compose, migrado pelo estágio `Database Migration` antes do estágio de testes rodar.

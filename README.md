# POC CI/CD — .NET no IIS

Prova de conceito de esteira CI/CD para aplicações **.NET** hospedadas em **IIS (Windows Server)**, com todo o "cérebro" da esteira rodando em **Linux via Docker Compose**.

## Objetivo

Reproduzir, com ferramentas gratuitas e open-source, uma esteira de entrega contínua equivalente ao que é usado em ambientes corporativos (Jenkins + SonarQube + Fortify + Nexus IQ + JFrog + CyberArk + Ansible).

## Stack

| Papel | Ferramenta PoC | Equivalente corporativo |
|---|---|---|
| CI/CD | Jenkins | Jenkins Enterprise |
| Qualidade de código | SonarQube Community | SonarQube Enterprise |
| SAST | Semgrep | Fortify |
| Dependências vulneráveis | OWASP Dependency-Check | Nexus IQ / Black Duck |
| Repositório de artefatos | Nexus Repository OSS | JFrog Artifactory |
| Gestão de credenciais | HashiCorp Vault | CyberArk |
| Deploy remoto | Ansible + WinRM | Ansible / Octopus Deploy |
| Migrations de banco | Liquibase | Liquibase / Flyway |
| Runtime alvo | Windows Server + IIS | Windows Server + IIS |

## Arquitetura

```
┌────────────────────────────────────────────────────┐
│  Máquina A — Linux (Docker Compose)                │
│                                                    │
│  ┌─────────┐  ┌───────────┐  ┌───────┐  ┌───────┐ │
│  │ Jenkins │  │ SonarQube │  │ Nexus │  │ Vault │ │
│  │  :8080  │  │   :9000   │  │ :8081 │  │ :8200 │ │
│  └────┬────┘  └───────────┘  └───────┘  └───────┘ │
│       │              └── Postgres :5432             │
└───────┼────────────────────────────────────────────┘
        │ Ansible / WinRM
        ▼
┌───────────────────────────────┐
│  Máquina B — Windows Server   │
│  IIS + Application Pool       │
│  (VirtualBox / VMware Eval)   │
└───────────────────────────────┘
```

> Máquina B só é necessária na **Fase 5** (deploy efetivo).

## Roadmap de Fases

### ✅ Fase 1 — Infraestrutura local (Docker Compose)
Sobe toda a esteira localmente via Docker Compose:
- Jenkins LTS com .NET 10 SDK instalado
- SonarQube Community + PostgreSQL 16
- Nexus Repository OSS
- HashiCorp Vault (modo dev)

### ✅ Fase 2 — CI mínima
- App .NET 10 Minimal API (`src/BnpPoc.Api`) com endpoint `GET /health`
- `Jenkinsfile` na raiz do repo com 6 stages: Restore → Build → Test → Publish → Archive → Upload to Nexus
- Publicação do artefato `BnpPoc.Api-<N>.zip` no Nexus (`dotnet-artifacts`)
- Setup manual documentado em [`cicd-poc/README.md`](cicd-poc/README.md#fase-2--manual-setup)

### 🔜 Fase 3 — Quality Gates
- SonarQube com Quality Gate **bloqueante** (pipeline falha se não passar)
- OWASP Dependency-Check (vulnerabilidades em pacotes NuGet)
- Semgrep (análise estática de segurança no código)

### 🔜 Fase 4 — Empacotamento e banco
- Geração de `.zip` com checksum (fingerprint do artefato)
- Publicação versionada no Nexus (ex: `1.0.0-build.42`)
- Liquibase rodando migrations contra SQL Server

### 🔜 Fase 5 — CD para IIS
- Jenkins busca segredo do Vault (connection string, credenciais)
- Ansible conecta ao Windows Server via WinRM
- Para o Application Pool → publica artefato → inicia o pool
- Registro de auditoria da entrega

### 🔜 Fase 6 — Polimento
- Webhook do repositório Git (trigger automático no push)
- `Jenkinsfile` versionado no repositório da aplicação (Pipeline as Code)
- Agente Jenkins Windows para builds .NET Framework (pré-requisito)
- Shared Library Jenkins com stages reutilizáveis

## Estrutura do repositório

```
POC-CI-CD-BNP-PARIBAS/
└── cicd-poc/
    ├── docker-compose.yml      # Orquestração de todos os serviços
    ├── README.md               # Instruções de execução da Fase 1
    └── jenkins/
        ├── Dockerfile          # Jenkins LTS-jdk17 + .NET 10 SDK
        └── plugins.txt         # Plugins instalados no build da imagem
```

## Início rápido (Fase 1)

```bash
# Pré-requisito do SonarQube (Elasticsearch exige esse valor mínimo)
sudo sysctl -w vm.max_map_count=262144

# Subir a esteira
cd cicd-poc
docker compose build   # primeira vez demora ~5 min (baixa .NET 10 SDK)
docker compose up -d
docker compose ps
```

Serviços disponíveis após a subida:

| Serviço | URL | Credenciais |
|---|---|---|
| Jenkins | http://localhost:8080 | senha inicial via `docker exec` |
| SonarQube | http://localhost:9000 | admin / admin |
| Nexus | http://localhost:8081 | admin / ver `admin.password` |
| Vault | http://localhost:8200 | token: `root` |

```bash
# Senha inicial do Jenkins
docker exec jenkins cat /var/jenkins_home/secrets/initialAdminPassword

# Senha inicial do Nexus
docker exec nexus cat /nexus-data/admin.password
```

## Decisões de design

- **SonarQube usa PostgreSQL**, não H2 — H2 não é suportado em produção pelo SonarQube.
- **Vault em modo dev** — dados em memória, token fixo `root`. Reiniciar o container apaga todos os segredos. Suficiente para PoC; Fase 5 documenta o setup de produção.
- **.NET SDK via `dotnet-install.sh`** — instalação oficial da Microsoft, não via apt, para garantir controle de versão e compatibilidade com a imagem Jenkins.
- **Credenciais hardcoded no compose** — intencional para PoC local. Antes de qualquer uso em rede, mover para `.env` excluído do git.

## Pré-requisitos

- Docker Engine 24+
- Docker Compose v2+
- 8 GB RAM disponível (SonarQube + Elasticsearch consomem ~3 GB sozinhos)
- Porta 8080, 9000, 8081 e 8200 livres no host

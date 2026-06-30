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
| **HashiCorp Vault** | Guarda segredos (connection strings, senhas) que o Ansible usa na hora do deploy. Jenkins busca os segredos do Vault — nunca ficam hardcoded no pipeline | CyberArk |
| **Ansible + WinRM** | Conecta ao Windows Server, para o Application Pool do IIS, publica o novo artefato e reinicia | Ansible / Octopus Deploy |
| **Liquibase** | Roda migrations de banco de dados de forma versionada e controlada | Liquibase / Flyway |

---

## Arquitetura

```
┌─────────────────────────────────────────────────────────────────────┐
│  Máquina A — Linux (toda a esteira roda aqui via Docker Compose)    │
│                                                                     │
│  ┌──────────────┐  ┌─────────────────┐  ┌────────┐  ┌───────────┐  │
│  │   Jenkins    │  │   SonarQube     │  │ Nexus  │  │   Vault   │  │
│  │   :8080      │  │   :9000         │  │ :8081  │  │   :8200   │  │
│  │              │  │ + Postgres:5432 │  │        │  │           │  │
│  └──────┬───────┘  └─────────────────┘  └────────┘  └───────────┘  │
│         │                                                            │
│         │  Pipeline: checkout → build → test → quality gate         │
│         │           → empacotar → upload Nexus → deploy             │
└─────────┼────────────────────────────────────────────────────────────┘
          │
          │  Ansible via WinRM (Fase 5)
          ▼
┌──────────────────────────────────────┐
│  Máquina B — Windows Server + IIS    │
│  (VM no VirtualBox com avaliação)    │
│  Necessária apenas a partir Fase 5   │
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

### 🔜 Fase 3 — Quality Gates

- SonarQube com Quality Gate **bloqueante** — pipeline falha se code coverage ou issues críticos não passarem
- OWASP Dependency-Check — bloqueia se pacotes NuGet tiverem CVEs
- Semgrep — análise estática de segurança no código

### 🔜 Fase 4 — Empacotamento e banco

- Zip com checksum SHA256 (fingerprint do artefato — garante integridade)
- Versionamento semântico no Nexus (ex: `1.0.0-build.42`)
- Liquibase rodando migrations contra SQL Server no deploy

### 🔜 Fase 5 — CD para IIS

- Jenkins busca credenciais do Vault (connection string, senha do app pool)
- Ansible conecta ao Windows Server via WinRM
- Sequência: **para o Application Pool → substitui os binários → inicia o pool**
- Log de auditoria da entrega

### 🔜 Fase 6 — Polimento

- Webhook Git → trigger automático no push (sem precisar clicar "Build Now")
- `Jenkinsfile` versionado no próprio repositório da aplicação
- Agente Jenkins rodando no Windows para builds .NET Framework
- Shared Library Jenkins com stages reutilizáveis entre projetos

---

## Estrutura do repositório

```
POC-CI-CD-BNP-PARIBAS/
├── Jenkinsfile                        # Pipeline declarativo — 6 stages
├── src/
│   ├── BnpPoc.sln                     # Solution .NET
│   ├── BnpPoc.Api/
│   │   ├── BnpPoc.Api.csproj          # .NET 10 Minimal API
│   │   └── Program.cs                 # GET /health → {"status":"healthy"}
│   └── BnpPoc.Api.Tests/
│       ├── BnpPoc.Api.Tests.csproj    # Projeto de testes xUnit
│       └── HealthEndpointTests.cs     # Teste de integração do endpoint
└── cicd-poc/
    ├── docker-compose.yml             # Orquestra Jenkins, SonarQube, Nexus, Vault
    ├── README.md                      # Setup detalhado (Fases 1 e 2)
    └── jenkins/
        ├── Dockerfile                 # Jenkins LTS-jdk17 + .NET 10 SDK + zip
        └── plugins.txt                # Plugins instalados na imagem
```

---

## Como rodar (Fase 1 + 2)

### Pré-requisitos

- Docker Engine 24+ e Docker Compose v2+
- 8 GB RAM disponível (SonarQube + Elasticsearch consomem ~3 GB)
- Portas 8080, 9000, 8081 e 8200 livres

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

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

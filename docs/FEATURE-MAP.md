# Feature Map

> Auto-maintained index of every user-facing feature and the code path that implements it. Updated alongside the code — not after the fact.

## Health Check Endpoint

Lets a caller (load balancer, monitoring probe, or developer) verify that the
`BnpPoc.Api` process is up and responding.

**Flow:**

1. `src/BnpPoc.Api/Program.cs` — `GET /health` route handler returns `200 OK` with `{"status":"healthy"}`.
2. `src/BnpPoc.Api.Tests/HealthEndpointTests.cs` — integration test boots the app in-process via `WebApplicationFactory<Program>`, calls `GET /health`, and asserts the response is successful and contains `"healthy"`.

---

## CI Build & Publish Pipeline

Takes a commit on `main`, migrates the database, runs quality/security gates,
builds and tests the .NET solution, and publishes a semver-versioned artifact
(plus its checksum) to the Nexus repository — the mechanism by which a
developer's change becomes a governed, deployable package. This is the CI half
of a 10-stage `Jenkinsfile` (the 10th stage, `Deploy`, is documented under
"Continuous Deployment (CD)" below).

**Trigger:** Auto-triggered by `triggers { pollSCM('H/2 * * * *') }` in the
`Jenkinsfile` — Jenkins polls the SCM every ~2 min and starts a build on a new
commit to `main`, fully local (no public inbound URL). The real event-driven
webhook path (`githubPush()` + smee.io/ngrok tunnel) is documented in
`cicd-poc/README.md` (Fase 6) and `docs/jenkins-windows-agent.md`, with the
Dockerized-Jenkins caveat.

**Flow:**

1. `Jenkinsfile` (Restore stage) — `dotnet restore src/BnpPoc.sln` restores NuGet packages for both projects.
2. `Jenkinsfile` (Database Migration stage) — waits for `sqlserver:1433`, then runs Liquibase (`execute-sql` to create `BnpPocDb`, then `update` against `db/changelog/db.changelog-master.xml`) using the `sqlserver-credentials` credential.
3. `Jenkinsfile` (SonarQube Analysis stage) — brackets `dotnet build --no-restore -c Release`, `dotnet test` with coverage, and `reportgenerator` between `dotnet-sonarscanner begin/end` so MSBuild hooks capture Roslyn diagnostics (build + test run nested inside this stage by design).
4. `Jenkinsfile` (SonarQube Quality Gate stage) — `waitForQualityGate abortPipeline: true` (5-min timeout) — blocks the pipeline if the gate fails.
5. `Jenkinsfile` (OWASP Dependency-Check stage) — scans `src/` and fails on CVSS ≥ `OWASP_CVSS_THRESHOLD` (7); publishes the HTML/JSON report.
6. `Jenkinsfile` (Semgrep stage) — `semgrep scan --config auto --severity ERROR --error` over `src/`, archiving `semgrep-report.json`.
7. `Jenkinsfile` (Publish stage) — `dotnet publish src/BnpPoc.Api/BnpPoc.Api.csproj -c Release --no-build -o publish/` produces the deployable output.
8. `Jenkinsfile` (Archive stage) — zips `publish/` into `BnpPoc.Api-${ARTIFACT_VERSION}.zip` (semver: `${BASE_VERSION}-build.${BUILD_NUMBER}`, e.g. `BnpPoc.Api-1.0.0-build.42.zip`) and writes `${ARTIFACT_NAME}.sha256` via `sha256sum`.
9. `Jenkinsfile` (Upload to Nexus stage) — uploads the zip **and** its `.sha256` to `http://nexus:8081/repository/dotnet-artifacts`, authenticating with the Jenkins `nexus-credentials` credential. As of Fase 6 the upload is performed by the Shared Library step `jenkins-shared-library/vars/nexusUpload.groovy`, consumed via `@Library('bnp-shared@main')` at the top of the `Jenkinsfile` (registered as the `bnp-shared` Global Pipeline Library — see `cicd-poc/README.md` Fase 6).
10. `cicd-poc/docker-compose.yml` (`nexus` service) — receives and stores the artifact + checksum in the `dotnet-artifacts` repository (created manually per `cicd-poc/README.md`).

Runs in a Jenkins agent built from `cicd-poc/jenkins/Dockerfile`, which provides the .NET 10 SDK, `zip`, the SonarScanner/reportgenerator tools, Semgrep, Liquibase, and the Ansible CLI.

---

## Local CI/CD Toolchain Bootstrap

Lets a developer stand up the entire CI/CD toolchain (Jenkins, SonarQube,
Nexus, Vault) on a single Linux host for local development or demoing the PoC.

**Flow:**

1. `cicd-poc/docker-compose.yml` — defines the `jenkins`, `sonarqube`, `db` (Postgres), `nexus`, and `vault` services on the `cicd-net` bridge network, started via `docker compose up -d`.
2. `cicd-poc/jenkins/Dockerfile` — builds the `jenkins` service's image: Jenkins LTS + JDK17, .NET 10 SDK, `zip`, `dotnet-sonarscanner`/`dotnet-reportgenerator-globaltool`, Semgrep, and the plugins listed in `cicd-poc/jenkins/plugins.txt`.
3. `cicd-poc/README.md` — documents the one-time manual setup after first boot: retrieving the Jenkins/Nexus initial passwords, creating the `dotnet-artifacts` Nexus repository, adding the `nexus-credentials` and `github-ssh` Jenkins credentials, and creating the `BnpPoc-CI` Pipeline job pointed at this repository's `Jenkinsfile`.

---

## Continuous Deployment (CD)

Takes the versioned artifact already published to Nexus and turns it into a
running application on a systemd-managed app host — the mechanism by which a
governed, checksummed package becomes a live service without a human copying
files onto a server.

**Flow:**

1. `Jenkinsfile` (Deploy stage) — runs immediately after `Upload to Nexus`; injects the `apphost-ssh` (SSH) and `nexus-credentials` (username/password) Jenkins credentials and invokes `ansible-playbook deploy/ansible/deploy.yml` over SSH against `apphost`, passing `ARTIFACT_VERSION`/`ARTIFACT_NAME`/`CHECKSUM_NAME`/`NEXUS_URL` as extra-vars.
2. `deploy/ansible/deploy.yml` (pull + verify) — `get_url` pulls the artifact and its `.sha256` back from Nexus and **verifies the SHA256 (hard-fail on mismatch)** before touching the server, so the deployed bytes are provably the published artifact.
3. `deploy/ansible/deploy.yml` (deploy) — unarchives the release, atomically repoints the `current` symlink, templates `templates/bnppoc-api.service.j2` into `/etc/systemd/system/bnppoc-api.service`, and restarts the `bnppoc-api` systemd unit (`systemd_service`, `become: true`).
4. `deploy/ansible/deploy.yml` (smoke test) — `uri` tasks hit `/health` (expects `200` + `healthy`) and perform a `/deployments` POST→GET round-trip proving the deployed build reaches SQL Server; a failed smoke test fails the stage.
5. `cicd-poc/apphost/Dockerfile` + `cicd-poc/docker-compose.yml` (`apphost` service) — the deploy target: a systemd-in-container Debian host on `cicd-net` with `openssh-server`, `python3`, `sudo`/passwordless-`deploy`, and the ASP.NET Core 10 runtime.

The Windows/IIS production equivalent of this playbook (WinRM instead of SSH,
IIS Application Pool recycle instead of systemd) is documented in
`docs/cd-windows-iis.md`, along with the production-hardening remediations.

---

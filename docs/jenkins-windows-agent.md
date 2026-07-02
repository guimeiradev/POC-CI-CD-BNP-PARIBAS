# Agente Jenkins Windows para .NET Framework (documentado, não executado)

> Documento de referência. Este PoC compila **.NET 10** (cross-platform) direto
> no controller Linux — o container `jenkins`. Este documento descreve como o
> mesmo Jenkins alcançaria um alvo que **só** compila no Windows: uma aplicação
> **.NET Framework 4.x** (legada, Windows-only), adicionando um **agente
> (node) Windows** e direcionando um stage para ele. Nada aqui é executado no
> PoC — não há host Windows disponível — mas a estrutura é deliberadamente
> portável, no mesmo espírito de [`cd-windows-iis.md`](cd-windows-iis.md).
>
> Este documento também cobre dois pontos de configuração da Fase 6 que valem
> para o PoC executado: o **setup da Shared Library** (`bnp-shared`) e o
> **caminho real de webhook** (`githubPush()`) com seu caveat local.

---

## 1. Objetivo — por que um agente Windows

O `Jenkinsfile` roda `dotnet build` no controller Linux porque o alvo do PoC é
**.NET 10**, que é cross-platform. Uma aplicação **.NET Framework 4.x** não
compila no Linux: ela exige a toolchain Windows-only:

| Toolchain | .NET 10 (PoC, executado) | .NET Framework 4.x (documentado) |
|---|---|---|
| Compilador | `dotnet build` (SDK cross-platform) | `MSBuild.exe` (Visual Studio Build Tools) |
| Restore de pacotes | `dotnet restore` | `nuget.exe restore` |
| SO do build | Linux (controller) | Windows Server (agente) |
| Runtime alvo | ASP.NET Core 10 (portável) | .NET Framework 4.x (Windows-only) |

Como o controller Linux não tem `MSBuild.exe` nem `nuget.exe`, o build .NET
Framework precisa rodar num **agente Windows** conectado ao mesmo Jenkins.

---

## 2. Como adicionar o node Windows

Manage Jenkins → **Nodes** → **New Node**:

1. Node name: `windows-dotnetfx` | Type: **Permanent Agent**.
2. **Number of executors:** 1 (ou mais, conforme a máquina).
3. **Remote root directory:** `C:\J\workspace` (workspace do agente no disco Windows).
4. **Labels:** `windows` ← **exatamente este label**; é por ele que um stage
   direciona o trabalho (`agent { label 'windows' }`).
5. **Usage:** "Only build jobs with label expressions matching this node" —
   garante que só os stages `.NET Framework` cairão nele; o resto do pipeline
   continua no controller Linux.
6. **Launch method** — escolher conforme a conexão (seção 3).

---

## 3. Conexão do agente (JNLP/inbound ou SSH)

| Método | Como funciona | Quando usar |
|---|---|---|
| **JNLP (inbound)** | O agente Windows abre a conexão **de saída** para o controller e roda o `agent.jar` (baixado da UI do node). Não exige porta de entrada no Windows. | Padrão para Windows; atravessa NAT/firewall sem abrir portas no agente. |
| **SSH** | O controller abre a conexão **de entrada** para o `sshd` do Windows (OpenSSH Server). Exige a porta 22 aberta no agente. | Quando já há OpenSSH Server gerenciado no parque Windows. |
| **WinRM** | Não suportado nativamente como launch method de node do Jenkins (é o transporte do **Ansible**, ver `cd-windows-iis.md`, não do agente Jenkins). | — |

Pré-requisito comum aos dois: **JDK (JRE 17+)** instalado no Windows Server para
rodar o `agent.jar`.

---

## 4. Pré-requisitos no agente Windows

Instalados **no Windows Server real** (não no PoC):

1. **Visual Studio Build Tools** (ou Build Tools standalone) com o workload
   **MSBuild** + o `MSBuild.exe` no `PATH`.
2. **`nuget.exe`** no `PATH` (restore de pacotes .NET Framework).
3. **.NET Framework Developer Pack** da versão alvo (ex.: 4.8) — os assemblies
   de referência para compilar contra aquele target framework.
4. **Git para Windows** — checkout do repositório no workspace do agente.
5. **JDK/JRE 17+** — runtime do `agent.jar` (ver seção 3).

---

## 5. Como um stage direciona o agente

O pipeline declara `agent any` no topo (roda no controller Linux por padrão). Um
stage individual **sobrescreve** esse default com `agent { label 'windows' }`,
fazendo *apenas aquele stage* rodar no node Windows:

```groovy
stage('Build .NET Framework') {
    agent { label 'windows' }
    steps {
        bat 'nuget restore MyLegacyApp.sln'
        bat 'msbuild MyLegacyApp.sln /p:Configuration=Release'
    }
}
```

- **`agent { label 'windows' }`** — sobrescreve o `agent any` do pipeline; casa
  com o label `windows` definido no node (seção 2). O Jenkins enfileira o stage
  para o primeiro executor livre daquele node.
- **`bat` vs `sh`** — no agente Windows os comandos rodam via `bat` (cmd.exe),
  não `sh`. É o análogo direto do `sh` usado nos stages Linux deste PoC.

---

## 6. Por que não é executado no PoC

Não há host Windows disponível localmente (mesmo motivo de
[`cd-windows-iis.md`](cd-windows-iis.md)): o PoC compila **.NET 10** no controller
Linux, que não precisa de MSBuild/nuget nem de um node Windows. O agente Windows
fica documentado como o caminho para builds **.NET Framework** legados.

### Resumo Linux controller × Windows agent

| Aspecto | Linux controller (PoC, executado) | Windows agent (documentado) |
|---|---|---|
| Shell dos steps | `sh` | `bat` |
| Toolchain | `dotnet` (SDK) | `msbuild` + `nuget.exe` |
| Target framework | .NET 10 | .NET Framework 4.x |
| Conexão do node | n/a (é o próprio controller) | JNLP (inbound) ou SSH |
| Seleção no stage | `agent any` (default) | `agent { label 'windows' }` |

---

## 7. Setup da Shared Library (`bnp-shared`)

O `Jenkinsfile` abre com `@Library('bnp-shared@main') _` e o stage `Upload to
Nexus` consome o step `nexusUpload` da biblioteca. Para o Jenkins resolver esse
`@Library`, registre a biblioteca **uma única vez**:

Manage Jenkins → **System** → **Global Trusted Pipeline Libraries** → **Add**:

- **Name:** `bnp-shared` ← idêntico ao usado em `@Library('bnp-shared@main')`.
- **Default version:** `main`.
- **Retrieval method:** **Modern SCM** → **Git**.
- **Project Repository:** `git@github.com:guimeiradev/POC-CI-CD-BNP-PARIBAS.git`.
- **Credentials:** `github-ssh` (o mesmo credential SSH da Fase 2).
- **Library Path:** `jenkins-shared-library` ← a biblioteca é **in-repo**, num
  subdiretório deste mesmo repositório (o código vive em
  `jenkins-shared-library/vars/nexusUpload.groovy`), não num repo separado.
- **Load implicitly:** **desmarcado** — o `Jenkinsfile` usa `@Library`
  explícito, então a biblioteca não deve ser carregada em todo build.

> **Caveat do plugin:** o campo **Library Path** é exposto pelo retriever Modern
> SCM do plugin "Pipeline: Groovy Libraries" (`workflow-cps-global-lib`). Se a
> versão embutida do plugin não expuser esse campo, a alternativa é publicar a
> biblioteca num repositório separado (`bnp-jenkins-shared`) com o mesmo
> conteúdo. A opção executada no PoC é a **in-repo com Library Path**.

---

## 8. Webhook real (`githubPush()`) e o caveat local

O trigger executado no PoC é `triggers { pollSCM('H/2 * * * *') }` — o Jenkins
faz **poll** de saída do SCM a cada ~2 min e dispara o build ao detectar um novo
commit. Ele foi escolhido por rodar **100% local, sem porta de entrada**.

O caminho de **produção** é o webhook real, dirigido por evento (latência de
segundos, sem poll periódico):

1. Trocar/complementar o trigger por `triggers { githubPush() }` no `Jenkinsfile`.
2. No GitHub → repo → **Settings → Webhooks → Add webhook**, com Payload URL
   `http://<jenkins-publico>/github-webhook/` e content type `application/json`.

> **Caveat:** o Jenkins deste PoC roda **Dockerizado, sem URL pública** — o
> GitHub não consegue alcançar `localhost:8080` de fora. Para exercitar o webhook
> real localmente seria preciso expor o Jenkins via um túnel (**smee.io** ou
> **ngrok**) e usar a URL do túnel como Payload URL. Por isso o **`pollSCM` é o
> default executado** no PoC, e o `githubPush()` fica documentado como o caminho
> de produção.

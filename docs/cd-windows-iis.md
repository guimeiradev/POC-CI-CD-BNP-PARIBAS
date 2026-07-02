# CD para Windows Server + IIS (equivalente, não executado)

> Documento de referência. O PoC roda o Continuous Deployment de fato contra um
> app host Linux (systemd via SSH — ver `deploy/ansible/deploy.yml` e o estágio
> `Deploy` do `Jenkinsfile`). Este documento descreve como **o mesmo pipeline**
> alcançaria um alvo real de produção: uma aplicação .NET hospedada em **IIS**
> num **Windows Server**. Nada aqui é executado no PoC — não há host Windows
> disponível — mas a estrutura do playbook é deliberadamente portável.

---

## 1. Objetivo

O mecanismo de deploy foi escolhido (**Ansible sobre um serviço gerenciado**)
justamente porque a mesma estrutura de playbook porta para Windows trocando duas
peças:

| Peça | Linux (PoC, executado) | Windows/IIS (produção, documentado) |
|---|---|---|
| Conexão | SSH (chave) | WinRM (NTLM/Kerberos) |
| Ciclo de vida do processo | `systemd` (`systemctl restart`) | IIS Application Pool (recycle) |

O restante do fluxo — puxar o artefato versionado + `.sha256` do Nexus,
**verificar o checksum antes de qualquer alteração no servidor**, substituir os
binários e rodar o smoke test — permanece idêntico. A sequência de deploy no IIS
é a análoga direta do `systemctl restart`: **parar o Application Pool →
substituir os binários → iniciar o Application Pool**.

---

## 2. Inventário WinRM

Em vez do `deploy/ansible/inventory.ini` (SSH), o alvo Windows usa uma conexão
WinRM sobre HTTPS:

```ini
[windows_apps]
windows_app ansible_host=<ip-ou-hostname-do-windows-server>

[windows_apps:vars]
ansible_connection=winrm
ansible_port=5986
ansible_winrm_transport=ntlm
ansible_winrm_scheme=https
ansible_winrm_server_cert_validation=ignore
```

| Variável | Papel |
|---|---|
| `ansible_connection=winrm` | Substitui o SSH; usa o Windows Remote Management. |
| `ansible_port=5986` | Porta do listener WinRM **sobre HTTPS** (5985 é HTTP puro — não usar). |
| `ansible_winrm_transport=ntlm` | Autenticação NTLM (ou `kerberos` em domínio AD). Substitui a chave SSH. |
| `ansible_winrm_scheme=https` | Força HTTPS no transporte. |
| `ansible_winrm_server_cert_validation=ignore` | **Atalho de laboratório** — aceita certificado WinRM auto-assinado. Em produção, usar `validate` com um certificado confiável (análogo do `host_key_checking` do lado SSH). |

---

## 3. Pré-requisitos no alvo Windows

Executados **no Windows Server real** (não no PoC):

1. Habilitar o listener WinRM sobre HTTPS (5986) — via `Enable-PSRemoting` +
   configuração de certificado, ou o script `ConfigureRemotingForAnsible.ps1`
   da própria Ansible.
2. Instalar as coleções Windows **no controller** (a máquina que roda o
   `ansible-playbook` — no PoC, o container Jenkins):

   ```bash
   ansible-galaxy collection install ansible.windows community.windows microsoft.iis
   ```

3. IIS instalado com o ASP.NET Core Hosting Bundle (módulo `AspNetCoreModuleV2`)
   e o Application Pool `BnpPocApiPool` criado.

> No PoC apenas o `ansible-core` está instalado (venv `/opt/ansible-venv` do
> `Jenkinsfile`/imagem Jenkins). As coleções Windows acima **não** são
> instaladas — o deploy Linux não precisa delas.

---

## 4. Playbook equivalente (mapa Linux → Windows)

Mapeamento tarefa a tarefa entre `deploy/ansible/deploy.yml` (executado) e o
equivalente IIS (documentado):

| Passo | Módulo Linux (PoC) | Módulo Windows (IIS) |
|---|---|---|
| Baixar `.sha256` do Nexus | `ansible.builtin.get_url` | `ansible.windows.win_get_url` |
| Baixar artefato + **verificar SHA256** | `ansible.builtin.get_url` (`checksum: sha256:…`) | `ansible.windows.win_get_url` (`checksum: …`, `checksum_algorithm: sha256`) |
| Descompactar release | `ansible.builtin.unarchive` | `community.windows.win_unzip` |
| Instalar/atualizar config do serviço | `ansible.builtin.template` (unit systemd) | `ansible.windows.win_template` / `ansible.windows.win_copy` |
| Reiniciar o serviço | `ansible.builtin.systemd_service` (`state: restarted`) | `microsoft.iis.web_app_pool` (`state: restarted`) |
| Smoke test HTTP | `ansible.builtin.uri` | `ansible.windows.win_uri` |

Detalhes específicos do IIS:

- **Caminho de deploy:** `C:\inetpub\wwwroot\BnpPoc.Api` (análogo do
  `/opt/bnppoc-api/current` no Linux).
- **Recycle do Application Pool:** feito com `microsoft.iis.web_app_pool`
  usando `state: restarted` sobre o pool `BnpPocApiPool`.

  > ⚠️ **Não usar `community.windows.win_iis_webapppool`** — está **deprecado,
  > marcado para remoção na community.windows 4.0.0**. O módulo suportado é
  > `microsoft.iis.web_app_pool`.

- **Sequência de deploy:** parar o Application Pool → substituir os binários em
  `C:\inetpub\wwwroot\BnpPoc.Api` → iniciar o Application Pool. É o análogo
  direto do `install unit → replace binaries → systemctl restart` do Linux.
- **Sem `become`/`sudo`:** no Windows não há passo de escalonamento. As tarefas
  rodam com os privilégios da própria conta que conecta via WinRM — não existe
  análogo do drop-in `NOPASSWD` do sudoers. A conta WinRM deve ter apenas as
  permissões necessárias para gerenciar o IIS e substituir os binários.

---

## 5. Endurecimento para produção

O PoC local assume atalhos aceitáveis para um laboratório descartável e
inaceitáveis em produção. Os três primeiros itens vêm da revisão de segurança da
Fase 4; os últimos são atalhos do próprio ambiente local de CD.

### (a) `TrustServerCertificate=True` desliga a validação do certificado TLS

A connection string do PoC (`…;TrustServerCertificate=True;`) aceita qualquer
certificado apresentado pelo SQL Server, o que anula a proteção contra
man-in-the-middle. **Em produção:** instalar um certificado TLS assinado por uma
CA confiável no SQL Server e remover `TrustServerCertificate` (ou definir
`=False`), de modo que a conexão só prossiga com um certificado válido.

### (b) A aplicação conecta como `sa`

O PoC conecta ao banco como `sa` (privilégio `sysadmin`), exatamente como o
container Jenkins já faz. **Em produção:** criar um login de **privilégio
mínimo** com acesso restrito ao `BnpPocDb` (`db_datareader` + `db_datawriter` +
`EXECUTE` apenas nas procedures necessárias), nunca `sysadmin`.

### (c) Connection string em texto puro no `appsettings.json`

As credenciais do banco ficam em texto puro no `appsettings.json` (e no
`Environment=` da unit systemd do PoC). **Em produção:** externalizar o segredo
— variáveis de ambiente injetadas pelo orquestrador, **Windows Credential
Store**, ou **HashiCorp Vault**. O Ansible injeta o segredo em deploy time; ele
nunca é commitado no repositório.

### (nota PoC) `privileged: true` e `sudo` sem senha

O `apphost` local do PoC roda com `privileged: true` (necessário para bootar o
systemd dentro de um container) e concede ao usuário `deploy` `sudo` **sem
senha** (`NOPASSWD:ALL` em `/etc/sudoers.d/deploy`) para satisfazer o
`become: true` do playbook. São conveniências de laboratório descartável. **Em
produção:** o alvo é uma VM gerenciada separadamente (não um container irmão
privilegiado) e a conta de deploy é de privilégio mínimo — o escalonamento, se
existir, é restrito apenas aos comandos que o deploy realmente executa. No lado
Windows/WinRM esse atalho sequer existe: as tarefas rodam com os privilégios da
conta que conecta, sem `NOPASSWD` de sudoers.

---

## 6. Diferenças de conexão (resumo)

| Aspecto | Linux (PoC, executado) | Windows/IIS (produção, documentado) |
|---|---|---|
| Transporte | SSH | WinRM sobre HTTPS |
| Autenticação | Chave SSH (`apphost-ssh`) | NTLM / Kerberos |
| Escalonamento | `become: true` (`sudo`) | Privilégios da conta WinRM (sem `become`) |
| Confiança do host | `host_key_checking=False` (PoC) | `ansible_winrm_server_cert_validation` |
| Ciclo de vida | `systemd` (`systemctl restart`) | IIS Application Pool (recycle) |
| Caminho de deploy | `/opt/bnppoc-api/current` | `C:\inetpub\wwwroot\BnpPoc.Api` |

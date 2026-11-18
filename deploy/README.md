# Deploy — Revenda.App (MGC Cloud)

Deploy para uma **VM Ubuntu da MagaluCloud**, replicando o padrão do projeto CapZeroApp:
[aspire-ssh-deploy](https://github.com/davidfowl/aspire-ssh-deploy) gera o `docker-compose.yaml`,
faz push das imagens para o container registry da Magalu e roda `docker compose up -d` na VM via SSH.
O **Caddy** no host faz terminação TLS com **certificado Let's Encrypt** para o hostname
`revenda.app.<IP>.sslip.io` (ver [Caddyfile](Caddyfile)) — HTTPS válido, sem aviso do navegador.
O certificado auto-assinado descrito mais abaixo é o caminho alternativo (apenas IP).

## VM alvo (confirmada)
- IP público: `201.23.87.110` · usuário `ubuntu` · região `br-se1-a` · Ubuntu 24.04 LTS
- Chave SSH local: `~/.ssh/mgc`
- Nota: a VM está registrada como hostname `revendaapp`

---

## Setup único na VM (antes do 1º deploy)

Na VM (`ssh -i ~/.ssh/mgc ubuntu@201.23.87.110`):

```bash
# 1. Docker Engine
sudo apt update
sudo apt install -y docker.io docker-compose-v2
sudo systemctl enable --now docker
sudo usermod -aG docker ubuntu   # re-login (exit e ssh de novo) p/ aplicar

# 2. Caddy (terminação TLS)
#    O Caddyfile versionado usa o hostname sslip.io e emite certificado Let's Encrypt
#    automático (ACME HTTP-01 pela porta 80) — não precisa gerar certificado na mão.
sudo apt install -y caddy
sudo cp deploy/Caddyfile /etc/caddy/Caddyfile    # da máquina de dev, ou cole o conteúdo
sudo systemctl enable --now caddy

# 3. Firewall (ufw) + liberar no security group da MGC
sudo ufw allow 22/tcp && sudo ufw allow 80/tcp && sudo ufw allow 443/tcp && sudo ufw enable
# No painel MGC (Rede → security groups): liberar TCP 80 e 443 (origem 0.0.0.0/0).
```

> O seeded `Seed:EnableDemoData=false` em Produção: **nenhum dado de demonstração é criado no deploy**.

---

## Deploy (a partir da máquina de dev)

Pré-requisitos:
- `docker login container-registry.br-se1.magalu.cloud` (conta MGC — push de imagens).
- `src/RevendaApp.AppHost/appsettings.Production.json` preenchido (não versionado):
  `DockerSSH` (VM + chave), `DockerRegistry`, `Parameters` (`postgres-password`,
  `seed-admin-email`, `seed-admin-password`).

```bash
# 1º: o seed agora é controlado. Local (dev) continua normal; produção não sobe dados demo.
# Deploy:

dotnet run --project src/RevendaApp.AppHost -- deploy
#   - (equivalente a `aspire deploy -e Production`)
#   - gera compose/imagens em src/RevendaApp.AppHost/aspire-output/
#   - push p/ container-registry.br-se1.magalu.cloud/revendaapp/*
#   - copia compose+.env via SSH e roda `docker compose up -d` na VM
```

O app fica acessível em **`https://revenda.app.201.23.87.110.sslip.io`** (certificado Let's
Encrypt válido). Login com o email/senha de `Parameters:seed-admin-email/-password`.

> O `deploy.yml` do GitHub Actions roda `aspire deploy -e Production` **depois** que o workflow `CI`
> conclui com sucesso na `main` (`on.workflow_run`), então um build reprovado nunca vai para
> produção. Três detalhes desse gatilho que não são óbvios:
> - `workflow_run` dispara também em falha, por isso o job tem a guarda
>   `conclusion == 'success' && event == 'push'`;
> - o workflow roda a versão que está na default branch, não a do commit que disparou o CI;
> - `github.sha` nesse evento é o **último commit da default branch**, não o commit testado. O
>   checkout usa `workflow_run.head_sha` e o `SHORT_SHA` sai de `git rev-parse --short HEAD`, para a
>   tag da imagem corresponder ao código publicado.
>
> Deploy manual continua por `workflow_dispatch` (o payload não traz `workflow_run`, e a guarda
> libera o job).

---

## Onde cada coisa roda
| Camada | Onde | Detalhe |
|---|---|---|
| Postgres | Container (Docker Compose) | imagem gerada, volume persistente (`revendaapp-postgres-data`) |
| Revenda.App Web | Container | `localhost:8080` HTTP, exposto `8080:8080` e `8443:8080` |
| Caddy | Host (systemd) | `:443` TLS (Let's Encrypt via sslip.io) → `localhost:8080`; `:80` usado pelo ACME |
| Registry | Magalu Container Registry | `container-registry.br-se1.magalu.cloud/revendaapp` |

## Sessões e proxy (hardening)

- **Chaves de Data Protection no Postgres.** As chaves ficam na tabela `DataProtectionKeys` do banco
  (`PersistKeysToDbContext`), não no filesystem efêmero do container. Reiniciar o container **não**
  desloga os usuários, e duas réplicas compartilham as mesmas chaves — a autenticação (cookie e
  tokens antiforgery) sobrevive à troca de instância. O circuito Blazor em si, por ser em memória,
  **não** sobrevive: o usuário cai na tela de reconexão, mas segue autenticado. Não troque isso por
  persistência em arquivo: cada restart geraria chaves novas e invalidaria todos os cookies de
  autenticação e tokens antiforgery já emitidos.
- **`X-Forwarded-*` confiável só de loopback + RFC1918.** O Caddy no host faz `reverse_proxy` para a
  porta publicada do container, então o Kestrel enxerga o gateway da bridge do Docker. Sem esse
  processamento o app acha que o esquema é `http`: o `UseHttpsRedirection`, as URLs absolutas e
  redirects gerados saem com o esquema errado e o HSTS não é aplicado. O cookie de sessão não é
  afetado — fora de Development o `SecurePolicy` é `Always`, independente do proxy. A lista de redes
  é ajustável em `Proxy:RedesConfiaveis` (CIDRs separados por vírgula); um CIDR malformado
  **derruba o startup** de propósito.
- **Recomendado: publique as portas do container só em loopback.** Use `127.0.0.1:8080:8080` e
  `127.0.0.1:8443:8080` no lugar de `8080:8080` e `8443:8080` — o raciocínio vale para **qualquer**
  porta publicada que chegue ao listener HTTP do container, não só a 8080. Hoje elas são alcançáveis
  pela NIC privada da VM: um par na mesma rede poderia falar direto com o Kestrel e forjar
  `X-Forwarded-Proto` (e, com isso, o esquema das URLs e do redirect). Publicando em loopback só o
  Caddy do host chega ao app.

## Atualizar secret do admin depois de criado
O seed do admin é idempotente (`FindByEmailAsync`). Se o `seed-admin-password` mudar após o
primeiro boot, o admin **não** será recriado — altere a senha via SQL na VM ou esvazie o volume.
Mudar as variáveis apenas no deploy subsequente não troca credenciais já gravadas.

## Backup e restauração do Postgres de produção

O Postgres roda como container (`revendaappapphost-postgres-1`) com volume persistente. **Antes de
qualquer deploy que mude dados no bootstrap** — migração, normalização, backfill — tire um dump.

Motivo concreto: a normalização do fiado legado (`NormalizarFiadoLegado`) roda no primeiro boot e
**grava** `Venda.ValorPagoFiado` em todas as vendas fiado. O rollback da migração não desfaz isso.

```bash
# 1. Dump completo (custom format, comprimido) no disco da VM
TS=$(date -u +%Y%m%dT%H%M%SZ)
ssh -i ~/.ssh/mgc ubuntu@201.23.87.110 \
  'docker exec revendaappapphost-postgres-1 sh -c '\''PGPASSWORD="$POSTGRES_PASSWORD" \
   pg_dump -U postgres -d revendaapp --format=custom --compress=9'\''' \
  > ~/backups/revendaapp-$TS.dump

# 2. Traga para fora da VM (backup que só existe no host que ele protege não é backup)
scp -i ~/.ssh/mgc ubuntu@201.23.87.110:~/backups/revendaapp-$TS.dump ~/backups/
sha256sum ~/backups/revendaapp-$TS.dump
```

A senha vem do env do próprio container — não precisa digitá-la nem passá-la na linha de comando
(onde ficaria no histórico e no `ps`).

**Verifique o dump antes de confiar nele.** Restaure num banco descartável e compare as contagens:

```bash
PG='docker exec -i revendaappapphost-postgres-1'
$PG sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -U postgres -d postgres \
           -c "CREATE DATABASE revendaapp_verif;"'
$PG sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" pg_restore -U postgres -d revendaapp_verif \
           --no-owner --no-privileges' < ~/backups/revendaapp-$TS.dump
# compare Vendas, Clientes, Pagamentos, ItensVenda, saldo_total etc. entre os dois bancos
$PG sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -U postgres -d postgres \
           -c "DROP DATABASE revendaapp_verif;"'
```

**Restauração de verdade** (para o banco em uso — derruba o app):

```bash
docker stop revendaappapphost-web-1
$PG sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" dropdb -U postgres --force revendaapp'
$PG sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" createdb -U postgres revendaapp'
$PG sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" pg_restore -U postgres -d revendaapp \
           --no-owner --no-privileges' < ~/backups/revendaapp-$TS.dump
docker start revendaappapphost-web-1
```

Cuidado: ao subir de novo, o bootstrap roda a normalização outra vez se o marcador em
`RegistrosAuditoria` (`Acao = 'FiadoLegadoNormalizado'`) não estiver no dump restaurado. Ela é
idempotente (replay determinístico a partir do histórico), então rodar de novo é seguro — mas o
estado final precisa ser conferido no log do bootstrap, que imprime clientes analisados, vendas
normalizadas e divergências.

## Referências
- `aspire deploy` reference: https://github.com/davidfowl/aspire-ssh-deploy
- Exemplo replicado: `/home/voch_silva/dev/flyve/CapZeroApp` (docs/deploy-https.md)
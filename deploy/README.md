# Runbook: deploy na Oracle Cloud (OCI)

VM ARM Always Free (Ubuntu 24.04 aarch64) rodando Api + Worker + Postgres com
Docker Compose. Design completo: `docs/superpowers/specs/2026-11-11-deploy-oci-design.md`.
A única porta aberta na VM é a 22 (SSH); a Api só escuta em `127.0.0.1:8080`
e é acessada por túnel SSH.

Nos comandos abaixo, `<ip>` é o IP público da VM (não commite o IP).

## 1. Chave SSH (no PC)

```bash
ssh-keygen -t ed25519 -N "" -f ~/.ssh/steamtracker_oci
cat ~/.ssh/steamtracker_oci.pub   # esta é a chave PÚBLICA, a que vai para o console
```

## 2. Criar a VM (console da OCI)

Compute → Instances → Create instance:

- Nome: `steamtracker`
- Imagem: Canonical Ubuntu 24.04 (aarch64)
- Shape: `VM.Standard.A1.Flex`, 2 OCPU / 12 GB
- Rede: VCN com sub-rede pública e IPv4 público atribuído
- SSH keys: colar a chave pública (conteúdo de `~/.ssh/steamtracker_oci.pub`)
- Boot volume: padrão

Se aparecer "Out of capacity": tente outro availability domain ou repita mais
tarde. Anote o IP público.

## 3. Primeiro acesso

```bash
ssh -i ~/.ssh/steamtracker_oci -o StrictHostKeyChecking=accept-new ubuntu@<ip> 'echo ok'
```

## 4. Bootstrap (uma vez; idempotente)

Instala Docker Engine e o plugin Compose e adiciona `ubuntu` ao grupo `docker`:

```bash
ssh -i ~/.ssh/steamtracker_oci ubuntu@<ip> 'sudo bash -s' < deploy/bootstrap.sh
```

O grupo `docker` só vale em novas sessões; o próximo `ssh` já o enxerga.

## 5. Deploy

Da raiz do repositório (Git Bash). Envia o `HEAD` (commite antes), cria o
`.env` na VM se não existir, sobe a stack e falha com exit ≠ 0 se o `migrate`
não terminar com sucesso:

```bash
./deploy/deploy.sh <ip>            # chave padrão ~/.ssh/steamtracker_oci
./deploy/deploy.sh <ip> /caminho/da/chave
```

Repetir o deploy não recria o `.env` nem mexe no volume do banco.

## 6. Operação

Túnel para a Api (deixe aberto; depois acesse `http://127.0.0.1:8080`):

```bash
ssh -i ~/.ssh/steamtracker_oci -N -L 8080:127.0.0.1:8080 ubuntu@<ip>
```

Conecte na VM e use, dentro de `~/steamtracker/src`:

```bash
ssh -i ~/.ssh/steamtracker_oci ubuntu@<ip>
cd ~/steamtracker/src
export STEAMTRACKER_ENV_FILE="$HOME/steamtracker/.env"
alias dc='docker compose -f docker-compose.prod.yml --env-file ~/steamtracker/.env'
```

```bash
dc ps -a                       # estado dos serviços (migrate deve ter Exited (0))
dc logs -f --tail 100 api      # logs (troque por worker, postgres, migrate)
dc restart api worker          # reinício
dc exec postgres psql -U steamtracker   # psql no banco
```

(`STEAMTRACKER_ENV_FILE` e `--env-file` são obrigatórios em qualquer comando
`docker compose` deste arquivo, inclusive `exec` e `logs`.)

## 7. Onde fica o segredo

`~/steamtracker/.env` na VM (`chmod 600`), com `POSTGRES_PASSWORD` gerada na
própria VM no primeiro deploy. Fica fora de `~/steamtracker/src`, que é
apagado a cada deploy. Nada de segredo vai para o git nem para o PC. Opcionais
(ex.: `SteamMarket__Currency`) podem ser adicionados a esse arquivo; veja
`.env.example`.

## 8. Riscos

Capacidade Ampere esgotada, bloqueio de IPs de nuvem pela Steam, sem backup,
chave sem passphrase e recuperação de VM ociosa: veja a seção 8 da spec.

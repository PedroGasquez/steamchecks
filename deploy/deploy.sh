#!/usr/bin/env bash
# Envia o HEAD do repositório para a VM e sobe a stack de produção.
# Uso: ./deploy/deploy.sh <ip> [caminho-da-chave]
set -euo pipefail

if [ $# -lt 1 ] || [ $# -gt 2 ]; then
  echo "Uso: $0 <ip> [caminho-da-chave]" >&2
  echo "  chave padrão: ~/.ssh/steamtracker_oci (usuário remoto: ubuntu)" >&2
  exit 1
fi

IP="$1"
KEY="${2:-$HOME/.ssh/steamtracker_oci}"
REPO_ROOT="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
SSH=(ssh -i "$KEY" "ubuntu@$IP")

if [ ! -f "$KEY" ]; then
  echo "Erro: chave SSH não encontrada em $KEY" >&2
  exit 1
fi

cd "$REPO_ROOT"

if [ -n "$(git status --porcelain)" ]; then
  echo "Aviso: há mudanças não commitadas; só o HEAD será enviado." >&2
fi

echo "==> Enviando código (git archive HEAD) para ~/steamtracker/src"
git archive --format=tar HEAD | "${SSH[@]}" \
  'rm -rf ~/steamtracker/src && mkdir -p ~/steamtracker/src && tar -x -C ~/steamtracker/src'

echo "==> Subindo a stack na VM"
"${SSH[@]}" bash -s <<'REMOTE'
set -euo pipefail
ENV_FILE="$HOME/steamtracker/.env"

if [ ! -f "$ENV_FILE" ]; then
  echo "Criando $ENV_FILE (senha gerada na VM)"
  ( umask 077; printf 'POSTGRES_PASSWORD=%s\n' "$(openssl rand -hex 32)" > "$ENV_FILE" )
  chmod 600 "$ENV_FILE"
else
  echo "$ENV_FILE já existe; mantido."
fi

cd "$HOME/steamtracker/src"
export STEAMTRACKER_ENV_FILE="$ENV_FILE"
compose() { docker compose -f docker-compose.prod.yml --env-file "$ENV_FILE" "$@" </dev/null; }

# O 'up' sai != 0 quando o migrate falha; captura para ainda mostrar logs.
up_rc=0
compose up -d --build || up_rc=$?

compose ps -a
compose logs --tail 20 migrate || true

migrate_id="$(compose ps -a -q migrate || true)"
migrate_rc=""
if [ -n "$migrate_id" ]; then
  migrate_rc="$(docker inspect -f '{{.State.ExitCode}}' "$migrate_id" || true)"
fi

if [ "$up_rc" -ne 0 ] || [ "$migrate_rc" != "0" ]; then
  echo "ERRO: deploy falhou (docker compose up: $up_rc; migrate exit code: ${migrate_rc:-desconhecido})." >&2
  exit 1
fi
echo "Deploy concluído: migrate terminou com sucesso."
REMOTE

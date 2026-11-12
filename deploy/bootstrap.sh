#!/usr/bin/env bash
# Instala Docker Engine + plugin Compose na VM (Ubuntu 24.04). Idempotente.
# Uso (do PC do dono):
#   ssh -i ~/.ssh/steamtracker_oci ubuntu@<ip> 'sudo bash -s' < deploy/bootstrap.sh
set -euo pipefail

if [ "${EUID:-$(id -u)}" -ne 0 ]; then
  echo "Erro: rode como root, por exemplo:" >&2
  echo "  ssh -i ~/.ssh/steamtracker_oci ubuntu@<ip> 'sudo bash -s' < deploy/bootstrap.sh" >&2
  exit 1
fi

export DEBIAN_FRONTEND=noninteractive NEEDRESTART_MODE=a

apt-get update </dev/null
apt-get install -y ca-certificates curl </dev/null

install -m 0755 -d /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc </dev/null
chmod a+r /etc/apt/keyrings/docker.asc

# shellcheck disable=SC1091
. /etc/os-release
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu ${UBUNTU_CODENAME:-$VERSION_CODENAME} stable" \
  > /etc/apt/sources.list.d/docker.list

apt-get update </dev/null
apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin </dev/null

systemctl enable --now docker </dev/null
usermod -aG docker ubuntu </dev/null

docker --version
docker compose version
echo "Bootstrap concluído. Reconecte via SSH para o grupo docker valer para o usuário ubuntu."

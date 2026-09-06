#!/usr/bin/env bash
# 在 VM 上跑一次（deploy-relay.ps1 每次部署都會呼叫）：裝 Caddy、建使用者與目錄、掛 systemd。可重跑。
set -euo pipefail

if ! command -v caddy >/dev/null 2>&1; then
  sudo apt-get update -qq
  sudo apt-get install -y -qq debian-keyring debian-archive-keyring apt-transport-https curl gnupg
  curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | sudo gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg --yes
  curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' | sudo tee /etc/apt/sources.list.d/caddy-stable.list >/dev/null
  sudo apt-get update -qq
  sudo apt-get install -y -qq caddy
fi

id -u tgosrelay >/dev/null 2>&1 || sudo useradd --system --no-create-home --shell /usr/sbin/nologin tgosrelay
sudo mkdir -p /opt/tgos-relay
sudo systemctl stop tgos-relay 2>/dev/null || true
sudo cp ~/relay-upload/tgos-relay /opt/tgos-relay/tgos-relay
sudo chmod 755 /opt/tgos-relay/tgos-relay
sudo chown -R tgosrelay:tgosrelay /opt/tgos-relay

if [ ! -f /etc/tgos-relay.env ]; then
  sudo cp ~/relay-upload/tgos-relay.env.example /etc/tgos-relay.env
  sudo chmod 600 /etc/tgos-relay.env
  echo "!! /etc/tgos-relay.env 是範例，請填 RELAY_KEY（與 TGOS 金鑰）後 systemctl restart tgos-relay"
fi

sudo cp ~/relay-upload/tgos-relay.service /etc/systemd/system/tgos-relay.service
sudo cp ~/relay-upload/Caddyfile /etc/caddy/Caddyfile
sudo systemctl daemon-reload
sudo systemctl enable --now tgos-relay
sudo systemctl restart tgos-relay
sudo systemctl reload caddy || sudo systemctl restart caddy
sleep 2
curl -s http://127.0.0.1:5000/healthz && echo

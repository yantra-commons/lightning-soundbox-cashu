#!/bin/sh
# Brings up the regtest stack and wires everything together:
#   bitcoind + NBXplorer + Postgres, two LND nodes with a channel between
#   them, a Nutshell mint on one node, BTCPay Server on the other with BOTH
#   plugins installed, a store, its Lightning connection, Cashu enabled, and
#   one paired (simulated) soundbox.
#
# Prints the environment variables the E2E tests need. Run from this folder:
#   ./setup.sh            then paste the exports it prints
#   ./setup.sh down       tears everything down
#
# Needs: docker compose, python (3), curl, and both plugins built in Release.
set -e
PY=$(command -v python3 || command -v python)
cd "$(dirname "$0")"

if [ "$1" = "down" ]; then docker compose down -v; exit 0; fi

ROOT=$(cd .. && pwd)
SOUNDBOX_PLUGIN=${SOUNDBOX_PLUGIN:-$ROOT/../lightning-soundbox-oss/plugin/BTCPayServer.Plugins.LightningSoundbox/bin/Release/net8.0}
CASHU_PLUGIN=$ROOT/plugin/BTCPayServer.Plugins.SoundboxCashu/bin/Release/net8.0

# Only the plugin assemblies and their private dependencies -- BTCPay
# supplies EF Core, Npgsql and Microsoft.Extensions itself.
mkdir -p plugins/BTCPayServer.Plugins.SoundboxCashu plugins/BTCPayServer.Plugins.LightningSoundbox
cp "$CASHU_PLUGIN/BTCPayServer.Plugins.SoundboxCashu.dll" "$CASHU_PLUGIN/NBitcoin.Secp256k1.dll" plugins/BTCPayServer.Plugins.SoundboxCashu/
cp "$SOUNDBOX_PLUGIN/BTCPayServer.Plugins.LightningSoundbox.dll" plugins/BTCPayServer.Plugins.LightningSoundbox/

docker compose up -d

bcli()  { docker compose exec -T bitcoind bitcoin-cli -regtest -rpcport=43782 -rpcuser=rpc -rpcpassword=rpc -rpcwallet=e2e "$@"; }
mine()  { bcli generatetoaddress "$1" "$(bcli getnewaddress)" >/dev/null; }
lncli() { node=$1; shift; docker compose exec -T "$node" lncli --network=regtest --no-macaroons --rpcserver=127.0.0.1:10008 "$@"; }
json()  { $PY -c "import sys,json;print(json.load(sys.stdin)$1)"; }
psql()  { docker compose exec -T postgres psql -U postgres -d btcpay -At -c "$1"; }

echo "== chain"
until docker compose exec -T bitcoind bitcoin-cli -regtest -rpcport=43782 -rpcuser=rpc -rpcpassword=rpc getblockchaininfo >/dev/null 2>&1; do sleep 2; done
docker compose exec -T bitcoind bitcoin-cli -regtest -rpcport=43782 -rpcuser=rpc -rpcpassword=rpc createwallet e2e >/dev/null 2>&1 || true
mine 101
until lncli merchant_lnd getinfo >/dev/null 2>&1 && lncli mint_lnd getinfo >/dev/null 2>&1; do sleep 2; done
bcli sendtoaddress "$(lncli merchant_lnd newaddress p2wkh | json "['address']")" 1 >/dev/null
bcli sendtoaddress "$(lncli mint_lnd newaddress p2wkh | json "['address']")" 1 >/dev/null
mine 6
until [ "$(lncli merchant_lnd walletbalance | json "['confirmed_balance']")" != "0" ]; do sleep 2; done

echo "== channel merchant <-> mint (2.5M sat each side)"
MINTPUB=$(lncli mint_lnd getinfo | json "['identity_pubkey']")
lncli merchant_lnd connect "$MINTPUB@mint_lnd:9735" >/dev/null 2>&1 || true
lncli merchant_lnd openchannel --node_key "$MINTPUB" --local_amt 5000000 --push_amt 2500000 >/dev/null
mine 6
until [ "$(lncli merchant_lnd listchannels | json "['channels'][0]['active']" 2>/dev/null)" = "True" ]; do sleep 2; done

echo "== btcpay"
B=http://127.0.0.1:5202
until curl -sf $B/api/v1/health >/dev/null; do sleep 2; done
curl -sf -X POST $B/api/v1/users -H "Content-Type: application/json" \
  -d '{"email":"e2e@example.com","password":"e2ePassw0rd!","isAdministrator":true}' >/dev/null
AUTH="e2e@example.com:e2ePassw0rd!"
STORE=$(curl -sf -u "$AUTH" -X POST $B/api/v1/stores -H "Content-Type: application/json" \
  -d '{"name":"Cashu E2E","defaultCurrency":"SATS"}' | json "['id']")
curl -sf -u "$AUTH" -X PUT "$B/api/v1/stores/$STORE/payment-methods/BTC-LN" -H "Content-Type: application/json" \
  -d '{"enabled":true,"config":{"connectionString":"type=lnd-rest;server=http://merchant_lnd:8080/;allowinsecure=true"}}' >/dev/null

# Cashu settings are written directly because the settings form (rightly)
# refuses a plain-http mint, and this mint is http inside the docker network.
psql "INSERT INTO \"BTCPayServer.Plugins.SoundboxCashu\".\"Settings\" (\"StoreId\",\"Enabled\",\"MintUrl\",\"ServerUrl\",\"UpdatedAt\") VALUES ('$STORE', true, 'http://nutshell:3338', '$B', now());" >/dev/null

# Pair a simulated soundbox through the Lightning Soundbox plugin's real
# claim endpoint.
psql "INSERT INTO \"BTCPayServer.Plugins.LightningSoundbox\".\"PairingTokens\" (\"Token\",\"StoreId\",\"Label\",\"Language\",\"CreatedAt\",\"ExpiresAt\") VALUES ('E2ETEST1', '$STORE', 'Counter', 'en', now(), now() + interval '15 minutes');" >/dev/null
DEVICE=$(curl -sf -X POST $B/plugins/lightningsoundbox/api/v1/pair/claim -H "Content-Type: application/json" \
  -d '{"token":"E2ETEST1","mac":"00:00:00:00:00:01","firmware":"e2e"}')

echo
echo "export E2E_COMPOSE_DIR='$(pwd)'"
echo "export E2E_BTCPAY=$B E2E_STORE=$STORE"
echo "export E2E_MINT=http://127.0.0.1:5203 E2E_MINT_AS_CONFIGURED=http://nutshell:3338 E2E_PAYER_LND=http://127.0.0.1:5204"
echo "export E2E_DEVICE_ID=$(echo "$DEVICE" | json "['device_id']") E2E_DEVICE_SECRET='$(echo "$DEVICE" | json "['shared_secret']")'"

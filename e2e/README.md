# Regtest end-to-end stack

This folder runs a complete, local, fake-money copy of the real deployment:

| Service | Role |
|---|---|
| `bitcoind`, `nbxplorer`, `postgres` | The regtest chain and BTCPay's dependencies. |
| `merchant_lnd` | The store's Lightning node. It also plays the customer, buying ecash from the mint. |
| `mint_lnd` | The mint's Lightning node. Each node has 2.5M sats of channel balance with the other. |
| `nutshell` | Cashu mint 0.21.0, with 100 ppk input fees so the fee handling is tested. |
| `btcpay` | BTCPay Server **2.4.7**, running both the Soundbox Cashu plugin and the unmodified Lightning Soundbox plugin. |

Ports are published on 127.0.0.1 only:
- BTCPay: 5202
- mint: 5203
- merchant LND REST: 5204
- mint LND REST: 5205
- Postgres: 5206

## Run it

First build both plugins in Release: this repo's plugin, and
`../lightning-soundbox-oss`'s plugin. Set `SOUNDBOX_PLUGIN` if that build is
somewhere else. Then:

```bash
./setup.sh          # takes a few minutes the first time; prints exports
# paste the printed exports, then:
cd ../plugin/BTCPayServer.Plugins.SoundboxCashu.Tests
dotnet test --filter Category=E2E

./setup.sh down     # removes everything, including volumes
```

On Windows, set `E2E_COMPOSE_DIR` to the Windows path of this folder (for
example `C:/…/e2e`). The test starts `docker compose` with that path as its
working directory.

The test account is `e2e@example.com` / `e2ePassw0rd!`, and the dashboard is at
`http://127.0.0.1:5202/plugins/sbcashu/<storeId>`. Don't use Basic auth to
poll the Greenfield API in a loop: BTCPay 2.4 throttles password logins, and
the account will stop accepting them for a while.

# Soundbox Cashu

**Cashu ecash payments for the [Lightning Soundbox](https://github.com/yantra-commons/Lightning-Soundbox):
a customer pays with ecash, and the box on the counter says the amount out loud.**

The Lightning Soundbox is an ESP32 point-of-sale device. It shows a payment QR
all day and speaks each payment as it arrives, the way UPI soundboxes do in
Indian shops. This project adds [Cashu](https://cashu.space) to it as a
**separate BTCPay Server plugin**. The Soundbox firmware, its plugin and its
repository are not modified at all.

```
 customer's Cashu wallet                 merchant's BTCPay Server                       shop counter
 ───────────────────────                 ────────────────────────                       ────────────
  scans the creqA… QR      POST ecash    Soundbox Cashu plugin
  types an amount   ───────────────────▶  1. swap at the mint  ──▶ ecash is final
                    (NUT-18 "post")       2. create a normal BTCPay Lightning invoice
                                          3. melt: the mint pays that invoice
                                                     │
                                                     ▼  invoice settled (ordinary LN payment)
                                          Lightning Soundbox plugin (unchanged) ──────▶ "nine hundred
                                                                                         ninety seven sats"
```

## Why it's built this way

**The ecash ends up on the merchant's own Lightning node, not at a mint.** Ecash
the store receives is held only for the few seconds between the swap and the
melt. After that the merchant has ordinary sats on their own node, so the
self-custody of the original Soundbox design holds. The mint is trusted for
those few seconds and no longer.

**There are no changes to the Soundbox.** BTCPay is the only connection between
the two plugins. A Cashu payment becomes a settled Lightning invoice, and the
Soundbox plugin already announces settled Lightning invoices. As a result:
- Units that are already deployed get Cashu without being reflashed.
- The Cashu plugin can be installed or removed without touching the Soundbox
  plugin.
- Webhooks, the invoice list and accounting all see Cashu sales as normal sales.

**It's a static QR, like the Soundbox's existing LNURL code.** The payment
request carries no amount. The customer types what they owe, and the merchant
never rings anything up. The plugin generates the `creqA…` string, and you
paste it into the device's existing **payment code** field.

## What the customer needs

A Cashu wallet that pays [NUT-18](https://github.com/cashubtc/nuts/blob/main/18.md)
payment requests over the `post` transport, holding ecash from the mint the
store has chosen.
The Cashu spec repository lists NUT-18 support in cashu.me, Minibits, Sovran,
cashu-ts and cdk. The plugin's endpoint answers the CORS preflight that browser
wallets send, and it accepts the payload the way cashu-ts writes it (amounts
as strings, optional DLEQ data).

> **Not yet tested with a phone.** Everything below has been verified in code
> and against a real mint, but no consumer wallet app has paid a real
> Soundbox yet. That is the next step. See *Status*.

## Repository layout

| Path | What it is |
|---|---|
| `plugin/BTCPayServer.Plugins.SoundboxCashu/` | The BTCPay plugin. |
| `  Cashu/` | The protocol layer, with no BTCPay dependency: blind-signature crypto (NUT-00), NUT-18 and V4-token encoding, the mint HTTP client, and the wallet operations. |
| `  Services/CashuWalletService.cs` | Receive → settle → recover. All of the money-handling logic is here. |
| `  Services/StoreInvoiceCreator.cs` | The single point where this plugin touches BTCPay invoicing. |
| `  Controllers/CashuPayController.cs` | The public NUT-18 endpoint that wallets POST to. |
| `plugin/BTCPayServer.Plugins.SoundboxCashu.Tests/` | Spec-vector tests, live-mint tests and end-to-end tests. |
| `e2e/` | A one-command regtest stack: BTCPay, both plugins, a Nutshell mint and two LND nodes. |

## Money safety

Each mint call follows the same rule. Anything the mint might sign is written
to the database **before** the request is sent. Local state is rolled back only
when the mint explicitly refuses the request. A timeout or a dropped connection
leaves the operation in place, and the recovery pass resolves it:

| Interrupted during | How it is recovered |
|---|---|
| the swap | NUT-09 `restore` asks the mint for signatures on the stored outputs. If it signed them, the payment is credited. If it didn't, nothing happened. |
| the melt | The plugin checks the quote state. `PAID`: it restores the NUT-08 change. `UNPAID`: NUT-07 `checkstate` confirms the inputs are unspent before they are returned to the pool. |

Settlement that fails because the node is offline or the mint can't route is
retried with backoff: 1, 2, 4 … minutes, up to an hour. The customer is never
affected, because their payment was final at the swap. The dashboard shows the
held balance and the last error. If Lightning settlement can't be fixed, the
merchant can export the ecash as a `cashuB` token.

**Fees.** The merchant receives the customer's amount minus the mint's input
fees (`input_fee_ppk`, on both the swap and the melt) and the actual Lightning
routing fee. The Lightning fee *reserve* returns as NUT-08 change and becomes a
small float that covers the reserve on the next payment, so payments settle
close to in full. On the regtest stack, with 100 ppk input fees, 1000 sats of
ecash settles as 996–999 sats.

## Install

Build the plugin against the BTCPay version you run. The same `net8.0` ceiling
applies as for the Soundbox plugin; see its README. The build output was
verified on **BTCPay 2.4.7**.

```bash
cd plugin
git clone --branch v2.3.0 --depth 1 https://github.com/btcpayserver/btcpayserver btcpayserver
dotnet build BTCPayServer.Plugins.SoundboxCashu/BTCPayServer.Plugins.SoundboxCashu.csproj -c Release
```

Package `BTCPayServer.Plugins.SoundboxCashu.dll` and `NBitcoin.Secp256k1.dll`
from `bin/Release/net8.0/` into a zip named
`BTCPayServer.Plugins.SoundboxCashu.btcpay`. Leave out the EF Core, Npgsql and
`Microsoft.Extensions.*` assemblies, because BTCPay supplies those. Upload it
at **Server Settings → Plugins**.

Then go to **Store → Integrations → Soundbox Cashu**:
1. Turn it on and enter an `https://` mint URL. The plugin checks that the mint
   responds and has an active sat keyset.
2. Copy the payment request it shows.
3. Under **Lightning Soundbox**, paste it into the device's payment code field.

The store must have a working Lightning node, because that is where payments
settle.

## Tests

```bash
cd plugin/BTCPayServer.Plugins.SoundboxCashu.Tests

dotnet test --filter "Category!=Live&Category!=E2E"   # spec vectors, offline (runs in CI)
dotnet test --filter Category=Live                    # against the public test mint, testnut.cashu.space
```

**Spec vectors (27 tests).** These cover hash_to_curve, blinding, the
blind-signature arithmetic, NUT-18 encoding and V4 token encoding. All of them
are byte-exact against the official test vectors in
[cashubtc/nuts](https://github.com/cashubtc/nuts/tree/main/tests).

**Live mint (2 tests).** These run against testnut.cashu.space, which charges
100 ppk fees. They cover:
- swapping ecash
- rejection of a double spend (code 11001)
- NUT-09 restore returning identical proofs
- a melt with NUT-08 change
- spending that change again

**End to end (2 tests).** These need the regtest stack, so run
`e2e/setup.sh` first and paste the exports it prints. Then run
`dotnet test --filter Category=E2E`. The tests act as both the customer and
the device:
- **Normal payment:** a wallet pays ecash with a CORS preflight. The test
  long-polls the Lightning Soundbox device API with the device's HMAC
  signature, exactly as the ESP32 firmware does, and receives the
  `payment_settled` event the box would speak.
- **Node offline:** the merchant's Lightning node is stopped while a customer
  pays. The payment is still accepted, and the box announces it about 90
  seconds after the node comes back.

## Status

Done and verified (see Tests):
- Receiving ecash, with mint, unit, keyset and size checks.
- Swap, crash recovery, and automatic Lightning settlement with retry.
- Export of held ecash as a token.
- The merchant dashboard.
- The Soundbox announcement, which needs no Soundbox changes.

Not done:
- **No consumer wallet has paid a real device yet.** The next step is to point
  cashu.me (or Minibits) at a mainnet store and confirm the wallet completes
  the `post` transport flow with an amountless request.
- The settings form requires an `https://` mint. A plain-http mint, which is
  only realistic on a LAN or over Tor, can only be configured through the
  database.
- Only one mint per store.
- The plugin does not verify DLEQ proofs. It doesn't need to for safety,
  because the swap at the mint is the authoritative check, but checking them
  would let it reject forged ecash without a round trip to the mint.

## Licence

MIT. See [LICENSE](LICENSE).

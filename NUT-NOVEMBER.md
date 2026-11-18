# Nut November 2026 submission notes

The contest rules ask existing projects to separate what was built in November
from what already existed. This file does that.

## What existed before November 1

The [Lightning Soundbox](https://github.com/yantra-commons/Lightning-Soundbox):
- ESP32 firmware
- a BTCPay plugin for pairing and device events
- spoken payment announcements in English, Hindi and Kannada

It was Lightning-only, and its last commit before this work was on
2026-10-17. **It contains no Cashu code, and this project does not modify it.**

## What was built in November (everything in this repository)

**All of this repository is new work.** None of it is copied from the
Soundbox repo:

- **A Cashu protocol layer in C#** (`plugin/.../Cashu/`), built on
  NBitcoin.Secp256k1:
  - hash_to_curve, blinding and unblinding (NUT-00)
  - a minimal CBOR encoder for NUT-18 `creqA` payment requests and V4 `cashuB`
    tokens
  - a mint client covering keysets, swap, melt, restore and checkstate (NUTs
    01/02/03/05/07/08/09/23)
  - NUT-02 fee arithmetic
- **The Soundbox Cashu BTCPay plugin**, which:
  - receives ecash through a NUT-18 `post` transport endpoint
  - swaps it at the merchant's mint
  - settles it automatically to the store's own Lightning node through an
    ordinary BTCPay invoice
  - recovers safely from interrupted swaps and melts
  - lets the merchant export held ecash as a token
  - includes a merchant dashboard
- **Tests:**
  - byte-exact checks against the official NUT-00 and NUT-18 test vectors
  - live tests against testnut.cashu.space
  - two end-to-end tests on a regtest stack (BTCPay 2.4.7, Nutshell 0.21.0,
    two LND nodes), which drive the real Soundbox device API
- **A one-command regtest environment** (`e2e/`).

## The idea, in one paragraph

The UPI soundbox is everywhere in Indian shops because the shopkeeper doesn't
have to look at anything: the box tells them they've been paid. The Lightning
Soundbox brought that to Bitcoin. This project adds ecash to it without making
the merchant a custodian. A customer pays from any Cashu wallet. The ecash is
redeemed at the merchant's mint and forwarded to the merchant's own Lightning
node within seconds, and the box speaks the amount. The design keeps Cashu
entirely outside the device and its plugin, so every Soundbox already deployed
can accept ecash without a firmware update.

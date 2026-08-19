# Back Office API (`OperationsApi`) — Frontend Reference

Everything below reflects the **actual current code** in `src/Api/OperationsApi` as of 2026-07-29 — not
the target design, the real request/response shapes. Endpoints not listed here do not exist yet (see
"Not built yet" at the bottom); do not build frontend screens for them.

## Base

- Host project: `src/Api/OperationsApi/CryptoPaymentEngine.Api.OperationsApi`
- All routes below are prefixed as shown (already include `/api/v1/ops/...`).
- Dev/staging base URL: check that host's `launchSettings.json` (Development) or the staging deployment
  docs (`docs/ec2-staging.md`) for the actual port — it is **not** the same port as `MerchantGateway`.
- `GET /health` exists, unauthenticated, returns `{ "status": "healthy" }`.
- Dev-only Swagger is mounted at `/swagger` when running in Development.

## Response envelope (every endpoint)

Every response — success or failure — is JSON shaped like this (built ad hoc per endpoint, not a
formal ProblemDetails/RFC-9457 mapper yet, so treat this shape as the contract):

```json
{
  "isSuccess": true,
  "data": { /* endpoint-specific, see below */ },
  "error": null
}
```

On failure, `isSuccess` is `false`, `data` is omitted/absent, and `error` is a human-readable string
(not a machine error code today — don't switch UI behavior on the text, only on the HTTP status).

## Auth

All routes require a Bearer session token **except** `POST /auth/login`, `GET /health`, and `/swagger`.

```
Authorization: Bearer <token>
```

Missing/invalid header → `401` with `{ isSuccess: false, error: "Missing or invalid Authorization header. Expected 'Bearer <token>'." }`.
Invalid/expired/revoked session → `401` with the validator's error message.

Two roles: **Admin** (can do everything) and **Viewer** (read-only). Routes marked 🔒**Admin** below
return `403` with `{ isSuccess: false, error: "Admin role required." }` for a Viewer token.

---

### `POST /api/v1/ops/auth/login`
No auth required.

**Request**
```json
{
  "username": "string, required, max 64",
  "password": "string, required, max 256"
}
```

**Response 200**
```json
{
  "isSuccess": true,
  "data": {
    "token": "string — bearer token, use in Authorization header from now on",
    "expiresAt": "2026-07-29T12:00:00Z",
    "role": "Admin" 
  },
  "error": null
}
```
`role` is `"Admin"` or `"Viewer"`.

**Response 401** (bad credentials) — `{ isSuccess: false, error: "<message>" }`.

---

### `POST /api/v1/ops/auth/logout`
Requires auth (any role). Reads the same `Authorization: Bearer <token>` header and revokes that session.

**Request**: no body.

**Response 200**
```json
{ "isSuccess": true, "data": { "loggedOut": true }, "error": null }
```

---

## Merchants

### `GET /api/v1/ops/merchants`
Any authenticated role. Query params: `page` (default 1), `pageSize` (default 50, max 200).

**Response 200**
```json
{
  "isSuccess": true,
  "data": {
    "page": 1,
    "pageSize": 50,
    "totalCount": 42,
    "items": [
      {
        "merchantId": "guid",
        "merchantCode": "ACME-1",
        "name": "Acme Payments",
        "status": "Active",
        "settlementDelayDays": 0,
        "createdAt": "2026-07-20T06:00:00Z",
        "hasActiveCredential": true,
        "allowedIps": ["1.2.3.4"],
        "settlementWallets": [{ "chain": "Tron", "address": "T..." }]
      }
    ]
  },
  "error": null
}
```
`status` is the merchant's status enum as a string (e.g. `Pending`, `Active`, `Frozen`, `Closed` —
confirm exact values against `Merchant.Domain.MerchantStatus` if you need a fixed dropdown list). `Frozen`
is the reversible admin risk-hold; the status toggle (`PATCH .../status` with `active: false`) freezes,
`active: true` unfreezes (returns to `Active`). A frozen merchant is blocked from all transacting.

### `GET /api/v1/ops/merchants/{id}`
Any authenticated role. `{id}` is a GUID.

**Response 200**: `data` is a single item shaped exactly like one entry in the list above.
**Response 404**: merchant not found — `{ isSuccess: false, error: "Merchant not found." }`.

### `GET /api/v1/ops/merchants/{id}/allowed-ips`
Any authenticated role.

**Response 200**
```json
{ "isSuccess": true, "data": { "merchantId": "guid", "allowedIps": ["1.2.3.4"] }, "error": null }
```
**Response 404**: merchant not found.

### `POST /api/v1/ops/merchants` 🔒 Admin
Creates a merchant. **This single call also activates the merchant** — there is no separate "create then
activate" step in the UI to build. It seeds **exactly one** deposit wallet (not a pool) so the merchant's
very first `/deposit` call doesn't pay the provisioning cost synchronously — every wallet after that is
still on-demand, PaymentIntent reuses a free one or mints a new one only when none is free. If the seed
wallet fails to provision, merchant creation still succeeds (`wallet` comes back `null`) — the first
deposit call just provisions synchronously instead.

**Request**
```json
{
  "merchantCode": "string, required, max 64",
  "name": "string, required, max 256",
  "callbackUrl": "string, optional, must be a valid absolute URL if present"
}
```

**Response 200**
```json
{
  "isSuccess": true,
  "data": {
    "merchantId": "guid",
    "merchantCode": "ACME-1",
    "apiKey": "string",
    "apiSecret": "string",
    "signingSecret": "string",
    "wallet": { "chain": "Tron", "address": "T..." }
  },
  "error": null
}
```
`wallet` is `null` if the seed provisioning failed (logged server-side) — this does not indicate a problem
the UI needs to surface beyond maybe a subtle note; the merchant is fully usable regardless.
**IMPORTANT for the UI**: `apiSecret` and `signingSecret` are shown **exactly once**, here, and are
never retrievable again (not even via `GET /merchants/{id}`). The frontend must show these prominently
with a copy button and an explicit "save this now, we cannot show it again" warning — same as the
`regenerate-key` response below.

**Response 400**: registration failed (e.g. duplicate `merchantCode`, invalid callback URL) —
`{ isSuccess: false, error: "<message>" }`.
**Response 500**: registration succeeded but the auto-activation step failed (rare/edge case, still
worth handling — show a generic error and tell the user to check with engineering, the merchant may
exist in a half-set-up state).

### `PATCH /api/v1/ops/merchants/{id}/status` 🔒 Admin
Activate or **freeze** a merchant. Freezing is a reversible admin risk-hold: a frozen merchant is blocked
from all transacting (new deposit addresses, user payouts, and earnings cash-out) until re-activated. Funds
already sent on-chain to an issued deposit address are still credited — freeze stops new activity, not recording.

**Request**
```json
{ "active": true }
```
`true` → activate (also **unfreezes**), `false` → **freeze**.

**Response 200**
```json
{ "isSuccess": true, "data": { "merchantId": "guid", "status": "Active" }, "error": null }
```
`status` is `Active` or `Frozen` (or `Pending`/`Closed`). **Response 400**: invalid transition (e.g. trying
to activate a Closed merchant) — `{ isSuccess: false, error: "<message>" }`.

### `PUT /api/v1/ops/merchants/{id}/settlement-period` 🔒 Admin
Sets the merchant's settlement period **T+N** in whole days (`0` = T+0, immediately withdrawable). Deposits
mature into withdrawable funds N days after confirmation (UTC calendar day). Gates the withdrawable balance
for **both** user payouts and the merchant's own cash-out. Read it back from `GET /ops/merchants/{id}`
(`settlementDelayDays`).

**Request** `{ "days": 1 }` — validated 0–30.
**Response 200** `{ "isSuccess": true, "data": { "merchantId": "guid", "settlementDelayDays": 1 }, "error": null }`.

### `PUT /api/v1/ops/merchants/{id}/settlement-wallet` 🔒 Admin
Registers/updates the merchant's whitelisted **cash-out (settlement) wallet** for a chain — the fixed
destination of a merchant earnings cash-out (never client-supplied). One per chain. Read them back from
`GET /ops/merchants/{id}` (`settlementWallets: [{ chain, address }]`).

**Request** `{ "chain": "Tron", "address": "T..." }`.
**Response 200** `{ "isSuccess": true, "data": { "merchantId": "guid", "network": "Tron", "address": "T..." }, "error": null }`.

### `PUT /api/v1/ops/merchants/{id}/fees` 🔒 Admin
Declares the merchant's per-asset **flat + %** fee for deposits and withdrawals. Fixed components are in
**display** units (converted to base units at the edge; a zero fixed component = pure-% pricing); percentages
are basis points (`100` = 1%). Read back at `GET /ops/merchants/{id}/fees`.

**Request**
```json
{ "chain": "Tron", "coin": "USDT",
  "depositFeeFixed": 0, "depositFeeBps": 100,
  "withdrawalFeeFixed": 1, "withdrawalFeeBps": 50 }
```
**Response 200** `{ "isSuccess": true, "data": { "merchantId": "guid", "assetId": "guid", "coin": "USDT", "network": "Tron" }, "error": null }`.

### `PUT /api/v1/ops/merchants/{id}/withdrawal-cap` 🔒 Admin
Sets the **merchant-withdrawal (cash-out) liquidity cap** for one asset — distinct from user min/max. An
optional flat cap (display units; omit or `null` = no flat cap) plus a percentage-of-settled-balance cap in
basis points (`0` = no percent cap). Both unset ⇒ no cap (cash out up to the settled balance). Read back
alongside the fees at `GET /ops/merchants/{id}/fees` (`merchantWithdrawalFlatCap`, `merchantWithdrawalPercentBps`).

**Request** `{ "chain": "Tron", "coin": "USDT", "flatCap": 1000, "percentBps": 5000 }` (here: min(1000 USDT, 50% of settled)).
**Response 200** `{ "isSuccess": true, "data": { "merchantId": "guid", "assetId": "guid", "coin": "USDT", "network": "Tron" }, "error": null }`.

### `PUT /api/v1/ops/merchants/{id}/withdrawal-limits` 🔒 Admin
Sets the per-merchant **user-withdrawal min/max** for one asset (the limits gating an *end-user payout*, distinct
from the cash-out cap). Values are display units. Each bound is an **override** of the platform config limit
(`Withdrawal:Policies`): omit or `null` = unset ⇒ config applies for that bound; a set value (including `0` = "no
minimum") fully overrides — staff can raise or lower a merchant's limits. Read back alongside the fees at
`GET /ops/merchants/{id}/fees` (`minimumWithdrawal`, `maximumWithdrawal`; `null` = using config).

**Request** `{ "chain": "Tron", "coin": "USDT", "minimum": 5, "maximum": 10000 }` (either may be null).
**Response 200** `{ "isSuccess": true, "data": { "merchantId": "guid", "assetId": "guid", "coin": "USDT", "network": "Tron" }, "error": null }`.
**Response 400**: `minimum > maximum`, a negative/over-precise value, or unknown chain/coin.

### `POST /api/v1/ops/merchants/{id}/regenerate-key` 🔒 Admin
Revokes the merchant's current credential and issues a new one. No request body.

**Response 200**
```json
{
  "isSuccess": true,
  "data": {
    "apiKey": "string",
    "apiSecret": "string",
    "signingSecret": "string",
    "warning": "Store both values securely — they will never be shown again. The previous credential is now revoked."
  },
  "error": null
}
```
Same one-time-display UI treatment as `apiSecret`/`signingSecret` above. Note this **immediately breaks
the old credential** — the UI should confirm ("this merchant's current API key will stop working
immediately") before calling it.

**Response 400**: merchant not found or other failure.

### `PUT /api/v1/ops/merchants/{id}/allowed-ips` 🔒 Admin
Replaces the merchant's entire IP allowlist (not additive — send the full desired list).

**Request**
```json
{ "ipAddresses": ["1.2.3.4", "5.6.7.8"] }
```

**Response 200**
```json
{
  "isSuccess": true,
  "data": {
    "merchantId": "guid",
    "allowedIps": ["1.2.3.4", "5.6.7.8"],
    "invalidIps": ["not-an-ip"],
    "cloudflare": { "added": 1, "removed": 0 }
  },
  "error": null
}
```
Invalid IP strings in the request are silently filtered out and reported back in `invalidIps` rather
than rejecting the whole request — **unless every single IP submitted was invalid**, in which case
nothing changes and you get:

**Response 400** (all-invalid case): `{ isSuccess: false, error: "No valid IPs provided. Invalid: <list>. Existing allowed IPs are unchanged." }`.

This call also pushes changes to Cloudflare's WAF allowlist synchronously — expect it to be slower than
a typical CRUD call; show a loading state.

---

## Payment Intents (deposit invoices)

### `POST /api/v1/ops/payment-intents/{reference}/fail` 🔒 Admin
Manually fails a stuck deposit invoice. `{reference}` is the PaymentIntent's GUID.

**Request**
```json
{ "reason": "string, required, max 512" }
```

**Response 200**
```json
{ "isSuccess": true, "data": { "reference": "guid", "status": "failed" }, "error": null }
```
**Response 404**: reference not found.
**Response 409**: found but in a state that can't be manually failed (e.g. already matched/confirmed) —
`{ isSuccess: false, error: "<message>" }`.

---

## Transactions (read-only ledger view)

### `GET /api/v1/ops/transactions`
Any authenticated role. Reads straight from the immutable ledger. Query params (all optional except
noted): `merchantId` (guid), `transactionId` (string — **requires `merchantId` to be set too**),
`fromDate`, `toDate` (ISO 8601 datetimes), `page` (default 1), `pageSize` (default 50, max 200).

**Response 200**
```json
{
  "isSuccess": true,
  "data": {
    "page": 1,
    "pageSize": 50,
    "totalCount": 10,
    "items": [
      {
        "journalId": "guid",
        "referenceType": "string",
        "referenceId": "guid",
        "assetId": "guid",
        "description": "string",
        "direction": "string",
        "amount": "string — decimal display value, not base units",
        "createdAt": "2026-07-29T06:00:00Z"
      }
    ]
  },
  "error": null
}
```

**Response 400**: `transactionId` was supplied without `merchantId` — `{ isSuccess: false, error: "merchantId is required when filtering by transactionId." }`.

**Known limitation**: `transactionId` lookup on this endpoint only resolves **deposit-side** references
(via PaymentIntent). If it doesn't match a deposit, you get an empty result set (`totalCount: 0`), not a
withdrawal match — there is currently no way to look up a withdrawal by its merchant reference through
this Ops endpoint (only through the merchant-facing API). Don't build a UI that assumes this searches
both.

---

## Transaction search (deposit and withdrawal screens)

Two **separate** endpoints, one per type — not a single shared query with a `type` filter. Deposits and
withdrawals genuinely show different columns (payer address / received amount only exist for a deposit),
so a shared shape would force nulls one side never populates. Build two distinct table screens; a
frontend that wants one combined view calls both and merges client-side (each row already carries its own
`type`).

### `GET /api/v1/ops/transactions/deposits`
Any authenticated role. Query params, all optional and AND-combined: `merchantId` (guid),
`systemOrderNumber` (guid — matches the invoice's `PublicReference`), `merchantOrderNumber` (string —
the merchant's own transaction id), `receivingAddress` (string), `network` (`"Tron"` etc.), `coin`
(string, e.g. `"USDT"` — **requires `network` to also be set**), `fromDate`/`toDate` (ISO 8601,
filters on `createdAt`), `page` (default 1), `pageSize` (default 50, max 200).

**Response 200**
```json
{
  "isSuccess": true,
  "data": {
    "page": 1,
    "pageSize": 50,
    "totalCount": 3,
    "items": [
      {
        "merchantId": "guid",
        "systemOrderNumber": "guid",
        "merchantOrderNumber": "string",
        "userId": null,
        "payerAddress": null,
        "receivingAddress": "T...",
        "network": "Tron",
        "coin": "USDT",
        "expectedAmount": 10.0,
        "receivedAmount": 10.0,
        "fee": "0",
        "confirms": 19,
        "type": "deposit",
        "createdAt": "2026-07-29T06:00:00Z",
        "status": "confirmed",
        "callback": "PendingNotification",
        "callbackFailedCount": 2,
        "callbackNextAttemptAt": "2026-07-30T06:01:30Z"
      }
    ]
  },
  "error": null
}
```

**Field notes**:
- `userId` and `payerAddress` are **always `null` today** — deliberately deferred (see "Not built yet").
  Don't build a UI that assumes these populate; show them as empty/dash cells.
- `receivedAmount` is `null` until the deposit actually matches on-chain (`status` still `pending`).
- `confirms` is `null` until a matching on-chain deposit exists to report a confirmation depth for.
- `fee` is **always the string `"0"`** — hardcoded placeholder, fee logic isn't wired into this view yet.
- `status`: one of `pending` | `confirmed` | `expired` | `failed`.
- `callback`: one of `null` (no delivery scheduled yet — payer hasn't paid, or merchant set no callback
  URL) | `"PendingNotification"` (retrying — see the retry schedule below) | `"Notified"` (merchant
  returned 2xx) | `"Abandoned"` (every automatic retry used up, never got a 2xx — see "Manual resend"
  below, this is not a dead end).
- `callbackFailedCount`: how many automatic attempts have failed so far (0 if never attempted or
  delivered on the first try — a successful attempt never increments this).
- `callbackNextAttemptAt`: the **exact** timestamp of the next automatic retry — only set while
  `callback` is `"PendingNotification"`; `null` once terminal (`Notified`/`Abandoned`) or if nothing was
  ever scheduled. Use this directly for a countdown; don't re-derive it from `callbackFailedCount` and the
  schedule table yourself.
- `coin` filter without `network` → `400` with `{ isSuccess: false, error: "network is required when filtering by coin." }`.
- An unrecognized `coin`/`network` combination returns `200` with an empty `items` array (not an error).

### `GET /api/v1/ops/transactions/withdrawals`
Same auth, same pagination, same filter set as the deposit endpoint above (`merchantId`,
`systemOrderNumber` — matches `Withdrawal.Id` — `merchantOrderNumber`, `receivingAddress`, `network`,
`coin`, `fromDate`/`toDate`, `page`, `pageSize`), **plus** `kind` — `user` (end-user payout) or `merchant`
(earnings cash-out); omit for both. An unrecognized `kind` returns `400`. Each row carries a `kind` field
(`"User"` / `"Merchant"`) so the two money-out kinds are distinguishable.

**Response 200**
```json
{
  "isSuccess": true,
  "data": {
    "page": 1,
    "pageSize": 50,
    "totalCount": 1,
    "items": [
      {
        "merchantId": "guid",
        "systemOrderNumber": "guid",
        "merchantOrderNumber": "string",
        "userId": null,
        "receivingAddress": "T...",
        "network": "Tron",
        "coin": "USDT",
        "expectedAmount": 1.0,
        "fee": "0",
        "confirms": 2,
        "type": "withdrawal",
        "kind": "User",
        "createdAt": "2026-07-29T06:00:00Z",
        "status": "pending",
        "callback": "Notified",
        "callbackFailedCount": 0,
        "callbackNextAttemptAt": null
      }
    ]
  },
  "error": null
}
```

**Field notes**: no `payerAddress`, no `receivedAmount` on this endpoint at all (not `null` — the keys
are simply absent). `status` is one of `pending` | `pending_approval` | `insufficient_balance` |
`awaiting_release` | `confirmed` | `failed` (withdrawals never expire). `pending_approval` means this
withdrawal is above the config approval threshold and is **waiting on a human** — build an "Approve"/"Reject"
action for exactly these rows (see "Withdrawal approval" below); plain `pending` means it's already approved
and processing automatically (building/signing/broadcasting) — no action needed, it resolves itself.
`insufficient_balance` means the **hot wallet can't physically cover it** — the merchant's funds are still
reserved (not lost), and the payout **auto-resumes** the moment the hot wallet is reloaded from treasury;
`statusReason` on the row carries the detail ("needs X, has Y"). `awaiting_release` means the payout is
funded but **above the auto-send threshold**, so it needs an operator to release it (see "Withdrawal funding
holds" below). No action is strictly required for `insufficient_balance` beyond reloading the wallet. `callback`/`callbackFailedCount`/
`callbackNextAttemptAt` follow the exact same vocabulary and retry/abandon/resend rules as the deposit
endpoint above (see "Callback delivery" below) — a withdrawal callback fires on both `Confirmed` (payload
`status: "confirmed"`) and `Rejected`/`Failed` (payload `status: "failed"`, includes `reason`), same as
deposits fire on both match and manual-fail. `userId`/`fee` follow the deposit endpoint's rules too.
`confirms` is `null` until the withdrawal is broadcast on-chain.

## Withdrawal approval

### `POST /api/v1/ops/withdrawals/{withdrawalId}/approve` 🔒 Admin
Approves a withdrawal sitting in `pending_approval`. No request body. Who approved is taken from your own
session (not a request field) — you can't approve as someone else.

**Response 200**
```json
{ "isSuccess": true, "data": { "withdrawalId": "guid", "status": "Approved" }, "error": null }
```
**Response 404**: no withdrawal with that id.
**Response 409**: not currently `pending_approval` (e.g. already approved, or already rejected) —
`{ isSuccess: false, error: "The withdrawal is not in a state that allows this operation." }`.

### `POST /api/v1/ops/withdrawals/{withdrawalId}/reject` 🔒 Admin
Rejects a withdrawal sitting in `pending_approval` and releases the merchant's reserved funds back to
their balance immediately.

**Request**
```json
{ "reason": "string, required, max 512" }
```

**Response 200**
```json
{ "isSuccess": true, "data": { "withdrawalId": "guid", "status": "Rejected" }, "error": null }
```
**Response 404**/**409**: same as approve.

Rejecting also fires the merchant's withdrawal-failed callback (`status: "failed"`, `reason` = what you
typed here) through the same retry/abandon/resend machinery as everything else on this page — you don't
need to separately notify the merchant.

## Withdrawal funding holds

Separate from approval: these act on a withdrawal the **hot wallet couldn't cover** (or one that's funded
but above the auto-send threshold). The reserve stays held throughout — a hold is a deferral, never a
release.

### `POST /api/v1/ops/withdrawals/{withdrawalId}/release` 🔒 Admin
Releases a withdrawal sitting in `awaiting_release` (funded, but above the threshold, so it waited for a
human). No request body; who released is taken from your session. It sends on the next processing pass.

**Response 200**
```json
{ "isSuccess": true, "data": { "withdrawalId": "guid", "status": "Released" }, "error": null }
```
**Response 409**: not currently on a fundable hold (e.g. already sent, or still `insufficient_balance`).

A withdrawal in `insufficient_balance` needs **no** endpoint to resume — reload the hot wallet from
treasury and the worker resumes it automatically once the float is sufficient.

### `POST /api/v1/ops/withdrawals/{withdrawalId}/cancel` 🔒 Admin
Abandons a parked withdrawal (`insufficient_balance` or `awaiting_release`) that won't be funded, releasing
the merchant's reserved funds — the one path that releases the reserve from a hold.

**Request**
```json
{ "reason": "string, required, max 512" }
```
**Response 200**
```json
{ "isSuccess": true, "data": { "withdrawalId": "guid", "status": "Cancelled" }, "error": null }
```
**Response 404**/**409**: not found / not currently on a hold. Cancelling fires the merchant's
withdrawal-failed callback the same way a reject does.

---

## Callback delivery — retry schedule and manual resend

Every scheduled callback — deposit or withdrawal, the mechanism is fully shared — is attempted
**immediately** once, then, if the merchant's endpoint doesn't return 2xx, retried automatically on a
fixed backoff:

| Attempt | Delay after previous attempt |
|---|---|
| 1 (initial) | immediate |
| 2 | 30 seconds |
| 3 | 1 minute |
| 4 | 2 minutes |
| 5 | 4 minutes |
| 6 | 10 minutes |

That's 6 attempts total (1 initial + 5 retries). If attempt 6 still doesn't get a 2xx, the row moves to
`"Abandoned"` and automatic retries **stop permanently** — there is no sweep that revives it. Getting it
delivered after that point requires the manual resend below.

Both transaction-search endpoints carry `callbackFailedCount` (how many attempts have failed so far) and
`callbackNextAttemptAt` (the exact timestamp of the next one) alongside `callback`, so a "next retry in
Xs" countdown in the UI can read `callbackNextAttemptAt` directly — no need to reconstruct it from
`callbackFailedCount` and the table above yourself. Only show a resend button once `callback` is
`"Abandoned"` — the backend refuses the call otherwise (see below), so gate the button the same way the
API gates the action, not just for cosmetics.

**Important distinction for the frontend**: the transaction's own `status` (pending/confirmed/expired/failed)
and its `callback` status are **completely independent**. A deposit can be `"confirmed"` while its callback
is still `"PendingNotification"`, or even permanently `"Abandoned"` — money already moved and settled
correctly regardless of whether the merchant's webhook endpoint ever answers. Don't gate any transaction
UI (e.g. "mark as done") on the callback status; they're two separate concerns and two separate columns.

### `POST /api/v1/ops/callbacks/{type}/{referenceId}/resend` 🔒 Admin
Manually retriggers a callback that automatic retries gave up on. `{type}` is `"deposit"` or
`"withdrawal"`; `{referenceId}` is the same guid shown as `systemOrderNumber` on the transaction-search
screens. Resends the **exact** persisted, already-signed payload from the original attempt — never
re-signs, never rebuilds the JSON body (so the merchant sees byte-for-byte the same request they'd have
gotten automatically).

**Request**: no body.

**Response 200**
```json
{ "isSuccess": true, "data": { "type": "deposit", "referenceId": "guid" }, "error": null }
```
Success here means the merchant returned 2xx this time — `callback` flips to `"Notified"` on the search
screens. Failure keeps it `"Abandoned"` (resendable again, no limit on how many times).

**Response 400**: `{type}` wasn't `"deposit"` or `"withdrawal"`.
**Response 404**: no callback delivery record exists for that reference at all (nothing was ever scheduled).
**Response 409**: a delivery record exists but isn't `"Abandoned"` yet (still retrying automatically, or
already `"Notified"`) — `{ isSuccess: false, error: "A manual resend is only available once automatic retries have been abandoned." }`.
Build the resend button so it's only enabled/shown when a row's `callback` is `"Abandoned"`.

---

## Not built yet — don't build frontend screens expecting these

Confirmed absent from the codebase as of this doc. If your BO frontend plan includes any of these,
flag it back rather than guessing an API shape:

- **Per-merchant configurable approval threshold** — currently a single platform-wide config value
  (`Withdrawal:Policies`), not per-merchant/toggle-able. Planned for later; don't build a merchant-facing
  "set your own threshold" screen against anything today.
- **Staff user management** (create/list/deactivate staff, assign roles) — staff accounts exist only via
  a dev seeder; no CRUD endpoint.
- **Per-merchant approval threshold** — the fee, settlement period, settlement wallet, cash-out cap, and the
  user-withdrawal min/max are all now settable per-merchant (see the Merchants section), but the withdrawal
  **approval threshold** is still the single platform-wide config value (`Withdrawal:Policies`) — no per-merchant
  override yet.
- **Wallet / treasury / sweep visibility** — nothing in Ops surfaces `AssetManagement/Wallet` data.
- **Energy / resource monitoring visibility** — `AssetManagement/Energy` writes to MongoDB but nothing
  in Ops reads it back out.
- **Reporting/dashboard endpoints** — `Platform/Reporting` isn't scaffolded on disk at all yet.

## Source files (for whoever needs to verify/extend this doc)

- `src/Api/OperationsApi/CryptoPaymentEngine.Api.OperationsApi/Endpoints/OpsAuthEndpoints.cs`
- `src/Api/OperationsApi/CryptoPaymentEngine.Api.OperationsApi/Endpoints/OpsMerchantEndpoints.cs`
- `src/Api/OperationsApi/CryptoPaymentEngine.Api.OperationsApi/Endpoints/OpsMerchantFeeEndpoints.cs` (per-asset fees)
- `src/Api/OperationsApi/CryptoPaymentEngine.Api.OperationsApi/Endpoints/OpsMerchantSettlementEndpoints.cs` (settlement period/wallet + cash-out cap)
- `src/Api/OperationsApi/CryptoPaymentEngine.Api.OperationsApi/Endpoints/OpsPaymentIntentEndpoints.cs`
- `src/Api/OperationsApi/CryptoPaymentEngine.Api.OperationsApi/Endpoints/OpsTransactionEndpoints.cs` (ledger view)
- `src/Api/OperationsApi/CryptoPaymentEngine.Api.OperationsApi/Endpoints/OpsDepositTransactionEndpoints.cs`
- `src/Api/OperationsApi/CryptoPaymentEngine.Api.OperationsApi/Endpoints/OpsWithdrawalTransactionEndpoints.cs`
- `src/Api/OperationsApi/CryptoPaymentEngine.Api.OperationsApi/Endpoints/OpsCallbackEndpoints.cs` (manual resend)
- `src/Api/OperationsApi/CryptoPaymentEngine.Api.OperationsApi/Endpoints/OpsWithdrawalApprovalEndpoints.cs` (approve/reject)
- `src/Api/OperationsApi/CryptoPaymentEngine.Api.OperationsApi/Endpoints/OpsWithdrawalFundingEndpoints.cs` (release/cancel — funding holds)
- `src/Api/OperationsApi/CryptoPaymentEngine.Api.OperationsApi/Models/OpsAuthRequests.cs`
- `src/Api/OperationsApi/CryptoPaymentEngine.Api.OperationsApi/Models/OpsMerchantRequests.cs`
- `src/Api/OperationsApi/CryptoPaymentEngine.Api.OperationsApi/Security/StaffAuthorization.cs`
- `src/Api/OperationsApi/CryptoPaymentEngine.Api.OperationsApi/Security/StaffBearerAuthMiddleware.cs`
- `src/Api/OperationsApi/CryptoPaymentEngine.Api.OperationsApi/Program.cs` (route mounting)
- `src/Gateway.Core/PaymentProcessing/PaymentIntent/Contracts/IPaymentIntentDirectory.cs` (`SearchAsync`)
- `src/Gateway.Core/PaymentProcessing/Withdrawal/Contracts/IWithdrawalDirectory.cs` (`SearchAsync`)
- `src/Gateway.Core/PaymentProcessing/Deposit/Contracts/IDepositLookup.cs` (`GetByIdsAsync`)
- `src/Gateway.Core/Platform/Notification/Domain/CallbackDelivery.cs` (state machine + the backoff schedule constant)
- `src/Gateway.Core/Platform/Notification/Application/ICallbackDeliveryQuery.cs` (Ops-facing status read)
- `src/Gateway.Core/Platform/Notification/Application/CallbackDeliveryProcessingService.cs` (the automatic retry worker's logic)
- `src/Gateway.Core/Platform/Notification/Application/CallbackDeliveryResendService.cs` (manual resend)
- `src/Gateway.Core/Platform/Notification/Workers/CallbackDeliveryWorker.cs` (registered only in `MerchantGateway`, never Ops)
- `src/Gateway.Core/Platform/Notification/Application/Handlers/WithdrawalConfirmedCallbackHandler.cs` / `WithdrawalFailedCallbackHandler.cs`
- `src/Gateway.Core/PaymentProcessing/Withdrawal/Domain/Withdrawal.cs` (`CallbackUrl`, carried onto `WithdrawalConfirmed`/`WithdrawalFailed`)

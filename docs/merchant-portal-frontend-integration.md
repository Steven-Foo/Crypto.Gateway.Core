# Merchant Portal API — Frontend Integration Guide

The complete, current contract for the **merchant-facing portal** (`Api/MerchantPortalApi`) — the backend
behind `apps/merchant`. Everything below was read off the endpoint source, not written from memory.

**This is a different host from the Back Office.** The Back Office (`Api/OperationsApi`, see
`docs/backoffice-frontend-integration.md`) is for *platform staff* and can see every merchant. This host is
for *a merchant's own staff* and can only ever see that one merchant. They share a session/CSRF design but
nothing else — different port, different cookie, different permission vocabulary, different database schema
for identity.

There is a third host, `Api/MerchantGateway`, which is the machine-to-machine **HMAC-signed** API a merchant's
*server* calls. It has no login and is not what this document describes.

---

## 1. Base URL and the one rule that matters

Dev default: `http://localhost:51081` (see `Properties/launchSettings.json` for the exact ports on your
machine). All routes are prefixed `/api/v1/portal/...`.

**The tenant is never a request parameter.** The merchant id comes only from the validated session. No
endpoint takes a `merchantId`, and passing one anyway does nothing — it is ignored, and the caller still sees
only its own data. If you find yourself wanting to send a merchant id, the answer is always that you do not
need to.

---

## 2. Response envelope

```json
{ "isSuccess": true,  "data": { }, "error": null }
{ "isSuccess": false, "data": null, "error": "human-readable message" }
```

> **Known inconsistency with the Back Office — read this before writing error handling.**
> The Ops host returns a machine-readable **`errorCode`** on every response (`wallet.not_found`,
> `ops.invalid_amount`, …). **This host does not yet.** Portal failures carry only the human `error` string.
> Until that is closed, branch on the **HTTP status code**, and treat `error` as display text only — do not
> pattern-match it, it gets reworded and localised. Adding `errorCode` here is a filed gap (§10).

### HTTP status codes

| Status | Meaning |
|---|---|
| 200 | Success |
| 400 | Validation failure — bad amount, missing field, unknown coin/network, malformed IP |
| 401 | No/invalid/expired session |
| 403 | Valid session, but missing this route's permission code — **or** a missing/incorrect CSRF token |
| 404 | Not found *or belongs to another tenant* — the two are deliberately indistinguishable |
| 409 | Conflict — duplicate merchant reference, or a state transition that no longer applies |

**404 vs 403 on another tenant's data is a deliberate design choice.** Asking for a resource that exists but
belongs to someone else returns 404, not 403, so the API never confirms that an id exists. Do not build UI
that treats those differently.

---

## 3. Authentication

The session is a server-side opaque token (revocable, fixed TTL). Only its SHA-256 hash is stored. It can be
presented two ways — **pick one per client**:

- **Cookie mode (use this for the browser SPA).** Login sets an **httpOnly** `cpe_portal_session` cookie. JS
  cannot read it, which is the point: an XSS bug cannot exfiltrate the session.
- **Bearer mode.** Send `Authorization: Bearer <token>`. For non-browser clients and scripts.

### CSRF — only relevant in cookie mode

A cookie is sent automatically by the browser, so cookie-authenticated **unsafe** methods
(POST/PUT/PATCH/DELETE) additionally require the session's CSRF token in an **`X-CSRF-Token`** header. Without
it you get **403**.

- Get the token from the **login** response, or from **`GET /auth/me`** after a page refresh (the session
  cookie is httpOnly, so the token cannot be persisted in JS across a reload — re-fetch it).
- GET requests never need it. Bearer-authenticated requests never need it (they are not ambient, so they are
  inherently CSRF-safe) and are exempt.

### `POST /api/v1/portal/auth/login` — the only unauthenticated endpoint

```json
{ "username": "merchant001", "password": "..." }
```

```json
{
  "isSuccess": true,
  "data": {
    "token": "...",              // bearer mode only; ignore it in the SPA
    "csrfToken": "...",          // echo as X-CSRF-Token on every write
    "expiresAt": "2026-08-27T...",
    "merchantId": "...",
    "username": "merchant001",
    "displayName": "Dev Merchant Ops",
    "permissions": ["*"],
    "mustChangePassword": false
  }
}
```

> The login body accepts an **`otp`** field. **It is currently ignored — 2FA is not implemented anywhere in
> this backend.** Do not build a UI that implies OTP protects anything.

`mustChangePassword` is a **UX signal, not a security control** — the API does not block other calls while it
is true. Show the change-password step, but do not treat it as enforcement.

### `POST /api/v1/portal/auth/logout`
Revokes the session server-side and clears the cookie. Safe to call without a valid session.

### `GET /api/v1/portal/auth/me`
Returns `merchantId`, `merchantUserId`, `username`, `displayName`, `permissions`, `csrfToken`. **Call this on
app boot** to restore session state and re-obtain the CSRF token.

---

## 4. Permissions — this drives every screen's visibility

The session carries a permission code list, **snapshotted at login**. A role change therefore takes effect at
the user's *next login*, not immediately. Gate UI on these codes and expect the server to enforce them anyway.

`["*"]` is the wildcard held by a tenant's Administrator role — it passes every check.

**A user with no role gets an empty permission list and can do nothing.** This is deliberate (fail-closed): an
account can sign in and see nothing rather than accidentally inheriting access. Render that state as "your
administrator has not assigned you a role" rather than as an error.

| Code | Grants |
|---|---|
| `portal.overview.view` | profile, funds, fees, deposit addresses |
| `portal.transactions.view` | payin / payout / cash-out history |
| `portal.payouts.create` | **submit** an end-user payout to a request-supplied address |
| `portal.payouts.approve` | **sign off** a payout someone else submitted |
| `portal.cashout.create` | initiate an earnings cash-out to the whitelisted settlement wallet |
| `portal.topup.create` | raise a merchant top-up invoice (§8.4) — adds **instantly withdrawable** balance, so grant deliberately |
| `portal.api.view` | read API-key metadata + allowed IPs (never a secret) |
| `portal.api.manage` | rotate the API credential, update the IP allowlist |
| `portal.accounts.view` / `.manage` | portal user accounts |
| `portal.roles.view` / `.manage` | portal roles |

`GET /api/v1/portal/permissions` returns the catalog for the role editor.

**Role codes are validated against this catalog.** Submitting an unknown code — including a platform `ops.*`
code — is refused with 400.

---

## 5. Money and decimals

Every amount is returned **twice**: a display decimal for humans, and the exact base-unit integer **as a
string** for anything that must be precise.

```json
{ "available": 1485.0, "availableBaseUnits": "1485000000" }
```

**Never do arithmetic on the display value, and never parse the base-unit string as a JS `number`** — it
routinely exceeds `Number.MAX_SAFE_INTEGER`. Use `BigInt` or a decimal library. Sum base units, convert once
for display.

On the way in, send amounts as **display decimals**. The host converts at the edge and **refuses over-precision
rather than truncating** — `10.1234567` on a 6-decimal asset is a 400, not a silent round. That is intentional:
silently losing a digit of someone's money is worse than an error message.

---

## 6. Overview screens — all require `portal.overview.view`

### `GET /api/v1/portal/profile`
```json
{ "merchantId": "...", "merchantCode": "DEMOACME", "name": "Acme Payments",
  "callbackUrl": "...", "canTransact": true, "settlementDelayDays": 0 }
```

`canTransact: false` means the merchant is **frozen** by platform staff. Deposits still credit (freezing stops
issuing and withdrawing, never recording), but address requests and all money-out are refused. Show this
prominently — otherwise every money-out button just fails with no explanation.

### `GET /api/v1/portal/funds`
```json
{ "settlementDelayDays": 1,
  "items": [ { "assetId": "...", "coin": "USDT", "network": "Tron",
               "available": 1485.0, "availableBaseUnits": "1485000000",
               "settled": 985.0,   "settledBaseUnits": "985000000" } ] }
```

**Two balances, and the difference matters.** `available` is the full ledger balance. `settled` is what can
actually be withdrawn *today* under the merchant's T+N settlement period: a deposit confirmed on day *D*
matures at `00:00Z` on day *D+N*. With `settlementDelayDays: 0` they are equal. Withdrawal screens must gate
on **`settled`**, or users will submit payouts that the backend correctly rejects.

### `GET /api/v1/portal/addresses`
```json
{ "items": [ { "walletId": "...", "network": "Tron", "address": "T..." } ] }
```

### `GET /api/v1/portal/fees`
Per-asset pricing and limits, all as display decimals (`null` = unset ⇒ the platform default applies):
`depositFeeFixed`, `depositFeeBps`, `withdrawalFeeFixed`, `withdrawalFeeBps`, `topUpFeeFixed`, `topUpFeeBps`,
`merchantWithdrawalFlatCap`, `merchantWithdrawalPercentBps`, `minimumWithdrawal`, `maximumWithdrawal`,
`approvalThreshold`.

**Read-only here.** Fees are set by platform staff in the Back Office — a merchant cannot price itself.

#### How a fee is applied — fees are DEDUCTED, never added on top

An invoice always asks for **exactly the amount requested**. The fee comes out of what arrives:

> A 100 USDT deposit at a 2% fee ⇒ the payer sends **100**, the merchant is credited **98**, the platform
> earns **2**.

Do not add the fee to the displayed amount, and do not expect the invoice to differ from the amount submitted.
This matters if you built against an earlier version: deposits were previously **grossed up** (a 100 request
at 2% asked the payer for 102.04 so the merchant netted 100). That behaviour is gone. A merchant's own
records should therefore reconcile against `expectedAmount` as the amount actually sent, with the fee shown
separately as the difference between what arrived and what was credited.

`topUpFee*` prices merchant top-ups (§8.4) and is a **separate schedule that defaults to zero** — it never
inherits the platform default deposit fee, so an unpriced merchant tops up free even when customer deposits
are priced.

---

## 7. Transaction history — `portal.transactions.view`

Three separate screens; they are genuinely different things, not one list with a filter.

Common query params: `network`, `coin`, `fromDate`, `toDate`, `page` (1-based), `pageSize`.
Response: `{ page, pageSize, totalCount, items }`.

### `GET /api/v1/portal/transactions/payin` — deposits (代收)
Extra filters: `merchantOrderNumber`, `receivingAddress`.

Row: `systemOrderNumber`, `merchantOrderNumber`, `network`, `coin`, `address`, `expectedAmount(+BaseUnits)`,
`receivedAmount(+BaseUnits)`, `confirmations`, `txHash`, `status`, `createdAt`.

The `received*`, `confirmations` and `txHash` fields are **null until a payment actually arrives** — an unpaid
invoice is a normal row, not a broken one.

### `GET /api/v1/portal/transactions/payout` — end-user payouts (代付)
### `GET /api/v1/portal/transactions/cash-out` — the merchant's own earnings withdrawals

Same row shape for both: `systemOrderNumber`, `merchantOrderNumber`, `network`, `coin`, `receivingAddress`,
`amount(+BaseUnits)`, `fee(+BaseUnits)`, `confirms`, `txHash`, `status`, `statusReason`, `type`, `createdAt`.

`statusReason` is the operator-facing explanation for a held payout (e.g. insufficient hot-wallet float).
Surface it — a status with no reason is the single most common support ticket.

---

## 8. Money out

Both actions run through the **same** Application services as the HMAC API, so every control applies
identically: idempotency on your reference, the per-merchant fee, the settled-balance gate, the liquidity cap,
the ledger reserve as the atomic overdraw guard, and the approval threshold.

**A duplicate `merchantOrderNumber` is a 409, not a replay.** Resubmitting the same reference does not return
the earlier result — it is rejected. Generate a fresh reference per attempt; never retry a timeout with the
same one and assume idempotent replay.

### `POST /api/v1/portal/payouts` — `portal.payouts.create`

```json
{ "merchantOrderNumber": "ORD-1001", "network": "Tron", "coin": "USDT",
  "amount": 125.50, "receivingAddress": "T..." }
```

Returns `systemOrderNumber`, `merchantOrderNumber`, `network`, `coin`, `amount`, `receivingAddress`, `status`.

**A portal payout always lands in `PendingMerchantApproval` first.** It is not on its way anywhere until
someone with `portal.payouts.approve` signs it off. The UI must not imply it has been sent.

### `POST /api/v1/portal/cash-outs` — `portal.cashout.create`

```json
{ "merchantOrderNumber": "CASH-2001", "network": "Tron", "coin": "USDT", "amount": 500.00 }
```

**No destination field, and there will never be one.** It always goes to the settlement wallet whitelisted by
platform staff — precisely so a stolen portal session or leaked API key cannot redirect the merchant's
earnings. If the wallet is not registered, this fails; the fix is a staff action, not a portal one.

### `POST /api/v1/portal/payouts/{id}/approve` — `portal.payouts.approve`

```json
{ "systemOrderNumber": "...", "status": "Approved", "awaitingPlatformApproval": false }
```

**Read `awaitingPlatformApproval`.** Approving does *not* always release the payout:

- at or below the merchant's approval threshold → cleared to send automatically;
- above it → moves to `PendingApproval` and waits for **platform staff**.

A merchant can never approve past the platform gate — the routing is re-resolved server-side at approval time,
not chosen by the caller. When the flag is true, say "sent for platform approval", not "payout approved".

### `POST /api/v1/portal/payouts/{id}/reject` — `portal.payouts.approve`

```json
{ "reason": "Duplicate order" }
```

Rejection **releases the ledger reserve** — the money returns to the available balance. A decline never
strands funds in clearing.

`404` = unknown id **or another tenant's payout**. `409` = no longer awaiting merchant approval.

### Separation of duties — how it actually works

Submit and approve are **different permission codes**, so a user granted only `create` cannot sign off their
own request. But a merchant admin holding **both** may approve a payout they raised — there is deliberately no
"different human" rule, so that a one-person merchant can still pay out. If a customer needs strict two-person
control, that is achieved by not granting both codes to one role.

---

## 8.4 Money **in** — merchant top-up — `portal.topup.create`

The merchant funding **its own** balance by sending crypto to an address we issue. Despite living next to the
money-out actions, this is money *in*: it is a real on-chain deposit that goes through the same scanner,
confirmation depth, and ledger posting as a customer payment, so platform custody genuinely rises.

### `POST /api/v1/portal/top-ups`

```json
{ "merchantOrderNumber": "TOPUP-2026-0001", "network": "Tron", "coin": "USDT", "amount": 500.00 }
```

Returns the address to send to and the exact amount:

```json
{ "isSuccess": true, "error": null,
  "data": { "reference": "...", "address": "T...", "network": "Tron", "coin": "USDT",
            "amount": 500.00, "amountBaseUnits": "500000000", "decimals": 6,
            "expiresAt": "...", "createdAt": "..." } }
```

**This endpoint moves no money.** It creates the invoice that tells the detection pipeline how to price and
classify what arrives. The balance changes only once the transfer confirms on-chain — so the UI must show this
as *pending* and let the deposit pipeline complete it, never as an immediate credit.

**The merchant pays the network gas** to send the funds, exactly as any sender does. On TRON that means the
sending wallet needs TRX (or energy) on top of the USDT being sent — a USDT-only wallet cannot broadcast. The
gas is paid to the network, not to us, and never appears in our fee figures.

**Four things that differ from a customer deposit — all worth surfacing in the UI:**

| | Customer deposit | Merchant top-up |
|---|---|---|
| Fee schedule | `depositFee*` | `topUpFee*` — **defaults to zero**, never inherits the platform default |
| Amount asked | exactly as requested | exactly as requested (identical — neither is grossed up) |
| Settlement (T+N) | held for the merchant's settlement period | **exempt — withdrawable as soon as it confirms** |
| Who sends | the merchant's customer | the merchant itself |

The T+N exemption is the substantive one: the settlement period exists to hold *customer* money through
chargeback and reorg risk, and a merchant's own float is not customer money. This is why the action sits
behind its own permission code rather than reusing a deposit permission — granting it lets a user add
instantly-spendable balance.

`409` = the `merchantOrderNumber` was already used. As everywhere else in this API that is a rejection, **not**
an idempotent replay of the earlier invoice — issue a fresh reference rather than assuming you will get the
original address back.

Top-ups appear in the normal deposit history (`/transactions/payin`) once detected.

---

## 9. Merchant self-administration

### API credential — `portal.api.view` / `portal.api.manage`

- `GET /api/v1/portal/api-credential` — metadata only. **The secret is never returned.**
- `POST /api/v1/portal/api-credential/rotate` — issues a new credential and **immediately invalidates the
  old one**. The new secret is returned **once and never again**. The UI must make that unmistakable — there
  is no recovery, by design.
- `PUT /api/v1/portal/allowed-ips` — replaces the whole allowlist (IPs and CIDRs, validated at the edge; a
  malformed entry is a 400). This is a **replace**, not an append: send the complete list.

### Accounts — `portal.accounts.view` / `portal.accounts.manage`

`GET /accounts`, `POST /accounts`, `PATCH /accounts/{id}/status`, `PATCH /accounts/{id}/role`,
`POST /accounts/{id}/reset-password`.

- Create and reset return a **generated one-time password**, shown once. Administrators never choose it.
- Guards that will reject you, and should be reflected in the UI: you cannot disable **yourself**, and you
  cannot disable the tenant's **last active account** — otherwise a merchant could lock itself out of its own
  portal with no way back in except a platform-staff intervention.
- Usernames are **globally unique across all merchants** (a username resolves to exactly one tenant at login),
  so a collision is possible with a merchant you cannot see. Surface it as "username taken", nothing more.
- **Disabling an account does not kill its live sessions** today — it is refused at next login. A filed gap.

### `POST /api/v1/portal/account/change-password` — no permission required
Everyone may change their own. Requires the current password and clears `mustChangePassword`.

### Roles — `portal.roles.view` / `portal.roles.manage`

`GET /roles`, `POST /roles`, `PUT /roles/{id}`, `PUT /roles/{id}/permissions`, `DELETE /roles/{id}`.

Roles are **per-tenant** — two merchants may each have a "Finance" role and they are unrelated. A role still
assigned to an account **cannot be deleted** (409); reassign those users first.

---

## 10. Known gaps — do not build UI that assumes these work

| Gap | Status |
|---|---|
| **2FA / OTP** | Not implemented anywhere in the backend. The login `otp` field is accepted and ignored. |
| **`errorCode` on failures** | Ops has it, this host does not. Branch on HTTP status for now. |
| **Dashboard / aggregates** | No portal endpoint. The Ops dashboard is platform-wide and is not exposed here. |
| **Paged history beyond these three lists** | Deferred; to be applied uniformly across every transaction endpoint at once. |
| **Merchant notification when a payout awaits approval** | None — the portal must poll the payout list. |
| **Portal audit log** | Merchant-admin actions are not recorded in a merchant-visible log. |
| **Revoking a disabled account's live sessions** | Refused at next login only. |
| **Editing the settlement wallet** | Deliberately staff-only, permanently. Not a gap — a security control. |

---

## 11. Local development

Run all three hosts against the same database. To get realistic data, enable the demo seeder in
`Api/MerchantGateway` — see **`docs/dev-sample-data.md`**. It creates several merchants with different
pricing, settlement periods and lifecycle states, plus invoices, credited deposits and payouts in every
status.

Portal logins seeded for those merchants (dev only, never real credentials):

| Username | Merchant | Password |
|---|---|---|
| `merchant001` | `DEVMERCHANT` | `Merchant@2026` |
| `acme001` | `DEMOACME` (T+0, active) | `Merchant@2026` |
| `globe001` | `DEMOGLOBE` (T+1, 50% cash-out cap) | `Merchant@2026` |
| `frost001` | `DEMOFROST` (**frozen**) | `Merchant@2026` |

**Sign in as more than one.** A single-tenant dev environment cannot show you a cross-tenant leak, and tenant
isolation is the one defect in this API that would matter most. `frost001` is the fastest way to check that
every money-out path degrades correctly for a frozen merchant.

CORS for the SPA origin is configured under `Cors:AllowedOrigins` (exact origins, never `*`, because the
session cookie is credentialed). If login succeeds in curl but fails in the browser, check that first.

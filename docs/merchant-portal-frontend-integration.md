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

Dev default: `http://localhost:52001` (see `Properties/launchSettings.json` for the exact ports on your
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

Every response — success and failure — carries a machine-readable **`errorCode`** (null on success), matching
the Back Office host:

```json
{ "isSuccess": true,  "data": { }, "error": null, "errorCode": null }
{ "isSuccess": false, "data": null, "error": "human-readable message", "errorCode": "portal.invalid_amount" }
```

**Branch on `errorCode`, never on `error`.** The message is display prose — it gets reworded and localised, so
code matching on it breaks silently on a copy edit.

Codes come from two places:

- **`portal.*`** — this host rejected the request before it reached a module: `portal.unauthenticated`,
  `portal.invalid_credentials`, `portal.csrf_invalid`, `portal.permission_denied`, `portal.invalid_chain`,
  `portal.invalid_asset`, `portal.invalid_amount`, `portal.invalid_status`, `portal.invalid_permission_code`,
  `portal.invalid_ip_address`, `portal.network_required`, `portal.address_required`, `portal.not_found`.
- **`<module>.*`** — a business rule refused it, passed through unchanged from where it was decided:
  `withdrawal.insufficient_balance`, `withdrawal.exceeds_settled_balance`,
  `withdrawal.duplicate_reference`, `withdrawal.merchant_cannot_transact`,
  `withdrawal.settlement_wallet_not_registered`, `merchant.not_found`, …

This is what lets a money screen tell "you don't have the balance" from "that asset isn't enabled" — both are
400s that a merchant would act on completely differently.

> **Money-out status codes are unchanged.** Several money-out rejections are conflicts internally, but
> `POST /payouts` and `POST /cash-outs` still return **400** for every business refusal except a duplicate
> reference (**409**), exactly as before. Use `errorCode` to tell them apart — do not switch existing
> handling to 409.

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

**How the temporary password actually behaves — read this before designing the change-password nudge:**
- It is a **normal password**, not a single-use code — it works for every login until it is actually changed,
  no matter how many times it is used in between.
- It **never expires** on its own. There is no time limit today; it stays valid indefinitely if nobody acts
  on `mustChangePassword`.
- **"Reset" and "change" are different actions that produce the password differently:** any *reset* (staff via
  `OperationsApi`, or another admin resetting a teammate via `POST /accounts/{id}/reset-password`) always
  hands back a **system-generated** password — nobody ever chooses it, by design, precisely because it may
  have passed through more hands than a real password should. The **only** way a user sets a password *they*
  chose is `POST /account/change-password` on their own account, which requires their current password and
  is the action that actually clears `mustChangePassword`.

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
| `portal.topup.create` | raise a merchant top-up invoice (§8.5) — adds **instantly withdrawable** balance, so grant deliberately |
| `portal.api.view` | read API-key metadata + allowed IPs (never a secret) |
| `portal.api.manage` | rotate the API credential, update the IP allowlist |
| `portal.accounts.view` / `.manage` | portal user accounts |
| `portal.roles.view` / `.manage` | portal roles |
| `portal.activity.view` | this merchant's own admin activity log (§9.5) — sensitive, grant deliberately |

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
{ "merchantId": "...", "merchantCode": "ME00001", "name": "Acme Payments",
  "callbackUrl": "...", "canTransact": true, "settlementDelayDays": 0,
  "requiresPayoutApproval": false }
```

`requiresPayoutApproval` tells you whether this merchant's payouts stop for its own approver — **read it to
decide whether to show the approval queue at all** (§8.3). Staff-controlled; a merchant cannot change it.

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
Query: `network` (optional), `page` (1-based), `pageSize` (default 50, max 200).

```json
{ "page": 1, "pageSize": 50, "totalCount": 2,
  "networks": [
    { "network": "Tron", "totalCount": 2,
      "items": [ { "walletId": "...", "network": "Tron", "address": "T..." } ] }
  ],
  "items": [ { "walletId": "...", "network": "Tron", "address": "T..." } ] }
```

**Paged**, because a merchant's address count grows with its deposit history — unlike accounts and roles,
which are bounded by headcount and are returned in full.

Page through **one network at a time** (`?network=Tron`). Paging is applied per chain, so with `network`
omitted the page number is applied to each chain separately and `networks[]` carries each chain's own
`totalCount` — there is no single ordering across chains that a page number could meaningfully address.
Today only TRON is live, so the common case is one group. An unknown or unsupported `network` is a **400**.

The flat top-level `items` is **retained for backward compatibility** and holds this page's rows across the
groups above — it is no longer every address the merchant owns. Prefer `networks[]`. Top-level `totalCount`
sums the groups in the response.

### `GET /api/v1/portal/fees`
Per-asset pricing and limits, all as display decimals (`null` = unset ⇒ the platform default applies):
`depositFeeFixed`, `depositFeePercent`, `withdrawalFeeFixed`, `withdrawalFeePercent`, `topUpFeeFixed`,
`topUpFeePercent`, `merchantWithdrawalFlatCap`, `merchantWithdrawalCapPercent`, `minimumWithdrawal`,
`maximumWithdrawal`, `approvalThreshold`.

**Percent, not basis points** — every `*Percent` field is a plain percent (`2` = 2%), matching the standard
used everywhere else on this API's wire format; the domain/DB store basis points, but nothing crossing this
endpoint ever does. (This field was named `*Bps` in an earlier version of this endpoint — if any client code
still reads `depositFeeBps`/`withdrawalFeeBps`/`topUpFeeBps`/`merchantWithdrawalPercentBps`, update it to the
`*Percent` names above.)

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

`topUpFee*` prices merchant top-ups (§8.5) and is a **separate schedule that defaults to zero** — it never
inherits the platform default deposit fee, so an unpriced merchant tops up free even when customer deposits
are priced.

---

## 7. Transaction history — `portal.transactions.view`

Three separate screens; they are genuinely different things, not one list with a filter.

Common query params: `network`, `coin`, `fromDate`, `toDate`, `status`, `page` (1-based), `pageSize`.

**`status` filters on the same effective status the rows report**, applied in SQL so `totalCount` reflects it.
This matters for a queue: client-side filtering breaks on paging, because page 1 of all payouts may hold none
of the status you are looking for, and the screen then renders empty while work waits several pages back.

- payin: `pending` | `confirmed` | `expired` | `failed`
- payout / cash-out: `pending` | `pending_merchant_approval` | `pending_approval` | `insufficient_balance` |
  `awaiting_release` | `confirmed` | `failed` | `pending_admin_audit` | `pending_finance_transfer` |
  `finance_settled`

An unrecognised value is a **400** (`portal.invalid_status`) listing the accepted set — never a silently
unfiltered page, which would read as "these are the matching ones".

The approval queue is `GET /transactions/payout?status=pending_merchant_approval`.
Response: `{ page, pageSize, totalCount, items }`.

### `GET /api/v1/portal/transactions/payin` — deposits (代收)
Extra filters: `merchantOrderNumber`, `receivingAddress`.

Row: `systemOrderNumber`, `merchantOrderNumber`, `network`, `coin`, `address`, `expectedAmount(+BaseUnits)`,
`receivedAmount(+BaseUnits)`, `confirmations`, `txHash`, `status`, `kind`, `createdAt`.

`kind` is `"Customer"` (a payment from the merchant's customer) or `"MerchantTopUp"` (the merchant funding
its own balance, §8.5). Both are real on-chain deposits in the same list, but they are priced on different
fee schedules and settle differently — a top-up is exempt from the T+N hold. **Surface or filter on this**,
otherwise a merchant reconciling revenue cannot separate customer income from its own deposits.

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

### When does a payout actually wait for merchant approval?

**It is the merchant's stored policy, not the fact that you called the portal.** `GET /portal/profile`
returns `requiresPayoutApproval`:

- **`false` (the default)** — a payout goes straight to the platform threshold decision. `POST /payouts`
  returns `status: "Approved"` (or `"PendingApproval"` above the platform threshold). **Nothing will ever
  appear in the approval queue**, so hide it rather than showing an empty screen.
- **`true`** — every payout this merchant raises stops at `PendingMerchantApproval` for its own approver
  first, **whether it was submitted here or over the HMAC API**.

That last point is the whole reason the setting exists. The flag used to be hardcoded per host — the portal
always said yes, the HMAC API always no — so it recorded *which host was called* rather than what the merchant
wanted, and a merchant integrating server-to-server could never reach the queue at all.

Only platform staff can change it (`PUT /ops/merchants/{id}/payout-approval`); a merchant cannot switch off
its own approval requirement. Turning it off releases nothing already waiting — those payouts still need
approving or rejecting.

### Separation of duties — how it actually works

Submit and approve are **different permission codes**, so a user granted only `create` cannot sign off their
own request. But a merchant admin holding **both** may approve a payout they raised — there is deliberately no
"different human" rule, so that a one-person merchant can still pay out. If a customer needs strict two-person
control, that is achieved by not granting both codes to one role.

---

### 8.4 Being told a payout needs approval — webhook

When a portal user submits a payout, a callback fires to the merchant's registered URL so an approver learns
about it without watching the portal. Same envelope and HMAC signing as the deposit and withdrawal callbacks.

```json
{ "transactionId": "<your reference>",
  "data": { "transactionId": "<your reference>", "referenceNo": "<systemOrderNumber>",
            "type": "withdraw", "status": "pending_merchant_approval",
            "amount": "50000000", "fee": "250000",
            "receivingAddress": "T...", "timestamp": "..." } }
```

- **`status` is a new value** outside the frozen `pending`/`confirmed`/`failed` vocabulary. An existing
  integration that switches on status will ignore it rather than mistake a payout needing a human for one
  already on its way — that is deliberate.
- `amount` and `fee` are **base-unit strings**, matching the other withdrawal callbacks.
- **Portal-submitted payouts only.** An HMAC-API payout never enters merchant approval (your server already
  authorised it by signing), so no callback fires for one. Existing API behaviour is unchanged.
- **It carries no approval link or token.** Approval happens in an authenticated portal session; a webhook
  can never be the thing that authorises money movement.
- No callback URL registered ⇒ nothing is sent, and the payout still waits normally. Delivery is
  best-effort notification: a failed callback never changes the payout's state.

---

## 8.5 Money **in** — merchant top-up — `portal.topup.create`

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

### 9.5 Activity log — `portal.activity.view`

`GET /api/v1/portal/activity` — who did what in **your** portal, and when. Answers "who gave that user the
ability to approve payouts?" and "when was our API credential rotated?".

Query: `action`, `entityType`, `entityId`, `fromDate`, `toDate`, `page`, `pageSize` (default 50, max 200).
Newest first.

```json
{ "page": 1, "pageSize": 50, "totalCount": 1,
  "items": [ { "id": "...", "actor": "merchant001", "actorUserId": "...",
               "action": "portal.role.permissions_changed", "entityType": "MerchantRole",
               "entityId": "...", "detail": "permissions=[portal.overview.view]",
               "ipAddress": "::1", "createdAt": "..." } ] }
```

**Scope is structural, not a filter.** The log returns only entries stamped with your merchant id. Platform
staff actions and other merchants' entries are excluded by the same condition — there is no parameter that
could widen it, and no `merchantId` query param exists (passing one is ignored). Filtering by a known
platform action code returns zero rows, not staff history.

`actor` is the username **as recorded at the time**, so renaming or deleting an account never rewrites
history.

Recorded actions:

| `action` | `entityType` |
|---|---|
| `portal.role.created` / `.updated` / `.permissions_changed` / `.deleted` | `MerchantRole` |
| `portal.account.created` / `.status_changed` / `.role_changed` / `.password_reset` / `.own_password_changed` | `MerchantUser` |
| `portal.api_credential.rotated` | `MerchantApiCredential` |
| `portal.allowed_ips.updated` | `Merchant` |
| `portal.payout.approved` / `.rejected` | `Withdrawal` |

**Only successful actions are recorded** — a rejected attempt is not an action, and logging failures would
make the trail unusable as evidence of what actually changed. It is not an authentication log: failed logins
are not here.

**Secrets are never recorded.** A rotation entry says *that* the credential was rotated, never the key or
secret; an account-create or password-reset entry never contains the generated password. Treat the log as
readable by anyone with the permission.

Read-only — nothing exposes a way to edit or delete an entry.

---

## 10. Known gaps — do not build UI that assumes these work

| Gap | Status |
|---|---|
| **2FA / OTP** | Not implemented anywhere in the backend. The login `otp` field is accepted and ignored. |
| **`errorCode` on failures** | **Done** — every response carries one, `portal.*` for host validation and `<module>.*` for business rules (§2). |
| **Dashboard / aggregates** | No portal endpoint. The Ops dashboard is platform-wide and is not exposed here. |
| **Paging** | **Done.** Transaction history and `/addresses` are paged. Accounts and roles return in full — bounded by headcount, so paging them would add UI work for no benefit. |
| **Merchant notification when a payout awaits approval** | **Done** — a `pending_merchant_approval` webhook fires for portal-submitted payouts (§8.4). Merchants with no registered callback URL still need to poll. |
| **Portal audit log** | **Done** — `GET /portal/activity` (§9.5). |
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
| `acme001` | `Acme Payments` (T+0, active) | `Merchant@2026` |
| `globe001` | `Globe Commerce` (T+1, 50% cash-out cap) | `Merchant@2026` |
| `frost001` | `Frostbite Retail` (**frozen**) | `Merchant@2026` |

**Sign in as more than one.** A single-tenant dev environment cannot show you a cross-tenant leak, and tenant
isolation is the one defect in this API that would matter most. `frost001` is the fastest way to check that
every money-out path degrades correctly for a frozen merchant.

CORS for the SPA origin is configured under `Cors:AllowedOrigins` (exact origins, never `*`, because the
session cookie is credentialed). If login succeeds in curl but fails in the browser, check that first.

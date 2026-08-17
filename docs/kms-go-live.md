# KMS custody go-live (production-tier validation)

The AWS KMS envelope custody is what flips **withdrawal + sweep money-out** and **deposit address provisioning**
from "inert in production" to live. This runbook validates it end-to-end against **real** AWS KMS using an **EC2
instance role — no static access keys, ever** (§10). Follow it top to bottom and the validation passes first try.

It is independent of the testnet-tier withdrawal flow in [`ec2-staging.md`](ec2-staging.md): the KMS gate below
needs **no database, no Redis, no running host** — only the role plus three identifiers.

## What it proves

`KmsEnvelopeLiveTests` runs the **real** production classes — `KmsHdWalletProvisioner` (mint seed →
`kms:Encrypt`) and `KmsEnvelopeSecretProvider` (`kms:Decrypt` → derive child key) — against your CMKs (only the
DB material store is in-memory). Green means:

- the two CMKs are usable, and the app's principal holds `kms:Encrypt` + `kms:Decrypt` on them;
- a KMS-sealed seed derives **exactly** the private key behind the watch-only address it was neutered to — the
  money-critical invariant that we never sign for the wrong address (§14);
- the encryption-context (AAD) binding is enforced — a ciphertext cannot be decrypted under a different context.

## Prerequisites

- Two **customer-managed symmetric CMKs** (key spec `SYMMETRIC_DEFAULT`, usage **Encrypt and decrypt**), one for
  **Deposit** and one for **Withdrawal**, in a single region. Note their **key ARNs** and the **region**.
- An **EC2 instance** in the same AWS account, with the **.NET 10 SDK** and this repo present (put the code there
  the same way you deploy the app).
- Leave each CMK on its **default key policy** — its root *"Enable IAM User Permissions"* statement is what lets
  the IAM role below use the key. Do **not** replace the key policy with a single hand-written statement.

## 1. Create the instance role — this *is* the credential (no keys)

IAM → Roles → Create role → trusted entity **AWS service → EC2**. Name it e.g. `cpe-staging-kms`, and attach this
inline policy (substitute your two key ARNs):

```json
{
  "Version": "2012-10-17",
  "Statement": [{
    "Sid": "CpeKmsEnvelope",
    "Effect": "Allow",
    "Action": ["kms:Encrypt", "kms:Decrypt"],
    "Resource": [
      "<deposit-cmk-arn>",
      "<withdrawal-cmk-arn>"
    ]
  }]
}
```

These are the **only** two KMS actions the code calls — no `GenerateDataKey`, no `DescribeKey`. `Resource` is the
two specific key ARNs (never `*`).

## 2. Attach the role to the EC2 instance

EC2 → the instance → **Actions → Security → Modify IAM role** → select `cpe-staging-kms`. The AWS SDK on the box
now resolves credentials from the instance role automatically via IMDS — **you set no access key anywhere.** This
is the same mechanism production uses (an instance role, or an ECS task role).

## 3. Run the KMS validation (role + three identifiers only)

On the box, from the repo root:

```bash
export CPE_KMS_REGION=<region>                 # e.g. ap-southeast-1
export CPE_KMS_DEPOSIT_ARN=<deposit-cmk-arn>
export CPE_KMS_WITHDRAWAL_ARN=<withdrawal-cmk-arn>
# Do NOT set AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY — the instance role supplies credentials.

dotnet test src/Gateway.Core/KeyManagement/Tests/CryptoPaymentEngine.Gateway.Core.KeyManagement.Tests.csproj \
  --filter "FullyQualifiedName~KmsEnvelopeLiveTests"
```

**Expected:** `Passed!  - Failed: 0, Passed: 3`. That is the go-live gate cleared.

Without the three `CPE_KMS_*` vars these tests **skip**, so a normal `dotnet test` anywhere else never touches AWS.

### If it fails

| Symptom | Cause | Fix |
|---|---|---|
| `AccessDeniedException` on Encrypt/Decrypt | the role is missing that action on that CMK | fix the Step-1 policy; confirm the CMK's default key policy (root IAM-enable statement) is intact |
| `Could not load credentials from any of the providers` | role not attached, or IMDS unreachable | re-check Step 2; ensure the box can reach the instance metadata service |
| `NotFoundException` / invalid `KeyId` | wrong ARN, or CMK is in a different region than `CPE_KMS_REGION` | make the `CPE_KMS_*` ARNs + region match the CMKs |
| a test throws `InvalidCiphertextException` and **passes** | that is the wrong-context negative test succeeding | nothing — expected |

## 4. Bring the money-out path live in the host (production tier)

Once Step 3 is green, enable KMS in the production host config (the environment's own config or its git-ignored
`appsettings.Local.json` — **identifiers only, never secrets**):

```jsonc
"KeyManagement": {
  "Kms": {
    "Enabled": true,
    "Region": "<region>",
    "KeyArns": { "Deposit": "<deposit-cmk-arn>", "Withdrawal": "<withdrawal-cmk-arn>" }
  }
}
```

Under `ASPNETCORE_ENVIRONMENT=Production`, setting `KeyManagement:Kms:Enabled=true` is what registers the real
KMS secret provider + provisioner **and** the real TRON tx-engine + secp256k1 signer. Without it, Production
registers **no** signer and withdrawal/sweep stay inert (never a fake signer, §10). The host still needs its
normal production config (`Db` / `Redis` / `Mongo` / `Chains:Tron` on **mainnet**, real merchants) — see the
promotion checklist in [`ec2-staging.md`](ec2-staging.md) §5. Then validate a real signed withdrawal end-to-end
(build → sign → broadcast → confirm → settle) and a deposit-address provisioning.

## Security notes (§10)

- **No static access keys** anywhere — the instance/task role is the credential, both here and in production.
- **Least privilege:** the role grants only `kms:Encrypt` + `kms:Decrypt` on exactly the two CMKs.
- **Two-factor by construction:** the sealed seed's ciphertext lives in this system's DB, the key-encryption key
  lives in KMS — either alone is useless. Config carries only identifiers; the seed never leaves KMS in plaintext
  except briefly in memory at sign time, after which it is zeroed.
- Keep each CMK on its **default key policy** (do not drop the root IAM-enable statement, or you can lock yourself
  out of the key). A separate CMK per purpose means a policy mistake on one cannot reach the other's seeds.

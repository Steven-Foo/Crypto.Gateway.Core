using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;

public static class LedgerErrors
{
    // ── Journal structure (money correctness) ───────────────────────────────────
    public static readonly Error JournalNeedsTwoLines =
        Error.Validation("ledger.journal_needs_two_lines", "A journal must have at least two entries (a debit and a credit).");

    public static readonly Error Unbalanced =
        Error.Validation("ledger.unbalanced", "A journal's total debits must equal its total credits.");

    public static readonly Error NonPositiveAmount =
        Error.Validation("ledger.non_positive_amount", "Every posting amount must be greater than zero.");

    public static readonly Error LineNotDebitXorCredit =
        Error.Validation("ledger.line_not_debit_xor_credit", "A posting line must be exactly one of a debit or a credit.");

    public static readonly Error AccountRequired =
        Error.Validation("ledger.account_required", "Every posting line must reference an account.");

    public static readonly Error AssetRequired =
        Error.Validation("ledger.asset_required", "A journal must reference an asset.");

    public static readonly Error ReferenceRequired =
        Error.Validation("ledger.reference_required", "A journal must reference a business event.");

    // ── Account ─────────────────────────────────────────────────────────────────
    public static readonly Error MerchantAccountNeedsOwner =
        Error.Validation("ledger.merchant_account_needs_owner", "A merchant-owned account requires an owner id.");

    public static readonly Error PlatformAccountHasNoOwner =
        Error.Validation("ledger.platform_account_has_no_owner", "A treasury/system account must not have an owner id.");

    public static readonly Error AccountNotActive =
        Error.Conflict("ledger.account_not_active", "The account is not active and cannot be posted to.");

    public static readonly Error EntryAssetMismatch =
        Error.Validation("ledger.entry_asset_mismatch", "A posting line's account is denominated in a different asset than the journal.");

    // ── Balance ───────────────────────────────────────────────────────────────────
    public static readonly Error BalanceWouldGoNegative =
        Error.Conflict("ledger.balance_would_go_negative", "The posting would drive the account balance below zero.");

    public static readonly Error InsufficientBalance =
        Error.Conflict("ledger.insufficient_balance", "The merchant's balance is insufficient for this withdrawal.");

    public static readonly Error NotFound =
        Error.NotFound("ledger.not_found", "Ledger record not found.");
}

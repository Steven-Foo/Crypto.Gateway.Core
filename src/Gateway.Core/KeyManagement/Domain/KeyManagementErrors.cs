using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;

public static class KeyManagementErrors
{
    public static readonly Error PathRequired =
        Error.Validation("keymgmt.path_required", "Derivation path is required.");

    public static readonly Error PathMalformed =
        Error.Validation("keymgmt.path_malformed", "Derivation path is not a valid BIP-32 path.");

    public static readonly Error PathPurposeNotBip44 =
        Error.Validation("keymgmt.path_purpose_invalid", "Derivation path must begin with m/44' (BIP-44).");

    public static readonly Error PathCoinTypeMismatch =
        Error.Validation("keymgmt.path_coin_type_mismatch", "Derivation path coin type does not match the chain (SLIP-44).");

    public static readonly Error PathShapeInvalid =
        Error.Validation("keymgmt.path_shape_invalid", "Derivation path has the wrong shape for this derivation scheme.");

    public static readonly Error NameRequired =
        Error.Validation("keymgmt.name_required", "HD wallet name is required.");

    public static readonly Error MerchantRequired =
        Error.Validation("keymgmt.merchant_required", "A merchant id is required for a merchant-owned HD wallet.");

    public static readonly Error SecretReferenceRequired =
        Error.Validation("keymgmt.secret_reference_required", "A secret reference is required. The seed itself is never stored.");

    public static readonly Error PublicKeyReferenceRequired =
        Error.Validation(
            "keymgmt.public_key_reference_required",
            "A watch-only (secp256k1) HD wallet requires an account public key reference.");

    public static readonly Error PublicKeyReferenceNotApplicable =
        Error.Validation(
            "keymgmt.public_key_reference_not_applicable",
            "An ed25519 HD wallet cannot derive from a public key; it must not carry one.");

    public static readonly Error NotFound =
        Error.NotFound("keymgmt.hd_wallet_not_found", "No active HD wallet exists for that chain and purpose.");

    public static readonly Error NotActive =
        Error.Conflict("keymgmt.hd_wallet_not_active", "The HD wallet is not active.");

    /// <summary>
    /// Non-hardened BIP-32 indices are 0 .. 2^31-1. Past that the index would mean a hardened child
    /// — a completely different key. Refuse rather than silently derive the wrong address.
    /// </summary>
    public static readonly Error PoolExhausted =
        Error.Conflict("keymgmt.derivation_pool_exhausted", "The HD wallet's derivation index space is exhausted.");

    public static readonly Error IndexOutOfRange =
        Error.Validation("keymgmt.index_out_of_range", "Derivation index must be between 0 and 2147483647.");

    public static readonly Error SchemeNotSupported =
        Error.Failure("keymgmt.scheme_not_supported", "No key deriver is registered for this HD wallet's derivation scheme.");

    public static readonly Error ChainNotSupported =
        Error.Failure("keymgmt.chain_not_supported", "No address encoder is registered for this chain.");

    public static readonly Error AddressAlreadyDerived =
        Error.Conflict("keymgmt.address_already_derived", "That address has already been derived.");
}

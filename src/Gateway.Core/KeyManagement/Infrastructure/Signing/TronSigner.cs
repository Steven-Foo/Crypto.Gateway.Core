using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Signing;

/// <summary>
/// Signs a TRON transaction: loads the referenced private key from the secret provider, produces the
/// 65-byte recoverable secp256k1 signature TRON expects (<c>r ‖ s ‖ recId</c> over <c>sha256(raw_data)</c>),
/// and appends it to the transaction's <c>signature</c> array. The key is loaded as a zeroized
/// <see cref="SecretLease"/>, used in memory, and wiped — it is never returned, logged, or persisted (§10);
/// the caller only ever sees the signed blob.
///
/// <para>This implementation reads a private key from an <see cref="ISecretProvider"/>, so it must be wired
/// ONLY where that is a testnet throwaway (Dev/testnet) — never in production, where signing belongs to a
/// KMS/HSM-backed signer that never exposes the key to the process at all.</para>
/// </summary>
public sealed class TronSigner(ISecretProvider secretProvider, ILogger<TronSigner> logger) : ISigner
{
    public async Task<Result<SignedTransaction>> SignAsync(SigningRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Chain != Chain.Tron)
            return Fail("sign.unsupported_chain", $"{nameof(TronSigner)} signs TRON transactions only.");

        // Parse the unsigned transaction object (opaque to everyone else). We need raw_data_hex + txID.
        JsonObject transaction;
        try
        {
            transaction = JsonNode.Parse(request.UnsignedPayload) as JsonObject
                          ?? throw new System.Text.Json.JsonException("not a JSON object");
        }
        catch (System.Text.Json.JsonException)
        {
            return Fail("sign.malformed", "Unsigned transaction is not a JSON object.");
        }

        var rawDataHex = TryGetString(transaction, "raw_data_hex");
        var txId = TryGetString(transaction, "txID");
        if (string.IsNullOrEmpty(rawDataHex) || string.IsNullOrEmpty(txId))
            return Fail("sign.malformed", "Unsigned transaction is missing raw_data_hex or txID.");

        byte[] rawData, claimedTxId;
        try
        {
            rawData = Convert.FromHexString(rawDataHex);
            claimedTxId = Convert.FromHexString(txId);
        }
        catch (FormatException)
        {
            return Fail("sign.malformed", "raw_data_hex or txID is not valid hex.");
        }

        // Money-critical integrity: the txID MUST equal sha256(raw_data). We sign this hash, so a tampered
        // txID (or mismatched raw_data) would otherwise let us sign the wrong transaction. Recompute; refuse on mismatch.
        var txHash = SHA256.HashData(rawData);
        if (claimedTxId.Length != txHash.Length || !CryptographicOperations.FixedTimeEquals(claimedTxId, txHash))
            return Fail("sign.txid_mismatch", "txID does not match sha256(raw_data) — refusing to sign.");

        byte[] signature;
        try
        {
            using var lease = await secretProvider.GetAsync(request.KeyReference, cancellationToken);
            signature = SignRecoverable(lease.Value, txHash);
        }
        catch (KeyNotFoundException)
        {
            return Fail("sign.key_not_found", $"No signing key for reference '{request.KeyReference}'.");
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            logger.LogError(ex, "TRON signing failed for withdrawal {WithdrawalId}.", request.WithdrawalId);
            return Fail("sign.key_invalid", "The signing key is not a valid secp256k1 private key.");
        }

        transaction["signature"] = new JsonArray(Convert.ToHexString(signature).ToLowerInvariant());
        logger.LogInformation("Signed TRON withdrawal {WithdrawalId} (tx {TxId}).", request.WithdrawalId, txId);
        return Result.Success(new SignedTransaction(Encoding.UTF8.GetBytes(transaction.ToJsonString())));
    }

    /// <summary>
    /// Produces TRON's 65-byte recoverable signature: <c>r(32) ‖ s(32) ‖ recId(1)</c> over the 32-byte hash.
    /// libsecp256k1 signs the hash as a big-endian scalar (the standard) and emits a low-S canonical
    /// signature, exactly as the TRON node verifies. The recovery id is the raw 0..3, not Ethereum's +27.
    /// </summary>
    public static byte[] SignRecoverable(ReadOnlySpan<byte> keyMaterial, byte[] hash32)
    {
        var privateKeyBytes = DecodePrivateKey(keyMaterial);
        try
        {
            using var key = new Key(privateKeyBytes);

            // SignCompact over the 32-byte hash: libsecp256k1 signs it as a big-endian scalar (the standard
            // TRON also uses) and NBitcoin returns a low-S canonical recoverable signature — r||s (64) plus the
            // recovery id 0..3, exactly TRON's format (not Ethereum's recId+27).
            var compact = key.SignCompact(new uint256(hash32));

            var signature = new byte[65];
            compact.Signature.CopyTo(signature.AsSpan());
            signature[64] = (byte)compact.RecoveryId;
            return signature;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKeyBytes);
        }
    }

    /// <summary>
    /// The secret may be the raw 32 private-key bytes (a KMS) or, for a dev/testnet throwaway held in an
    /// in-memory store, the UTF-8 text of its 64-hex encoding. Both resolve to the 32-byte key.
    /// </summary>
    private static byte[] DecodePrivateKey(ReadOnlySpan<byte> material)
    {
        if (material.Length == 32)
            return material.ToArray();

        var text = Encoding.UTF8.GetString(material).Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];

        var bytes = Convert.FromHexString(text);
        if (bytes.Length != 32)
            throw new FormatException($"A secp256k1 private key must be 32 bytes; got {bytes.Length}.");
        return bytes;
    }

    private static string? TryGetString(JsonObject obj, string name) =>
        obj.TryGetPropertyValue(name, out var node) && node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    private static Result<SignedTransaction> Fail(string code, string message) =>
        Result.Failure<SignedTransaction>(Error.Failure(code, message));
}

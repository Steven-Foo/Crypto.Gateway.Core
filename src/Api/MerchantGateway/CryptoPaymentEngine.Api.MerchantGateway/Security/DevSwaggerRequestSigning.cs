namespace CryptoPaymentEngine.Api.MerchantGateway.Security;

/// <summary>
/// DEVELOPMENT ONLY. Makes Swagger UI's "Try it out" actually exercise the merchant API instead of bouncing
/// off <see cref="MerchantSignatureMiddleware"/>.
///
/// <para>The merchant API is HMAC-signed, and Swagger UI cannot compute a signature over the request body on
/// its own — so "Try it out" would always fail with "Missing X-Timestamp or X-Signature header". (The legacy
/// PoC had the same gap: its Swagger declared only an X-Api-Key scheme while its MerchantSecurityFilter still
/// demanded the signature, so its deposit flow was never provable through Swagger either.) This injects a
/// swagger-ui request interceptor that signs each <c>/api/v1</c> call in the browser with the <b>dev seed
/// merchant's</b> credentials, using exactly the scheme the middleware verifies:
/// <c>hex(HMAC-SHA256(hexDecode(signingSecret), "{unixSeconds}\n{body}"))</c>.</para>
///
/// <para><b>Why this is safe (§10):</b> it is wired only when the environment is Development AND the dev
/// merchant seed is enabled. It embeds only the seeded <i>dev</i> credentials, which are already fixed and
/// documented in committed config and are explicitly never a real merchant's secret. It must never be wired
/// in production — a real signing secret must never be embedded in a page.</para>
/// </summary>
public static class DevSwaggerRequestSigning
{
    /// <summary>
    /// The swagger-ui <c>requestInterceptor</c>, as a JS function expression. Returns a Promise, which
    /// swagger-ui awaits — WebCrypto's HMAC is async. <c>crypto.subtle</c> requires a secure context;
    /// localhost qualifies, so the dev host is fine.
    /// </summary>
    public static string InterceptorJs(string apiKey, string signingSecretHex) => $$"""
        (request) => {
          if (!/\/api\/v1\//.test(request.url)) { return request; }

          const timestamp = Math.floor(Date.now() / 1000).toString();
          const body = typeof request.body === 'string'
            ? request.body
            : (request.body ? JSON.stringify(request.body) : '');

          const keyBytes = new Uint8Array(('{{signingSecretHex}}'.match(/../g) || []).map(b => parseInt(b, 16)));

          return crypto.subtle
            .importKey('raw', keyBytes, { name: 'HMAC', hash: 'SHA-256' }, false, ['sign'])
            .then(key => crypto.subtle.sign('HMAC', key, new TextEncoder().encode(timestamp + '\n' + body)))
            .then(signature => {
              request.headers['X-Api-Key'] = '{{apiKey}}';
              request.headers['X-Timestamp'] = timestamp;
              request.headers['X-Signature'] = Array.from(new Uint8Array(signature))
                .map(b => b.toString(16).padStart(2, '0'))
                .join('');
              return request;
            });
        }
        """;
}

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
    ///
    /// <remarks>
    /// <para><b>This string must stay on ONE line and contain NO backslashes.</b> It is not a cosmetic
    /// constraint — violating it silently blanks the whole Swagger page, and the cause is invisible from the
    /// C# source.</para>
    ///
    /// <para>Swashbuckle emits this function into <c>/swagger/index.js</c> as JSON <i>inside a single-quoted
    /// JS string literal</i>: <c>JSON.parse('{"RequestInterceptorFunction":"…"}')</c>. The browser therefore
    /// unescapes it <b>twice</b> — once parsing the JS string literal, once in <c>JSON.parse</c> — and each
    /// pass consumes one backslash. A regex like <c>/\/api\/v1\//</c> arrives as <c>//api/v1//</c> (a line
    /// comment); <c>'\n'</c> arrives as a real newline inside a string literal. Both are syntax errors, and a
    /// literal newline is illegal inside a JSON string value in the first place. Swashbuckle then feeds the
    /// text to its own <c>parseFunction()</c> → <c>new Function(...)</c>, all from <c>window.onload</c>, so
    /// any throw means swagger-ui never mounts: a blank page with no server-side symptom.</para>
    ///
    /// <para>Hence: <c>indexOf</c> instead of a regex, and <c>String.fromCharCode(10)</c> instead of
    /// <c>'\n'</c>. Single quotes are safe — Swashbuckle escapes them as <c>'</c>, which survives both
    /// passes. <c>parseFunction</c> also rebuilds the function by taking the first <c>{</c> and the last
    /// <c>}</c>, so keep the body's braces balanced and the arrow's <c>{</c> first.</para>
    /// </remarks>
    public static string InterceptorJs(string apiKey, string signingSecretHex) =>
        "(request) => { " +
            "if (request.url.indexOf('/api/v1/') === -1) { return request; } " +
            "var ts = Math.floor(Date.now() / 1000).toString(); " +
            "var body = typeof request.body === 'string' ? request.body : (request.body ? JSON.stringify(request.body) : ''); " +
            $"var keyBytes = new Uint8Array(('{signingSecretHex}'.match(/../g) || []).map(function (h) {{ return parseInt(h, 16); }})); " +
            // String.fromCharCode(10) is the newline the signing string needs: '\n' would not survive the
            // double-unescaping described above.
            "var message = new TextEncoder().encode(ts + String.fromCharCode(10) + body); " +
            "return crypto.subtle.importKey('raw', keyBytes, { name: 'HMAC', hash: 'SHA-256' }, false, ['sign'])" +
                ".then(function (key) { return crypto.subtle.sign('HMAC', key, message); })" +
                ".then(function (signature) { " +
                    $"request.headers['X-Api-Key'] = '{apiKey}'; " +
                    "request.headers['X-Timestamp'] = ts; " +
                    "request.headers['X-Signature'] = Array.from(new Uint8Array(signature))" +
                        ".map(function (b) { return b.toString(16).padStart(2, '0'); }).join(''); " +
                    "return request; " +
                "}); " +
        "}";
}

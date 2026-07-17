using System.Text.Json;
using CryptoPaymentEngine.Api.MerchantGateway.Security;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Api.IntegrationTests;

/// <summary>
/// Guards the dev Swagger signing interceptor against the trap that silently blanks the whole Swagger page.
///
/// <para>Swashbuckle emits the interceptor into <c>/swagger/index.js</c> as JSON <b>inside a single-quoted JS
/// string literal</b> — <c>JSON.parse('{"RequestInterceptorFunction":"…"}')</c> — so the browser unescapes it
/// twice, and each pass eats one backslash. It then rebuilds the function with its own <c>parseFunction()</c>
/// → <c>new Function(...)</c>, from <c>window.onload</c>. Any throw there means swagger-ui never mounts: a
/// blank page, HTTP 200, nothing wrong server-side. It regressed exactly this way once — <c>/\/api\/v1\//</c>
/// reached the browser as <c>//api/v1//</c> (a line comment) and <c>'\n'</c> as a real newline.</para>
///
/// <para>These tests encode the two invariants that make the emitted JS survive the round trip, so the
/// failure is a red test rather than a blank page someone has to debug through three layers of escaping.</para>
/// </summary>
public sealed class DevSwaggerRequestSigningTests
{
    private const string ApiKey = "cpe_dev_merchant";
    private const string SecretHex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string Js => DevSwaggerRequestSigning.InterceptorJs(ApiKey, SecretHex);

    [Fact]
    public void The_interceptor_contains_no_backslash_because_double_unescaping_would_eat_it()
    {
        // A backslash survives neither pass: the JS string literal consumes one, JSON.parse the next.
        Js.ShouldNotContain("\\");
    }

    [Fact]
    public void The_interceptor_is_a_single_line_because_a_raw_newline_is_illegal_inside_a_json_string()
    {
        // Newlines in the source arrive as \n in the JSON, which the JS string layer turns into a REAL
        // newline — and an unescaped control character inside a JSON string value makes JSON.parse throw.
        Js.ShouldNotContain("\n");
        Js.ShouldNotContain("\r");
    }

    [Fact]
    public void Swashbuckle_parseFunction_can_rebuild_it_into_a_one_argument_function()
    {
        // Mirrors Swashbuckle's parseFunction(): body = between the FIRST '{' and the LAST '}',
        // params = between the first '(' and the last ')' of everything before that first '{'.
        var bodyStart = Js.IndexOf('{');
        var bodyEnd = Js.LastIndexOf('}');
        bodyStart.ShouldBeGreaterThan(0);
        bodyEnd.ShouldBeGreaterThan(bodyStart);

        var declare = Js[..bodyStart];
        var parameters = declare[(declare.IndexOf('(') + 1)..declare.LastIndexOf(')')];
        parameters.Trim().ShouldBe("request"); // new Function("request", body)

        var body = Js[(bodyStart + 1)..bodyEnd];
        body.Count(c => c == '{').ShouldBe(body.Count(c => c == '}')); // balanced, so the last '}' really closes the fn
    }

    [Fact]
    public void It_survives_the_browsers_double_unescaping_unchanged()
    {
        // The real round trip: serialize as Swashbuckle does, unescape the JS string literal, then JSON.parse.
        // Whatever comes out is what the browser compiles — it must equal what we wrote.
        var json = JsonSerializer.Serialize(new { RequestInterceptorFunction = Js });
        var afterJsStringLiteral = UnescapeJsStringLiteral(json);
        var roundTripped = JsonDocument.Parse(afterJsStringLiteral)
            .RootElement.GetProperty("RequestInterceptorFunction").GetString();

        roundTripped.ShouldBe(Js);
    }

    [Fact]
    public void It_signs_with_the_configured_credentials_and_only_targets_the_merchant_api()
    {
        Js.ShouldContain($"'{ApiKey}'");
        Js.ShouldContain($"'{SecretHex}'");
        Js.ShouldContain("'/api/v1/'");            // scoped to the signed surface
        Js.ShouldContain("String.fromCharCode(10)"); // the "{ts}\n{body}" separator, backslash-free
        Js.ShouldContain("X-Signature");
    }

    /// <summary>Applies the unescaping a JS engine performs on a single-quoted string literal.</summary>
    private static string UnescapeJsStringLiteral(string source)
    {
        var sb = new System.Text.StringBuilder(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] != '\\') { sb.Append(source[i]); continue; }

            i++;
            switch (source[i])
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u': sb.Append((char)Convert.ToInt32(source.Substring(i + 1, 4), 16)); i += 4; break;
                default: sb.Append(source[i]); break; // covers \\ -> \ and \" -> "
            }
        }

        return sb.ToString();
    }
}

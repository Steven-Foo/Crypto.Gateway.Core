using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace CryptoPaymentEngine.Api.MerchantGateway.Security;

/// <summary>
/// Documents the HMAC headers <see cref="MerchantSignatureMiddleware"/> enforces on <c>/api/v1/*</c>.
/// Swashbuckle has no way to know about them on its own — they're read from <c>HttpContext</c> inside
/// middleware, never bound as endpoint parameters.
///
/// <para>They are documented as <b>optional</b> on purpose: Swagger only ships in Development, where
/// <see cref="DevSwaggerRequestSigning"/> fills all three in the browser, and swagger-ui refuses to send a
/// request while a <c>Required</c> field is blank — which would block the very "Try it out" the interceptor
/// exists to enable. They remain mandatory on the wire; the middleware, not the docs, enforces that.</para>
/// </summary>
public sealed class HmacHeadersOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var path = context.ApiDescription.RelativePath ?? "";
        if (!path.StartsWith("api/v1/", StringComparison.OrdinalIgnoreCase))
            return;

        operation.Parameters ??= [];

        const string autoFilled = "Leave blank in Swagger — it is signed for you in Development. ";

        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "X-Api-Key",
            In = ParameterLocation.Header,
            Required = false,
            Description = autoFilled + "The merchant's API key (dev seed: cpe_dev_merchant).",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        });
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "X-Timestamp",
            In = ParameterLocation.Header,
            Required = false,
            Description = autoFilled + "Unix seconds (UTC). Must be within 5 minutes of server time or the " +
                          "request is rejected as a replay.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        });
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "X-Signature",
            In = ParameterLocation.Header,
            Required = false,
            Description = autoFilled + "hex(HMAC-SHA256(hexDecode(signingSecret), \"{X-Timestamp}\\n{body}\")). " +
                          "This is what a real merchant integration must compute; " +
                          "tools/dev/Invoke-MerchantRequest.ps1 does the same from PowerShell.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        });
    }
}

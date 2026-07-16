using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace CryptoPaymentEngine.Api.MerchantGateway.Security;

/// <summary>
/// Documents the HMAC headers <see cref="MerchantSignatureMiddleware"/> enforces on <c>/api/v1/*</c> as
/// fillable Swagger UI fields. Swashbuckle has no way to know about them on its own — they're read from
/// <c>HttpContext</c> inside middleware, never bound as endpoint parameters.
/// </summary>
public sealed class HmacHeadersOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var path = context.ApiDescription.RelativePath ?? "";
        if (!path.StartsWith("api/v1/", StringComparison.OrdinalIgnoreCase))
            return;

        operation.Parameters ??= [];

        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "X-Api-Key",
            In = ParameterLocation.Header,
            Required = true,
            Description = "The merchant's API key. Dev seed: cpe_dev_merchant.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        });
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "X-Timestamp",
            In = ParameterLocation.Header,
            Required = true,
            Description = "Unix seconds (UTC). Must be within 5 minutes of server time or the request is rejected as a replay.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        });
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "X-Signature",
            In = ParameterLocation.Header,
            Required = true,
            Description = "hex(HMAC-SHA256(signingSecret, \"{X-Timestamp}\\n{body}\")). Swagger UI cannot " +
                          "compute this for you — run tools/dev/Invoke-MerchantRequest.ps1 with the same " +
                          "body first (dev signing secret documented in docs/dev-round-trip.md), then paste " +
                          "the timestamp/signature it prints into these two fields.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        });
    }
}

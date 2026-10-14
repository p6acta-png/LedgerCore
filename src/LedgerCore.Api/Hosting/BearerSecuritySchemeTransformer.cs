using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace LedgerCore.Api.Hosting;

/// <summary>Declares the bearer-token scheme in the OpenAPI document so Swagger UI can send tokens.</summary>
internal sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info.Title = "LedgerCore API";
        document.Info.Description =
            "Simulated double-entry ledger. All amounts are integer minor units (øre). " +
            "Money-moving POST endpoints require an Idempotency-Key header.";

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "In Development, obtain a token from POST /api/dev/tokens.",
        };
        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("Bearer", document)] = [],
        });
        return Task.CompletedTask;
    }
}

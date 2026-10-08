using MessageBroker.Application.Security;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace MessageBroker.Api.Hosting;

/// <summary>
/// The OpenAPI document (/openapi/v1.json, always on) and two browser UIs over it, for trying the API:
/// Swagger UI at /swagger and Scalar at /scalar. The UIs are on in Development and elsewhere only when
/// Broker:ApiDocsUi is true. Both take the full Authorization header value, "ApiKey &lt;key&gt;".
/// </summary>
public static class ApiDocumentation
{
    public const string SwaggerPath = "swagger";
    public const string ScalarPath = "/scalar";

    public static bool UiEnabled(WebApplication app) =>
        app.Environment.IsDevelopment() || app.Configuration.GetValue("Broker:ApiDocsUi", false);

    public static IServiceCollection AddBrokerOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(o => o.AddDocumentTransformer<ApiKeySecurityTransformer>());

    /// <summary>Maps Scalar at /scalar/v1 (/scalar redirects there). Its page and assets need no key.</summary>
    public static IEndpointConventionBuilder MapBrokerScalar(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapScalarApiReference(ScalarPath, o => o
                .WithTitle("Message Broker API")
                .WithOpenApiRoutePattern("/openapi/{documentName}.json")
                .AddPreferredSecuritySchemes([ApiKeys.Scheme])
                .AddApiKeyAuthentication(ApiKeys.Scheme, key => key.Value = "ApiKey ")
                .EnablePersistentAuthentication())
            .AllowAnonymous();

    /// <summary>Serves Swagger UI. Call before authentication: the UI's static files need no key.</summary>
    public static IApplicationBuilder UseBrokerSwaggerUi(this IApplicationBuilder app) =>
        app.UseSwaggerUI(o =>
        {
            o.RoutePrefix = SwaggerPath;
            o.SwaggerEndpoint("/openapi/v1.json", "Message Broker API v1");
            o.DocumentTitle = "Message Broker API";
            o.EnablePersistAuthorization();
            o.EnableTryItOutByDefault();
            o.DisplayRequestDuration();
        });

    private sealed class ApiKeySecurityTransformer : IOpenApiDocumentTransformer
    {
        public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
        {
            document.Info.Title = "Internal Message Broker API";
            document.Info.Description =
                "Topics, publishing, pull delivery, settlement, dead letters and administration. " +
                "Authorize with the full header value: ApiKey followed by your key, e.g. ApiKey mbk_….";
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes[ApiKeys.Scheme] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = "Authorization",
                Description = "The full Authorization header value: ApiKey mbk_…",
            };
            document.Security ??= [];
            document.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(ApiKeys.Scheme, document)] = [],
            });
            return Task.CompletedTask;
        }
    }
}

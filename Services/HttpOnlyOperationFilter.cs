using System.Text.Json.Nodes;
using Coflnet.Sky.SkyAuctionTracker.Controllers;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

/// <summary>
/// Marks the investigation endpoints with <c>x-internal: true</c>.
/// They stay in the OpenAPI document (and the Swagger UI) but openapi-generator skips them,
/// so they are not part of the generated Coflnet.Sky.FlipTracker.Client.
/// </summary>
public class HttpOnlyOperationFilter : IOperationFilter
{
    public const string Marker = "x-internal";

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.ApiDescription.ActionDescriptor is ControllerActionDescriptor descriptor
            && descriptor.ControllerTypeInfo.AsType() == typeof(InvestigationController))
        {
            operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();
            operation.Extensions[Marker] = new JsonNodeExtension(JsonValue.Create(true));
        }
    }
}

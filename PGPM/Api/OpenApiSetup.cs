using System.Text.Json;
using Microsoft.OpenApi;

namespace PGPM.Api;

/// <summary>
/// The v1 OpenAPI document (docs/03 §3.3): only api/v1/, written at Debug build time to PGPM.WebUI/src/api/openapi.json,
/// from which `npm run gen:api` makes the UI's types.
/// </summary>
public static class OpenApiSetup
{
    public static IServiceCollection AddPgpmOpenApi(this IServiceCollection services) =>
        services.AddOpenApi("v1", o =>
        {
            o.ShouldInclude = d => d.RelativePath?.StartsWith("api/v1/", StringComparison.Ordinal) == true;
            // Query parameters in camelCase, as the JSON (?page=2&pageSize=50). Binding ignores case, so this only names
            // them in the document the UI's types come from.
            o.AddOperationTransformer((operation, _, _) =>
            {
                foreach (var parameter in operation.Parameters?.OfType<OpenApiParameter>() ?? [])
                {
                    if (parameter.In == ParameterLocation.Query && parameter.Name is { Length: > 0 } name)
                    {
                        parameter.Name = JsonNamingPolicy.CamelCase.ConvertName(name);
                    }
                }
                return Task.CompletedTask;
            });
            // An enum's shared schema is made from its first use: when that is a nullable property, null joined the enum, and
            // with it every use of the type in the UI's generated types. A property's own oneOf says whether it may be null.
            o.AddSchemaTransformer((schema, context, _) =>
            {
                var type = Nullable.GetUnderlyingType(context.JsonTypeInfo.Type) ?? context.JsonTypeInfo.Type;
                if (type.IsEnum && schema.Enum is { Count: > 0 } values)
                {
                    foreach (var value in values.Where(v => v is null || v.GetValueKind() == JsonValueKind.Null).ToList())
                    {
                        values.Remove(value);
                    }
                    if (schema.Type is { } t && t.HasFlag(JsonSchemaType.Null))
                    {
                        schema.Type = t & ~JsonSchemaType.Null;
                    }
                }
                return Task.CompletedTask;
            });
            // A JSON object of any members (a synced row, a patch's {old, new}): without additionalProperties the UI's types
            // would allow no member at all.
            o.AddSchemaTransformer((schema, context, _) =>
            {
                if (context.JsonTypeInfo.Type == typeof(System.Text.Json.Nodes.JsonObject))
                {
                    schema.AdditionalPropertiesAllowed = true;
                    schema.AdditionalProperties = new OpenApiSchema();
                }
                return Task.CompletedTask;
            });
            // A file upload is multipart/form-data, which the document does not infer without the Consumes constraint that
            // ApiSetup turns off.
            o.AddOperationTransformer((operation, context, _) =>
            {
                if (context.Description.ParameterDescriptions.Any(p => p.Type == typeof(IFormFile))
                    && operation.RequestBody is OpenApiRequestBody { Content: { } content }
                    && content.Remove("application/x-www-form-urlencoded", out var media))
                {
                    content["multipart/form-data"] = media;
                }
                return Task.CompletedTask;
            });
        });
}

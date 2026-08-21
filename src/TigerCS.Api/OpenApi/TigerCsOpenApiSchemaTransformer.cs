using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace TigerCS.Api.OpenApi;

/// <summary>
/// Keeps a schema's <c>required</c> list honest: a property whose type is
/// nullable is dropped from it.
///
/// <para>
/// The generator derives <c>required</c> from a record's positional
/// constructor, so every parameter lands there — including the optional ones.
/// That contradicts what the endpoints actually accept: <c>POST
/// /api/intake-records</c>, for instance, documents
/// <c>rawUnitNumberEntered</c> as "must be omitted when isUnitRelated is
/// false", and a client generated from an unadjusted document could not
/// express that. Removing nullable properties from <c>required</c> leaves a
/// statement that holds for both directions — a nullable field may be
/// omitted from a request, and a client must not assume one is present.
/// </para>
///
/// <para>Documentation only — model binding and validation are untouched.</para>
/// </summary>
internal sealed class TigerCsOpenApiSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (schema.Required is not { Count: > 0 } || schema.Properties is null)
        {
            return Task.CompletedTask;
        }

        foreach (var name in schema.Required.ToArray())
        {
            if (schema.Properties.TryGetValue(name, out var property) && IsNullable(property))
            {
                schema.Required.Remove(name);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// True when the property's declared type includes <c>null</c> — the
    /// OpenAPI 3.1 spelling of a nullable field, which the generator emits as
    /// e.g. <c>"type": ["null", "string"]</c>.
    /// </summary>
    private static bool IsNullable(IOpenApiSchema property) =>
        property.Type is { } type && type.HasFlag(JsonSchemaType.Null);
}

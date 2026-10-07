using Kintsugi.Domain.Common;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>Which connection and model one AI feature uses in <see cref="AiProvider.Routed"/>
/// mode. At most one per feature.</summary>
public class AiFeatureRoute : BaseEntity
{
    public AiFeature Feature { get; private set; }
    public Guid ConnectionId { get; private set; }

    /// <summary>The model id exactly as the connection's endpoint expects it — a models.dev id for
    /// a catalog connection (<c>gemini-2.5-pro</c>, <c>claude-sonnet-4-5@20250929</c>), or whatever
    /// a custom endpoint calls it.</summary>
    public string Model { get; private set; } = string.Empty;

    private AiFeatureRoute()
    {
    }

    public static AiFeatureRoute Create(AiFeature feature, Guid connectionId, string model)
    {
        var route = new AiFeatureRoute { Feature = feature };
        route.Apply(connectionId, model);
        return route;
    }

    public void Update(Guid connectionId, string model)
    {
        Apply(connectionId, model);
        MarkUpdated();
    }

    private void Apply(Guid connectionId, string model)
    {
        if (connectionId == Guid.Empty)
        {
            throw new DomainException("A route needs a connection.");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new DomainException("A route needs a model.");
        }

        ConnectionId = connectionId;
        Model = model.Trim();
    }
}

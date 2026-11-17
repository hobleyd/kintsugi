using Kintsugi.Domain.Common;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// One call to a metered AI model: what it was for, which model answered, how many tokens it used
/// and what that is estimated to have cost. The ledger the AI Usage screen and the monthly budget
/// are read from.
/// </summary>
/// <remarks>
/// <para>
/// **The cost is an estimate, fixed when the call is recorded.** It is the tokens the provider
/// reported times the per-million-token price models.dev listed at that moment, so a later price
/// change does not rewrite last month. It is null when no price was known (a model models.dev does
/// not list, or a catalogue that could not be fetched): the tokens still count, and the screen says
/// the cost is unknown rather than showing zero.
/// </para>
/// <para>
/// **Only calls that report tokens are here.** Ollama is local and reports tokens but costs
/// nothing; the Claude Agent SDK and Goose run on a subscription and report nothing, so they never
/// appear. A budget therefore only ever counts what could actually be billed per call.
/// </para>
/// </remarks>
public class AiUsageRecord : BaseEntity
{
    public DateTimeOffset OccurredUtc { get; private set; }

    public AiFeature Feature { get; private set; }

    /// <summary>The connection's name as the administrator gave it, kept as text so a deleted
    /// connection does not erase what it spent.</summary>
    public string ConnectionName { get; private set; } = default!;

    public string Model { get; private set; } = default!;

    /// <summary>What the call was about — the application being researched or repaired, or the
    /// product a CPE was suggested for. Null when nothing names it.</summary>
    public string? Subject { get; private set; }

    public long InputTokens { get; private set; }

    public long OutputTokens { get; private set; }

    /// <summary>US dollars, estimated; null when the model's price was unknown.</summary>
    public decimal? EstimatedCostUsd { get; private set; }

    private AiUsageRecord()
    {
    }

    public static AiUsageRecord Create(
        DateTimeOffset occurredUtc,
        AiFeature feature,
        string connectionName,
        string model,
        string? subject,
        long inputTokens,
        long outputTokens,
        decimal? estimatedCostUsd)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new DomainException("An AI usage record must name the model that answered.");
        }

        if (inputTokens < 0 || outputTokens < 0)
        {
            throw new DomainException("Token counts cannot be negative.");
        }

        if (estimatedCostUsd is < 0)
        {
            throw new DomainException("An estimated cost cannot be negative.");
        }

        return new AiUsageRecord
        {
            OccurredUtc = occurredUtc,
            Feature = feature,
            ConnectionName = string.IsNullOrWhiteSpace(connectionName) ? "(unnamed)" : connectionName.Trim(),
            Model = model.Trim(),
            Subject = string.IsNullOrWhiteSpace(subject) ? null : subject.Trim(),
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            EstimatedCostUsd = estimatedCostUsd
        };
    }
}

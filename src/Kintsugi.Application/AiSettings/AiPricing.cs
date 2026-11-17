namespace Kintsugi.Application.AiSettings;

/// <summary>Turns tokens into an estimated cost using the prices in the models.dev catalogue.</summary>
public static class AiPricing
{
    /// <summary>US dollars per million input tokens charged against the budget for a call whose
    /// model the catalogue does not price.</summary>
    /// <remarks>
    /// <para>
    /// **An unpriced call is counted against the limit at a deliberately high rate, not at zero.**
    /// Counting it as nothing made the cap bypassable: route research to a model models.dev does not
    /// list (or let the catalogue fetch fail) and nothing ever reached the limit. Refusing unpriced
    /// models outright would instead stop every AI call whenever models.dev is unreachable, so the
    /// budget fails closed by over-counting rather than by refusing.
    /// </para>
    /// <para>
    /// The ledger row itself still records a null cost — the screen says "no price" rather than
    /// passing off this figure as the model's real one — and <see cref="AiBudgetStatusDto"/> carries
    /// how much of the month's total came from this rate, so the AI Usage screen can say so. The
    /// figures sit at the top of the list prices of the frontier models this system is pointed at,
    /// so an unknown model is almost always over-estimated rather than under.
    /// </para>
    /// </remarks>
    public const decimal FallbackInputPerMillion = 15m;

    /// <summary>US dollars per million output tokens for an unpriced call; see
    /// <see cref="FallbackInputPerMillion"/>.</summary>
    public const decimal FallbackOutputPerMillion = 75m;

    /// <summary>What an unpriced call's tokens count for against the budget.</summary>
    public static decimal FallbackEstimate(long inputTokens, long outputTokens) =>
        (inputTokens * FallbackInputPerMillion + outputTokens * FallbackOutputPerMillion) / 1_000_000m;

    /// <summary>
    /// The estimated cost in US dollars, or null when the catalogue holds no price for the model.
    /// </summary>
    /// <remarks>
    /// The model is matched by id across every provider, ignoring a Vertex-style <c>@version</c>
    /// suffix, because a connection stores the id it calls (<c>claude-sonnet-5-5</c>) while the
    /// catalogue lists the same model under several providers (<c>claude-sonnet-5-5@default</c>).
    /// Where several providers price it, the first with a price wins; they are normally the same.
    /// </remarks>
    public static decimal? Estimate(AiCatalogDto catalog, string model, long inputTokens, long outputTokens)
    {
        var wanted = Normalise(model);

        foreach (var provider in catalog.Providers)
        {
            foreach (var entry in provider.Models)
            {
                if (entry.InputPerMillion is null || entry.OutputPerMillion is null
                    || !string.Equals(Normalise(entry.Id), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return (inputTokens * entry.InputPerMillion.Value + outputTokens * entry.OutputPerMillion.Value) / 1_000_000m;
            }
        }

        return null;
    }

    private static string Normalise(string model)
    {
        var id = model.Trim();
        var at = id.IndexOf('@');
        return at < 0 ? id : id[..at];
    }
}

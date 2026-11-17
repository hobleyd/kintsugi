namespace Kintsugi.Application.Hosts;

/// <summary>Where a host stands against the newest agent published for its own platform.</summary>
public enum AgentUpdateState
{
    /// <summary>Nothing to compare: the host has never reported an agent version, its operating
    /// system is not one of the three the agents run on, or no package has been published for its
    /// platform.</summary>
    Unknown,

    /// <summary>Running the newest published agent, or a newer one.</summary>
    Current,

    /// <summary>Behind the newest published agent, for a reason that is not yet worrying: the
    /// package is new, or the host is offline and will update when it next checks in.</summary>
    Behind,

    /// <summary>Online and still behind a day or more after the newer package was published. Agents
    /// check for an update at every hourly check-in, so by then something is stopping it.</summary>
    NotUpdating
}

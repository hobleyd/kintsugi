using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Tests.Domain;

public class RemoteControlSessionTests
{
    private static RemoteControlSession CreateValid() =>
        RemoteControlSession.Request(Guid.NewGuid(), "C02ABC123DEF", "designer-mbp", "admin@example.com", RemoteControlSessionKind.Screen);

    [Fact]
    public void Request_StartsPendingWithNothingDecided()
    {
        var session = CreateValid();

        Assert.Equal(RemoteControlConsent.Pending, session.Consent);
        Assert.Null(session.ConsentDecidedAtUtc);
        Assert.Null(session.StartedAtUtc);
        Assert.Null(session.EndedAtUtc);
    }

    [Fact]
    public void Request_WithoutAHost_IsStillValid()
    {
        // The audit trail has to outlive its subject, so there is no requirement that a host row
        // exist — see the note on the entity.
        var session = RemoteControlSession.Request(null, "C02ABC123DEF", "designer-mbp", "admin@example.com", RemoteControlSessionKind.Screen);

        Assert.Null(session.HostId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Request_WithoutASerialNumber_Throws(string? serialNumber)
    {
        Assert.Throws<DomainException>(() =>
            RemoteControlSession.Request(Guid.NewGuid(), serialNumber!, "designer-mbp", "admin@example.com", RemoteControlSessionKind.Screen));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Request_WithoutARequester_Throws(string? requestedBy)
    {
        // Who asked is the whole point of the record; an anonymous one would be worse than none.
        Assert.Throws<DomainException>(() =>
            RemoteControlSession.Request(Guid.NewGuid(), "C02ABC123DEF", "designer-mbp", requestedBy!, RemoteControlSessionKind.Screen));
    }

    [Fact]
    public void RecordConsent_StampsTheDecision()
    {
        var session = CreateValid();

        session.RecordConsent(RemoteControlConsent.Granted);

        Assert.Equal(RemoteControlConsent.Granted, session.Consent);
        Assert.NotNull(session.ConsentDecidedAtUtc);
    }

    [Fact]
    public void RecordConsent_PendingIsNotADecision()
    {
        var session = CreateValid();

        Assert.Throws<DomainException>(() => session.RecordConsent(RemoteControlConsent.Pending));
    }

    [Fact]
    public void RecordConsent_CannotOverturnARefusal()
    {
        // The security invariant, not tidiness: the consent message arrives over a socket the agent
        // holds, so without first-answer-wins a host that already refused could be talked into
        // sending a second, granting message.
        var session = CreateValid();
        session.RecordConsent(RemoteControlConsent.Denied);

        session.RecordConsent(RemoteControlConsent.Granted);

        Assert.Equal(RemoteControlConsent.Denied, session.Consent);
    }

    [Fact]
    public void RecordConsent_CannotOverturnATimeout()
    {
        var session = CreateValid();
        session.RecordConsent(RemoteControlConsent.TimedOut);

        session.RecordConsent(RemoteControlConsent.Granted);

        Assert.Equal(RemoteControlConsent.TimedOut, session.Consent);
    }

    [Fact]
    public void MarkStarted_RefusesWithoutConsent()
    {
        // A relay bug must not be able to produce a record of a session that ran unconsented.
        var session = CreateValid();

        Assert.Throws<DomainException>(session.MarkStarted);
    }

    [Theory]
    [InlineData(RemoteControlConsent.Denied)]
    [InlineData(RemoteControlConsent.TimedOut)]
    [InlineData(RemoteControlConsent.AgentUnreachable)]
    public void MarkStarted_RefusesOnAnythingButAGrant(RemoteControlConsent outcome)
    {
        var session = CreateValid();
        session.RecordConsent(outcome);

        Assert.Throws<DomainException>(session.MarkStarted);
    }

    [Fact]
    public void MarkStarted_AfterAGrant_IsRecordedOnce()
    {
        var session = CreateValid();
        session.RecordConsent(RemoteControlConsent.Granted);

        session.MarkStarted();
        var first = session.StartedAtUtc;
        session.MarkStarted();

        Assert.Equal(first, session.StartedAtUtc);
    }

    [Fact]
    public void MarkEnded_KeepsTheFirstReason()
    {
        // Both socket handlers race to end the session as the relay unwinds, and the side that
        // noticed first is the one that knows why.
        var session = CreateValid();
        session.RecordConsent(RemoteControlConsent.Granted);
        session.MarkStarted();

        session.MarkEnded("the host user pressed Disconnect");
        session.MarkEnded("the connection closed");

        Assert.Equal("the host user pressed Disconnect", session.EndReason);
    }

    [Fact]
    public void MarkEnded_WithoutAReason_StillSaysSomething()
    {
        var session = CreateValid();

        session.MarkEnded("   ");

        Assert.Equal("ended", session.EndReason);
        Assert.NotNull(session.EndedAtUtc);
    }

    [Fact]
    public void MarkEnded_CanCloseOutARequestThatWasNeverGranted()
    {
        // A refused request is still a record that has to be closed.
        var session = CreateValid();
        session.RecordConsent(RemoteControlConsent.Denied);

        session.MarkEnded("the host user refused");

        Assert.NotNull(session.EndedAtUtc);
    }

    [Fact]
    public void Request_DefaultsToNothingBeingAShell()
    {
        // Kind is part of the audit record, so the two must be distinguishable in the table rather
        // than inferred from the consent column.
        Assert.Equal(RemoteControlSessionKind.Screen, CreateValid().Kind);
        Assert.Equal(RemoteControlSessionKind.Shell, CreateShell().Kind);
    }

    [Fact]
    public void RecordConsent_LetsAShellSessionProceedWithoutAskingAnyone()
    {
        var session = CreateShell();

        session.RecordConsent(RemoteControlConsent.NotRequired);

        Assert.Equal(RemoteControlConsent.NotRequired, session.Consent);
        Assert.NotNull(session.ConsentDecidedAtUtc);
    }

    [Fact]
    public void RecordConsent_RefusesToLetAScreenSessionSkipConsent()
    {
        // The whole feature rests on nobody's screen being watched without them agreeing, and the
        // answer arrives over a socket the *agent* holds — so this is checked rather than trusted.
        // Without it an agent opens capture, keyboard and pointer on a host shown no dialog at all,
        // simply by reporting that no permission was needed.
        Assert.Throws<DomainException>(() => CreateValid().RecordConsent(RemoteControlConsent.NotRequired));
    }

    [Fact]
    public void MarkStarted_AcceptsAShellSessionThatNobodyWasAskedAbout()
    {
        var session = CreateShell();
        session.RecordConsent(RemoteControlConsent.NotRequired);

        session.MarkStarted();

        Assert.NotNull(session.StartedAtUtc);
    }

    [Fact]
    public void MarkStarted_StillRefusesAScreenSessionNobodyGranted()
    {
        var session = CreateValid();
        session.RecordConsent(RemoteControlConsent.Denied);

        Assert.Throws<DomainException>(session.MarkStarted);
    }

    [Fact]
    public void RecordConsent_RecordsThatTheHostCouldNotProvideTheSession()
    {
        // Distinct from AgentUnreachable, where nothing answered at all: here the agent answered
        // and said no screen exists to share, which is a different fact about the host.
        var session = CreateValid();

        session.RecordConsent(RemoteControlConsent.Unavailable);

        Assert.Equal(RemoteControlConsent.Unavailable, session.Consent);
    }

    private static RemoteControlSession CreateShell() =>
        RemoteControlSession.Request(
            Guid.NewGuid(), "C02ABC123DEF", "designer-mbp", "admin@example.com", RemoteControlSessionKind.Shell);
}

using Kintsugi.Application.Baselines;
using Kintsugi.Application.Baselines.Commands.CreateBaseline;
using Kintsugi.Application.Baselines.Commands.SignBaselineVersion;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.ScriptApproval;
using Kintsugi.Domain.Exceptions;
using Kintsugi.Infrastructure.Persistence.Repositories;
using static Kintsugi.Tests.Application.Baselines.BaselineTestData;

namespace Kintsugi.Tests.Application.Baselines;

public class BaselineSigningTests
{
    [Fact]
    public void CanonicalJson_IsTheSameBytesWhateverOrderTheItemsArrivedIn()
    {
        var id = Guid.NewGuid();
        var version = Guid.NewGuid();
        var a = BaselineDocument.ToCanonicalJson(new BaselineDocument(1, id, "b", version, 1, new[] { Setting("b.two"), Setting("a.one", valueName: "Other") }));
        var b = BaselineDocument.ToCanonicalJson(new BaselineDocument(1, id, "b", version, 1, new[] { Setting("a.one", valueName: "Other"), Setting("b.two") }));

        Assert.Equal(a, b);
        Assert.DoesNotContain("\n", a);
        Assert.All(a, c => Assert.True(c < 128, "the canonical document is ASCII-only"));
        Assert.Equal(new[] { "a.one", "b.two" }, BaselineDocument.Parse(a).Items.Select(i => i.Key));
    }

    /// <summary>
    /// The whole round trip: a version created and signed through the handlers, verified the way the
    /// agent verifies it — and refused the moment one byte of the document changes.
    /// </summary>
    [Fact]
    public async Task SignedDocument_VerifiesAgainstThePinnedKey_AndChangedBytesDoNot()
    {
        await using var context = Context();
        var repository = new BaselineRepository(context);
        var signing = SigningService();

        var created = await new CreateBaselineCommandHandler(repository, context)
            .Handle(new CreateBaselineCommand("Windows workstation", new[] { Setting("copilot.off") }, "reviewer@example.com"), CancellationToken.None);
        var stored = (await repository.GetVersionAsync(created.VersionId, CancellationToken.None))!;

        var signed = await new SignBaselineVersionCommandHandler(repository, signing, context)
            .Handle(new SignBaselineVersionCommand(created.VersionId, ScriptContentHash.Of(stored.Document), "reviewer@example.com"), CancellationToken.None);

        Assert.True(signed.Signed);
        Assert.Equal("reviewer@example.com", signed.SignedBy);
        Assert.True(AgentWouldAccept(signing.GetPublicKeyPem(), stored.Document, stored.Signature!));

        var tampered = stored.Document.Replace("\"data\":\"1\"", "\"data\":\"0\"");
        Assert.NotEqual(stored.Document, tampered);
        Assert.False(AgentWouldAccept(signing.GetPublicKeyPem(), tampered, stored.Signature!));
    }

    /// <summary>SAM holds password hashes and SECURITY the LSA secrets; the agent refuses them too
    /// (<c>baseline::is_protected_path</c>), but a baseline naming them must never get as far as signing.</summary>
    [Theory]
    [InlineData(@"SAM\SAM\Domains\Account")]
    [InlineData(@"security\Policy\Secrets\DefaultPassword\CurrVal")]
    [InlineData("SECURITY")]
    public void Validator_RefusesSamAndSecurity(string path)
    {
        var fact = Fact("lsa") with { Path = path };

        Assert.False(new BaselineItemValidator().Validate(fact).IsValid);
        Assert.False(new BaselineItemValidator().Validate(Setting("hashes") with { Path = path }).IsValid);
        Assert.True(new BaselineItemValidator().Validate(Fact("ok") with { Path = @"SOFTWARE\SAMSUNG" }).IsValid);
    }

    [Fact]
    public async Task Sign_RefusesWhenTheStoredDocumentIsNotTheOneReviewed()
    {
        await using var context = Context();
        var repository = new BaselineRepository(context);
        var created = await new CreateBaselineCommandHandler(repository, context)
            .Handle(new CreateBaselineCommand("b", new[] { Setting("one") }), CancellationToken.None);

        var reviewedSomethingElse = ScriptContentHash.Of("{\"items\":[]}");
        await Assert.ThrowsAsync<ConflictException>(() => new SignBaselineVersionCommandHandler(repository, SigningService(), context)
            .Handle(new SignBaselineVersionCommand(created.VersionId, reviewedSomethingElse), CancellationToken.None));

        Assert.Null((await repository.GetVersionAsync(created.VersionId, CancellationToken.None))!.Signature);
    }

    [Fact]
    public async Task Sign_RequiresTheReviewedHash_AndNeverSignsAVersionTwice()
    {
        await using var context = Context();
        var repository = new BaselineRepository(context);
        var handler = new SignBaselineVersionCommandHandler(repository, SigningService(), context);
        var created = await new CreateBaselineCommandHandler(repository, context)
            .Handle(new CreateBaselineCommand("b", new[] { Setting("one") }), CancellationToken.None);

        await Assert.ThrowsAsync<DomainException>(() => handler.Handle(new SignBaselineVersionCommand(created.VersionId, ""), CancellationToken.None));

        await handler.Handle(new SignBaselineVersionCommand(created.VersionId, created.DocumentSha256), CancellationToken.None);
        await Assert.ThrowsAsync<DomainException>(() => handler.Handle(new SignBaselineVersionCommand(created.VersionId, created.DocumentSha256), CancellationToken.None));
    }
}

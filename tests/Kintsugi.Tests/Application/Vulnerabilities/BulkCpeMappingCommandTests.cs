using Moq;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.Vulnerabilities.Commands.ConfirmCpeMappings;
using Kintsugi.Application.Vulnerabilities.Commands.ResetCpeMappings;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.Vulnerabilities;

/// <summary>
/// The mapping queue's bulk Confirm and Clear — what they change, and what they refuse to change
/// quietly.
/// </summary>
public class BulkCpeMappingCommandTests
{
    private readonly Mock<IVulnerabilityRepository> _repository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    private static CpeMapping Suggested(string name, string vendor, string product)
    {
        var mapping = CpeMapping.Discover(CpeSubjectKind.Application, name, name);
        mapping.Suggest(vendor, product, CpeSuggestionSource.Ai, "Looks like the desktop client.");
        return mapping;
    }

    private void Holding(params CpeMapping[] mappings)
    {
        _repository
            .Setup(r => r.GetMappingsByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) =>
                mappings.Where(m => ids.Contains(m.Id)).ToList());

        _repository
            .Setup(r => r.GetAssessmentsForMappingAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<CpeAssessment>());
    }

    private ConfirmCpeMappingsCommandHandler ConfirmHandler() => new(_repository.Object, _unitOfWork.Object);

    private ResetCpeMappingsCommandHandler ResetHandler() => new(_repository.Object, _unitOfWork.Object);

    [Fact]
    public async Task Confirm_AcceptsWhatEachRowAlreadyProposes()
    {
        var firefox = Suggested("Firefox", "mozilla", "firefox");
        var chrome = Suggested("Google Chrome", "google", "chrome");
        Holding(firefox, chrome);

        var result = await ConfirmHandler().Handle(
            new ConfirmCpeMappingsCommand(new[] { firefox.Id, chrome.Id }), CancellationToken.None);

        Assert.Equal(2, result.Applied);
        Assert.Empty(result.Skipped);
        Assert.Equal(CpeMappingStatus.Confirmed, firefox.Status);
        Assert.Equal("chrome", chrome.Product);
    }

    [Fact]
    public async Task Confirm_KeepsTheSuggestionsOwnSource()
    {
        // Nobody typed anything, so stamping these Manual would claim a person entered a pair they
        // only accepted — and the Confidence column reads this field to say so.
        var firefox = Suggested("Firefox", "mozilla", "firefox");
        Holding(firefox);

        await ConfirmHandler().Handle(new ConfirmCpeMappingsCommand(new[] { firefox.Id }), CancellationToken.None);

        Assert.Equal(CpeSuggestionSource.Ai, firefox.SuggestionSource);
    }

    [Fact]
    public async Task Confirm_SkipsARowWithNothingProposed_AndSaysWhich()
    {
        // Ticking every row on the screen is the obvious thing to do, and most of a fresh queue has
        // no suggestion yet. Reported by name, because "3 of 40 confirmed" with no detail is how a
        // subject sits unassessed for months.
        var unmapped = CpeMapping.Discover(CpeSubjectKind.Application, "In-House Tool", "In-House Tool");
        var firefox = Suggested("Firefox", "mozilla", "firefox");
        Holding(unmapped, firefox);

        var result = await ConfirmHandler().Handle(
            new ConfirmCpeMappingsCommand(new[] { unmapped.Id, firefox.Id }), CancellationToken.None);

        Assert.Equal(1, result.Applied);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal("In-House Tool", skipped.DisplayName);
        Assert.Contains("Nothing is proposed", skipped.Reason);
        Assert.Equal(CpeMappingStatus.Unmapped, unmapped.Status);
    }

    [Fact]
    public async Task Confirm_LeavesAnAlreadyConfirmedRowAlone()
    {
        // Re-confirming is not harmless: Confirm() stamps ConfirmedAtUtc, and a bulk click must not
        // rewrite the record of when a human actually decided.
        var firefox = Suggested("Firefox", "mozilla", "firefox");
        firefox.Confirm("mozilla", "firefox", CpeSuggestionSource.Manual);
        var confirmedAt = firefox.ConfirmedAtUtc;
        Holding(firefox);

        var result = await ConfirmHandler().Handle(
            new ConfirmCpeMappingsCommand(new[] { firefox.Id }), CancellationToken.None);

        Assert.Equal(0, result.Applied);
        Assert.Equal(confirmedAt, firefox.ConfirmedAtUtc);
        Assert.Contains("Already confirmed", Assert.Single(result.Skipped).Reason);
    }

    [Fact]
    public async Task Confirm_ReportsAnIdThatIsNoLongerThere_RatherThanFailingTheBatch()
    {
        var firefox = Suggested("Firefox", "mozilla", "firefox");
        Holding(firefox);

        var result = await ConfirmHandler().Handle(
            new ConfirmCpeMappingsCommand(new[] { firefox.Id, Guid.NewGuid() }), CancellationToken.None);

        Assert.Equal(1, result.Applied);
        Assert.Contains("no longer exists", Assert.Single(result.Skipped).Reason);
    }

    [Fact]
    public async Task Confirm_SavesOnceForTheWholeSelection()
    {
        Holding(Suggested("Firefox", "mozilla", "firefox"), Suggested("Google Chrome", "google", "chrome"));
        var ids = new[] { Guid.NewGuid() };

        await ConfirmHandler().Handle(new ConfirmCpeMappingsCommand(ids), CancellationToken.None);

        // Nothing applied, so nothing written.
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Clear_ReturnsRowsToTheQueueAndDiscardsTheirFindings()
    {
        // The findings describe the product the subject used to claim to be; leaving them behind
        // an unmapped row is how a CVE count outlives the mapping that produced it.
        var firefox = Suggested("Firefox", "mozilla", "firefox");
        firefox.Confirm("mozilla", "firefox", CpeSuggestionSource.Manual);
        Holding(firefox);

        var result = await ResetHandler().Handle(
            new ResetCpeMappingsCommand(new[] { firefox.Id }), CancellationToken.None);

        Assert.Equal(1, result.Applied);
        Assert.Equal(CpeMappingStatus.Unmapped, firefox.Status);
        Assert.Null(firefox.Vendor);
        _repository.Verify(
            r => r.GetAssessmentsForMappingAsync(firefox.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Clear_SkipsARowAlreadyInTheQueue()
    {
        var unmapped = CpeMapping.Discover(CpeSubjectKind.Application, "In-House Tool", "In-House Tool");
        Holding(unmapped);

        var result = await ResetHandler().Handle(
            new ResetCpeMappingsCommand(new[] { unmapped.Id }), CancellationToken.None);

        Assert.Equal(0, result.Applied);
        Assert.Contains("Already in the queue", Assert.Single(result.Skipped).Reason);
    }
}

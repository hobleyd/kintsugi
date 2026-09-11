using Moq;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.Vulnerabilities.Commands.ConfirmCpeMapping;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.Vulnerabilities;

/// <summary>
/// Confirming one subject: the dictionary check that makes a typo survivable, and what the row
/// then claims about where its vendor and product came from.
/// </summary>
public class ConfirmCpeMappingCommandHandlerTests
{
    private readonly Mock<IVulnerabilityRepository> _repository = new();
    private readonly Mock<IVulnerabilitySettingsProvider> _settings = new();
    private readonly Mock<INvdClient> _nvd = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    public ConfirmCpeMappingCommandHandlerTests()
    {
        _settings
            .Setup(s => s.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VulnerabilitySettingsSnapshot(true, null, 24, 250, 4000, true, null, null));

        _nvd
            .Setup(n => n.CpeExistsAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _repository
            .Setup(r => r.GetAssessmentsForMappingAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<CpeAssessment>());
    }

    private ConfirmCpeMappingCommandHandler Handler() =>
        new(_repository.Object, _settings.Object, _nvd.Object, _unitOfWork.Object);

    private CpeMapping Holding(CpeMapping mapping)
    {
        _repository
            .Setup(r => r.GetMappingAsync(mapping.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mapping);
        return mapping;
    }

    private static CpeMapping Suggested(string name, string vendor, string product)
    {
        var mapping = CpeMapping.Discover(CpeSubjectKind.Application, name, name);
        mapping.Suggest(vendor, product, CpeSuggestionSource.Ai, "The desktop client.");
        return mapping;
    }

    [Fact]
    public async Task AcceptingASuggestionUnchanged_KeepsWhereThePairCameFrom()
    {
        // The queue's row tick submits exactly what the row already carries, and the bulk confirm
        // does the same thing to forty rows at once. If this stamped Manual, the same action would
        // describe itself two different ways depending on which control was pressed — and the
        // Confidence column's tooltip reads this field.
        var mapping = Holding(Suggested("Firefox", "mozilla", "firefox"));

        await Handler().Handle(
            new ConfirmCpeMappingCommand(mapping.Id, "mozilla", "firefox"), CancellationToken.None);

        Assert.Equal(CpeMappingStatus.Confirmed, mapping.Status);
        Assert.Equal(CpeSuggestionSource.Ai, mapping.SuggestionSource);
    }

    [Fact]
    public async Task CorrectingASuggestion_MakesItAHandEnteredPair()
    {
        var mapping = Holding(Suggested("Zoom", "aisquared", "zoomtext"));

        await Handler().Handle(
            new ConfirmCpeMappingCommand(mapping.Id, "zoom", "zoom_workplace_desktop"),
            CancellationToken.None);

        Assert.Equal(CpeSuggestionSource.Manual, mapping.SuggestionSource);
        Assert.Equal("zoom_workplace_desktop", mapping.Product);
    }

    [Fact]
    public async Task ConfirmingFromNothing_IsAlwaysHandEntered()
    {
        var mapping = Holding(CpeMapping.Discover(CpeSubjectKind.Application, "Firefox", "Firefox"));

        await Handler().Handle(
            new ConfirmCpeMappingCommand(mapping.Id, "mozilla", "firefox"), CancellationToken.None);

        Assert.Equal(CpeSuggestionSource.Manual, mapping.SuggestionSource);
    }

    [Fact]
    public async Task APairTheDictionaryDoesNotContain_IsRefused()
    {
        // The check that stops a typo attributing another product's CVEs to this one, and the
        // reason the single-row route is not what a bulk confirm calls.
        _nvd
            .Setup(n => n.CpeExistsAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var mapping = Holding(Suggested("Firefox", "mozilla", "firefox"));

        await Assert.ThrowsAsync<ConflictException>(() => Handler().Handle(
            new ConfirmCpeMappingCommand(mapping.Id, "mozilla", "firefax"), CancellationToken.None));

        Assert.Equal(CpeMappingStatus.Suggested, mapping.Status);
    }
}

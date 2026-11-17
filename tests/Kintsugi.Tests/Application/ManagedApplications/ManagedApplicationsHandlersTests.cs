using MediatR;
using Moq;
using Kintsugi.Application.Applications;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.ManagedApplications;
using Kintsugi.Application.ManagedApplications.Commands.AddManagedOwner;
using Kintsugi.Application.ManagedApplications.Commands.MarkApplicationsManaged;
using Kintsugi.Application.ManagedApplications.Commands.RemoveManagedOwner;
using Kintsugi.Application.ManagedApplications.Commands.UnmarkApplicationManaged;
using Kintsugi.Application.ManagedApplications.Queries.GetManagedApplications;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Tests.Application.ManagedApplications;

/// <summary>
/// Saying that another tool patches an application, taking it back, and the list of tools that can own
/// one. Marking is a decision with a consequence — Kintsugi stops patching it everywhere — so what
/// these pin is that it does exactly what was asked and nothing more.
/// </summary>
public class ManagedApplicationsHandlersTests
{
    private readonly Mock<IManagedApplicationRepository> _repository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ISender> _sender = new();
    private readonly Mock<IInstalledApplicationRepository> _installed = new();
    private readonly List<ManagedApplication> _added = new();
    private readonly List<ManagedOwner> _addedOwners = new();

    public ManagedApplicationsHandlersTests()
    {
        _repository.Setup(r => r.AddAsync(It.IsAny<ManagedApplication>(), It.IsAny<CancellationToken>()))
            .Callback<ManagedApplication, CancellationToken>((a, _) => _added.Add(a)).Returns(Task.CompletedTask);
        _repository.Setup(r => r.AddOwnerAsync(It.IsAny<ManagedOwner>(), It.IsAny<CancellationToken>()))
            .Callback<ManagedOwner, CancellationToken>((o, _) => _addedOwners.Add(o)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.Send(It.IsAny<GetManagedApplicationsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManagedApplicationsDto(Array.Empty<ManagedApplicationDto>(), Array.Empty<string>()));
    }

    private MarkApplicationsManagedCommandHandler Mark() => new(_repository.Object, _unitOfWork.Object, _sender.Object);

    private void OwnerOnList(string name) =>
        _repository.Setup(r => r.FindOwnerAsync(name, It.IsAny<CancellationToken>())).ReturnsAsync(ManagedOwner.Create(name));

    [Fact]
    public async Task Mark_AddsEachApplicationUnderTheOwner_AndSaves()
    {
        OwnerOnList("CrowdStrike");

        await Mark().Handle(new MarkApplicationsManagedCommand(new[] { "Falcon Sensor", "Falcon" }, "CrowdStrike"), CancellationToken.None);

        Assert.Equal(new[] { "Falcon Sensor", "Falcon" }, _added.Select(a => a.ApplicationName));
        Assert.All(_added, a => Assert.Equal("CrowdStrike", a.Owner));
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Mark_MovesAnApplicationThatIsAlreadyMarkedToTheNewOwner_RatherThanAddingASecond()
    {
        OwnerOnList("NinjaOne");
        var existing = ManagedApplication.Create("Falcon", "CrowdStrike");
        _repository.Setup(r => r.FindAsync("Falcon", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        await Mark().Handle(new MarkApplicationsManagedCommand(new[] { "Falcon" }, "NinjaOne"), CancellationToken.None);

        Assert.Equal("NinjaOne", existing.Owner);
        Assert.Empty(_added);
    }

    [Fact]
    public async Task Mark_MarksANameTypedTwiceOnce()
    {
        OwnerOnList("Jamf Pro");

        await Mark().Handle(new MarkApplicationsManagedCommand(new[] { "Self Service+", " self service+ ", "" }, "Jamf Pro"), CancellationToken.None);

        Assert.Equal("Self Service+", Assert.Single(_added).ApplicationName);
    }

    [Fact]
    public async Task Mark_UsesTheOwnerAsTheListSpellsIt()
    {
        _repository.Setup(r => r.FindOwnerAsync("crowdstrike", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ManagedOwner.Create("CrowdStrike"));

        await Mark().Handle(new MarkApplicationsManagedCommand(new[] { "Falcon" }, "crowdstrike"), CancellationToken.None);

        Assert.Equal("CrowdStrike", Assert.Single(_added).Owner);
        Assert.Empty(_addedOwners);
    }

    [Fact]
    public async Task Mark_AddsAnOwnerThatIsNotOnTheListYet()
    {
        await Mark().Handle(new MarkApplicationsManagedCommand(new[] { "Teams" }, "Intune"), CancellationToken.None);

        Assert.Equal("Intune", Assert.Single(_addedOwners).Name);
        Assert.Equal("Intune", Assert.Single(_added).Owner);
    }

    [Fact]
    public async Task Mark_RefusesAnOwnerThatIsBlank()
    {
        await Assert.ThrowsAsync<DomainException>(() =>
            Mark().Handle(new MarkApplicationsManagedCommand(new[] { "Teams" }, "   "), CancellationToken.None));
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void MarkValidator_RefusesNothingToMark_AndTooManyAtOnce()
    {
        var validator = new MarkApplicationsManagedCommandValidator();

        Assert.False(validator.Validate(new MarkApplicationsManagedCommand(Array.Empty<string>(), "NinjaOne")).IsValid);
        Assert.False(validator.Validate(new MarkApplicationsManagedCommand(
            Enumerable.Range(0, MarkApplicationsManagedCommandValidator.MaxNames + 1).Select(i => $"app {i}").ToList(), "NinjaOne")).IsValid);
        Assert.True(validator.Validate(new MarkApplicationsManagedCommand(new[] { "Falcon" }, "CrowdStrike")).IsValid);
        Assert.False(validator.Validate(new MarkApplicationsManagedCommand(new[] { new string('x', 256) }, "NinjaOne")).IsValid);
    }

    [Fact]
    public async Task Unmark_GivesTheApplicationBack()
    {
        var existing = ManagedApplication.Create("Falcon", "CrowdStrike");
        _repository.Setup(r => r.FindAsync("falcon", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        await new UnmarkApplicationManagedCommandHandler(_repository.Object, _unitOfWork.Object, _sender.Object)
            .Handle(new UnmarkApplicationManagedCommand("falcon"), CancellationToken.None);

        _repository.Verify(r => r.Remove(existing), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Unmark_OfAnApplicationThatIsNotMarked_IsNotAnError_AndSavesNothing()
    {
        await new UnmarkApplicationManagedCommandHandler(_repository.Object, _unitOfWork.Object, _sender.Object)
            .Handle(new UnmarkApplicationManagedCommand("Slack"), CancellationToken.None);

        _repository.Verify(r => r.Remove(It.IsAny<ManagedApplication>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddOwner_AddsATool_AndAddingOneAlreadyThereChangesNothing()
    {
        var handler = new AddManagedOwnerCommandHandler(_repository.Object, _unitOfWork.Object, _sender.Object);

        await handler.Handle(new AddManagedOwnerCommand("Intune"), CancellationToken.None);
        Assert.Equal("Intune", Assert.Single(_addedOwners).Name);

        _addedOwners.Clear();
        // The real repository finds an owner without regard to case; the mock has to be told that.
        _repository.Setup(r => r.FindOwnerAsync("intune", It.IsAny<CancellationToken>())).ReturnsAsync(ManagedOwner.Create("Intune"));
        await handler.Handle(new AddManagedOwnerCommand("intune"), CancellationToken.None);
        Assert.Empty(_addedOwners);
    }

    [Fact]
    public async Task RemoveOwner_TakesAToolOffTheList()
    {
        var owner = ManagedOwner.Create("Intune");
        _repository.Setup(r => r.FindOwnerAsync("Intune", It.IsAny<CancellationToken>())).ReturnsAsync(owner);

        await new RemoveManagedOwnerCommandHandler(_repository.Object, _unitOfWork.Object, _sender.Object)
            .Handle(new RemoveManagedOwnerCommand("Intune"), CancellationToken.None);

        _repository.Verify(r => r.RemoveOwner(owner), Times.Once);
    }

    [Fact]
    public async Task RemoveOwner_IsRefusedWhileAnApplicationIsStillManagedByIt()
    {
        var owner = ManagedOwner.Create("NinjaOne");
        _repository.Setup(r => r.FindOwnerAsync("NinjaOne", It.IsAny<CancellationToken>())).ReturnsAsync(owner);
        _repository.Setup(r => r.OwnerIsInUseAsync("NinjaOne", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            new RemoveManagedOwnerCommandHandler(_repository.Object, _unitOfWork.Object, _sender.Object)
                .Handle(new RemoveManagedOwnerCommand("NinjaOne"), CancellationToken.None));

        Assert.Contains("NinjaOne still patches some applications", error.Message);
        _repository.Verify(r => r.RemoveOwner(It.IsAny<ManagedOwner>()), Times.Never);
    }

    [Fact]
    public async Task Get_ListsApplicationsByOwner_WithHostCounts_AndTheOwnerList()
    {
        _repository.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            ManagedApplication.Create("Self Service+", "Jamf Pro"),
            ManagedApplication.Create("Falcon Sensor", "CrowdStrike"),
            ManagedApplication.Create("Falcon", "CrowdStrike"),
        });
        _repository.Setup(r => r.GetOwnersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            ManagedOwner.Create("NinjaOne"), ManagedOwner.Create("CrowdStrike"), ManagedOwner.Create("Jamf Pro"),
        });
        _installed.Setup(i => i.GetSummariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new ApplicationSummaryDto("Falcon Sensor", 48, Array.Empty<string>(), Array.Empty<ApplicationSummaryDto>()),
            new ApplicationSummaryDto("Self Service+", 40, Array.Empty<string>(), new[]
            {
                new ApplicationSummaryDto("falcon", 3, Array.Empty<string>(), Array.Empty<ApplicationSummaryDto>()),
            }),
        });

        var result = await new GetManagedApplicationsQueryHandler(_repository.Object, _installed.Object)
            .Handle(new GetManagedApplicationsQuery(), CancellationToken.None);

        Assert.Equal(
            new[] { ("Falcon", "CrowdStrike", 3), ("Falcon Sensor", "CrowdStrike", 48), ("Self Service+", "Jamf Pro", 40) },
            result.Applications.Select(a => (a.ApplicationName, a.Owner, a.HostCount)));
        Assert.Equal(new[] { "CrowdStrike", "Jamf Pro", "NinjaOne" }, result.Owners);
    }

    [Fact]
    public async Task Get_GivesAMarkedApplicationNobodyHasInstalledAHostCountOfZero()
    {
        _repository.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { ManagedApplication.Create("Teams", "Intune") });
        _repository.Setup(r => r.GetOwnersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ManagedOwner>());
        _installed.Setup(i => i.GetSummariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ApplicationSummaryDto>());

        var result = await new GetManagedApplicationsQueryHandler(_repository.Object, _installed.Object)
            .Handle(new GetManagedApplicationsQuery(), CancellationToken.None);

        Assert.Equal(0, Assert.Single(result.Applications).HostCount);
    }
}

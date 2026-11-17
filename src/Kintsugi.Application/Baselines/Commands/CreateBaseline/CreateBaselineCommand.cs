using FluentValidation;
using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Baselines.Commands.CreateBaseline;

/// <summary>
/// Creates a baseline and its first version, unsigned. Nothing reaches an agent until a person signs a
/// version (<see cref="SignBaselineVersion.SignBaselineVersionCommand"/>) and assigns the baseline.
/// </summary>
/// <param name="CreatedBy">The signed-in administrator, set by the controller — never from the body.</param>
public record CreateBaselineCommand(string Name, IReadOnlyList<BaselineItem> Items, string? CreatedBy = null)
    : IRequest<CreatedBaselineVersionDto>;

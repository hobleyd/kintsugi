using FluentValidation;
using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Baselines.Commands.CreateBaselineVersion;

/// <summary>
/// Adds a new, unsigned version to a baseline. Versions are never edited — changing one value makes a
/// new version — so a signature always vouches for the bytes a reviewer saw. Agents keep running the
/// previous signed version until this one is signed.
/// </summary>
public record CreateBaselineVersionCommand(Guid BaselineId, IReadOnlyList<BaselineItem> Items, string? CreatedBy = null)
    : IRequest<CreatedBaselineVersionDto>;

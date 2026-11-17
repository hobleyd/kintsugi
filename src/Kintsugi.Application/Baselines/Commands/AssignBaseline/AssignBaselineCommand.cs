using FluentValidation;
using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Application.Baselines.Commands.AssignBaseline;

/// <summary>
/// Sets who a baseline applies to: every Windows host, a list of hosts by name, or both. Replaces the
/// previous assignment. An empty one takes the baseline off every device without losing its history.
/// </summary>
public record AssignBaselineCommand(Guid BaselineId, bool AllWindowsHosts, IReadOnlyList<string> HostNames, string? AssignedBy = null)
    : IRequest;

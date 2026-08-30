using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.AiSettings.Queries.GetGooseCliStatus;

public record GetGooseCliStatusQuery(string? Endpoint) : IRequest<GooseCliStatus>;

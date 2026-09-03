using System.Net;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.WebApi.Middleware;

public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (ValidationException ex)
        {
            var problemDetails = new ValidationProblemDetails(
                ex.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()))
            {
                Status = (int)HttpStatusCode.BadRequest,
                Title = "One or more validation errors occurred."
            };

            await WriteProblemDetailsAsync(context, problemDetails);
        }
        catch (DomainException ex)
        {
            var problemDetails = new ProblemDetails
            {
                Status = (int)HttpStatusCode.BadRequest,
                Title = "Request could not be processed.",
                Detail = ex.Message
            };

            await WriteProblemDetailsAsync(context, problemDetails);
        }
        catch (NotFoundException ex)
        {
            var problemDetails = new ProblemDetails
            {
                Status = (int)HttpStatusCode.NotFound,
                Title = "Resource not found.",
                Detail = ex.Message
            };

            await WriteProblemDetailsAsync(context, problemDetails);
        }
        catch (ForbiddenException ex)
        {
            var problemDetails = new ProblemDetails
            {
                Status = (int)HttpStatusCode.Forbidden,
                Title = "Request not authorized.",
                Detail = ex.Message
            };

            await WriteProblemDetailsAsync(context, problemDetails);
        }
        catch (ConflictException ex)
        {
            var problemDetails = new ProblemDetails
            {
                Status = (int)HttpStatusCode.Conflict,
                Title = "Request conflicts with existing data.",
                Detail = ex.Message
            };

            await WriteProblemDetailsAsync(context, problemDetails);
        }
        catch (ExternalServiceException ex)
        {
            var problemDetails = new ProblemDetails
            {
                Status = (int)HttpStatusCode.BadGateway,
                Title = "A dependent external service could not be reached.",
                Detail = ex.Message
            };

            await WriteProblemDetailsAsync(context, problemDetails);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception processing {Method} {Path}", context.Request.Method, context.Request.Path);

            var problemDetails = new ProblemDetails
            {
                Status = (int)HttpStatusCode.InternalServerError,
                Title = "An unexpected error occurred."
            };

            await WriteProblemDetailsAsync(context, problemDetails);
        }
    }

    private static Task WriteProblemDetailsAsync(HttpContext context, ProblemDetails problemDetails)
    {
        context.Response.ContentType = "application/problem+json";
        context.Response.StatusCode = problemDetails.Status ?? (int)HttpStatusCode.InternalServerError;
        return context.Response.WriteAsJsonAsync(problemDetails);
    }
}

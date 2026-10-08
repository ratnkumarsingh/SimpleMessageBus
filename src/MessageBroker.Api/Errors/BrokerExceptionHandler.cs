using MessageBroker.Application.Errors;
using MessageBroker.Contracts;
using MessageBroker.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace MessageBroker.Api.Errors;

/// <summary>
/// Turns <see cref="BrokerException"/>s (including the stored procedures' 50xxx errors, mapped by
/// SqlErrorMapper) into RFC 9457 problem details. Anything else falls through to the default 500.
/// </summary>
public sealed class BrokerExceptionHandler(IProblemDetailsService problemDetails, ILogger<BrokerExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        ProblemDetails problem;
        switch (exception)
        {
            case BrokerValidationException validation:
                problem = new ValidationProblemDetails(validation.Errors
                    .GroupBy(e => e.Field)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray()))
                {
                    Status = StatusCodes.Status400BadRequest,
                    Type = ProblemTypes.Validation,
                    Title = BrokerProblems.Describe(BrokerErrorKind.Validation).Title,
                };
                break;

            case BrokerException broker:
                var description = BrokerProblems.Describe(broker.Kind);
                problem = new ProblemDetails
                {
                    Status = description.Status,
                    Type = description.Type,
                    Title = description.Title,
                    Detail = broker.Message,
                };
                if (broker.Kind == BrokerErrorKind.Forbidden)
                    logger.LogWarning("Permission denied on {Method} {Path}: {Detail}", context.Request.Method, context.Request.Path, broker.Message);
                else if (broker.Kind == BrokerErrorKind.Unavailable)
                    logger.LogError(exception, "Message store unavailable on {Method} {Path}", context.Request.Method, context.Request.Path);
                break;

            case BadHttpRequestException badRequest:
                problem = new ProblemDetails
                {
                    Status = badRequest.StatusCode,
                    Type = BrokerProblems.TypeForStatus(badRequest.StatusCode),
                    Title = badRequest.StatusCode == StatusCodes.Status413PayloadTooLarge
                        ? BrokerProblems.Describe(BrokerErrorKind.PayloadTooLarge).Title
                        : BrokerProblems.Describe(BrokerErrorKind.Validation).Title,
                    Detail = badRequest.Message,
                };
                break;

            default:
                return false;
        }

        context.Response.StatusCode = problem.Status!.Value;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = problem,
            Exception = exception,
        });
    }
}

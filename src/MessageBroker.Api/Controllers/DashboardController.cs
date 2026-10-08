using MessageBroker.Application.Services;
using MessageBroker.Contracts.Models;
using Microsoft.AspNetCore.Mvc;

namespace MessageBroker.Api.Controllers;

/// <summary>Read models for the admin dashboard (Admin). Requeue uses POST /api/v1/deadletters/{id}/requeue.</summary>
[Route("api/v1/admin")]
public sealed class DashboardController(DashboardService dashboard) : BrokerController
{
    /// <summary>Totals, a per-minute throughput series over windowMinutes (5–1440, default 60) and subscription health.</summary>
    [HttpGet("overview")]
    public Task<OverviewResponse> Overview([FromQuery] int? windowMinutes, CancellationToken ct) =>
        dashboard.GetOverviewAsync(Caller, windowMinutes, ct);

    /// <summary>Messages newest first, filtered. Pass the previous page's nextCursor as cursor.</summary>
    [HttpGet("messages")]
    public Task<MessageSearchResponse> Messages([FromQuery] MessageSearchRequest request, CancellationToken ct) =>
        dashboard.SearchMessagesAsync(Caller, request, ct);

    /// <summary>Dead letters across every subscription, newest first. Pass the previous page's nextBefore as before.</summary>
    [HttpGet("deadletters")]
    public Task<DeadLetterPage> DeadLetters([FromQuery] DeadLetterSearchRequest request, CancellationToken ct) =>
        dashboard.SearchDeadLettersAsync(Caller, request, ct);
}

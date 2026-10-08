using MessageBroker.Api.Auth;
using MessageBroker.Application.Dispatch;
using MessageBroker.Contracts;
using MessageBroker.Contracts.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace MessageBroker.Api.Hubs;

/// <summary>
/// Live activity for admin dashboards (<see cref="AdminHub"/>). Admin keys only: the endpoint's
/// policy turns other keys away at negotiate (403). Clients only listen.
/// </summary>
[Authorize(Roles = BrokerClaims.AdminRole)]
public sealed class AdminActivityHub(BrokerActivityFeed feed, ILogger<AdminActivityHub> logger) : Hub
{
    public const string AdminsGroup = "admins";

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, AdminsGroup);
        feed.AdminConnected();
        logger.LogInformation("Admin dashboard {ConnectionId} connected for {AppId}", Context.ConnectionId, Context.User!.ToCaller().AppId);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        feed.AdminDisconnected();
        logger.LogInformation("Admin dashboard {ConnectionId} disconnected", Context.ConnectionId);
        return Task.CompletedTask;
    }
}

/// <summary>[Fix 10] Sends the activity feed to the admins group.</summary>
public sealed class HubAdminActivitySink(IHubContext<AdminActivityHub> hub) : IAdminActivitySink
{
    public Task SendActivityAsync(IReadOnlyList<MessageActivity> batch, CancellationToken ct) =>
        hub.Clients.Group(AdminActivityHub.AdminsGroup).SendAsync(AdminHub.Activity, batch, ct);

    public Task SendOverviewChangedAsync(CancellationToken ct) =>
        hub.Clients.Group(AdminActivityHub.AdminsGroup).SendAsync(AdminHub.OverviewChanged, ct);
}

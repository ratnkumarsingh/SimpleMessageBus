using System.Net;
using System.Net.Http.Json;
using MessageBroker.Contracts;
using MessageBroker.Contracts.Models;
using MessageBroker.IntegrationTests.Infrastructure;

namespace MessageBroker.IntegrationTests.Api;

public sealed class AdminApiTests(SqlServerFixture sql) : ApiTest(sql)
{
    private async Task<List<string>> ListHostsAsync() =>
        (await ReadAsync<List<AllowedHostResponse>>(await Admin.GetAsync("/api/v1/admin/webhook-hosts"))).Select(h => h.Host).ToList();

    [Fact(DisplayName = "A07 [Fix 9] Allowlist: list, add (normalized, idempotent), remove; bad hosts are 400, missing is 404")]
    public async Task A07_AllowlistEndpoints()
    {
        Assert.Equal([ApiFactory.SeededHost], await ListHostsAsync());

        await ExpectAsync(Admin.PostAsJsonAsync("/api/v1/admin/webhook-hosts", new AllowedHostRequest("  Billing.Internal ")), HttpStatusCode.NoContent);
        await ExpectAsync(Admin.PostAsJsonAsync("/api/v1/admin/webhook-hosts", new AllowedHostRequest("billing.internal")), HttpStatusCode.NoContent);
        await ExpectAsync(Admin.PostAsJsonAsync("/api/v1/admin/webhook-hosts", new AllowedHostRequest("10.20.30.40")), HttpStatusCode.NoContent);
        Assert.Equal(["10.20.30.40", "billing.internal", ApiFactory.SeededHost], await ListHostsAsync());

        var added = (await ReadAsync<List<AllowedHostResponse>>(await Admin.GetAsync("/api/v1/admin/webhook-hosts")))
            .Single(h => h.Host == "billing.internal");
        Assert.NotNull(added.AddedBy);

        foreach (var bad in new[] { "", "https://billing.internal", "billing.internal:443", "billing.internal/path", "has space" })
            await ProblemAsync(await Admin.PostAsJsonAsync("/api/v1/admin/webhook-hosts", new AllowedHostRequest(bad)),
                HttpStatusCode.BadRequest, ProblemTypes.Validation);

        await ExpectAsync(Admin.DeleteAsync("/api/v1/admin/webhook-hosts/BILLING.internal"), HttpStatusCode.NoContent);
        Assert.DoesNotContain("billing.internal", await ListHostsAsync());
        await ProblemAsync(await Admin.DeleteAsync("/api/v1/admin/webhook-hosts/billing.internal"), HttpStatusCode.NotFound, ProblemTypes.NotFound);
    }

    [Fact(DisplayName = "A07b Startup seeds configured hosts and never removes hosts an Admin added")]
    public async Task A07b_ConfigSeeding()
    {
        await ExpectAsync(Admin.PostAsJsonAsync("/api/v1/admin/webhook-hosts", new AllowedHostRequest("admin-added.internal")), HttpStatusCode.NoContent);

        // A second host with a different configuration starts against the same database.
        await using var other = new ApiFactory(Sql.ConnectionString, new Dictionary<string, string?>
        {
            ["Broker:Webhooks:AllowedHosts:0"] = "Payments.Internal",
            ["Broker:Webhooks:AllowedHosts:1"] = ApiFactory.SeededHost,
        });
        _ = other.Server; // starts the host, which runs the startup tasks

        Assert.Equal(["admin-added.internal", ApiFactory.SeededHost, "payments.internal"], await ListHostsAsync());

        // Running the seed again changes nothing.
        await other.RunStartupTasksAsync();
        Assert.Equal(3, (await ListHostsAsync()).Count);
    }

    [Fact(DisplayName = "A07c Applications and permissions: register, list, grant (idempotent), list, revoke; invalid input is 400")]
    public async Task A07c_ApplicationsAndPermissions()
    {
        var created = await Admin.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("ProjectA"));
        var app = await ReadAsync<ApplicationResponse>(created, HttpStatusCode.Created);
        Assert.Equal(("ProjectA", false, true), (app.Name, app.IsAdmin, app.IsActive));
        Assert.Equal(app, await ReadAsync<ApplicationResponse>(await Admin.GetAsync($"/api/v1/admin/applications/{app.AppId}")));
        Assert.Contains(app, await ReadAsync<List<ApplicationResponse>>(await Admin.GetAsync("/api/v1/admin/applications")));

        await ProblemAsync(await Admin.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("projecta")),
            HttpStatusCode.Conflict, ProblemTypes.Conflict);
        await ProblemAsync(await Admin.GetAsync($"/api/v1/admin/applications/{Guid.NewGuid()}"), HttpStatusCode.NotFound, ProblemTypes.NotFound);

        var topic = await CreateTopicViaApiAsync();
        var grant = new PermissionRequest("topic", topic.TopicId, "publish");
        await ExpectAsync(Admin.PostAsJsonAsync($"/api/v1/admin/applications/{app.AppId}/permissions", grant), HttpStatusCode.NoContent);
        await ExpectAsync(Admin.PostAsJsonAsync($"/api/v1/admin/applications/{app.AppId}/permissions", grant), HttpStatusCode.NoContent);
        var permissions = await ReadAsync<List<PermissionResponse>>(await Admin.GetAsync($"/api/v1/admin/applications/{app.AppId}/permissions"));
        Assert.Equal(new PermissionResponse(app.AppId, "Topic", topic.TopicId, "Publish"), Assert.Single(permissions));

        foreach (var bad in new[]
        {
            new PermissionRequest("Queue", topic.TopicId, "Publish"),
            new PermissionRequest("Topic", topic.TopicId, "Delete"),
            new PermissionRequest("Topic", Guid.Empty, "Publish"),
            new PermissionRequest("Topic", topic.TopicId, "1"),
        })
        {
            await ProblemAsync(await Admin.PostAsJsonAsync($"/api/v1/admin/applications/{app.AppId}/permissions", bad),
                HttpStatusCode.BadRequest, ProblemTypes.Validation);
        }
        await ProblemAsync(await Admin.PostAsJsonAsync($"/api/v1/admin/applications/{app.AppId}/permissions",
            new PermissionRequest("Topic", Guid.NewGuid(), "Publish")), HttpStatusCode.NotFound, ProblemTypes.NotFound);

        await ExpectAsync(Admin.PostAsJsonAsync($"/api/v1/admin/applications/{app.AppId}/permissions/revoke", grant), HttpStatusCode.NoContent);
        Assert.Empty(await ReadAsync<List<PermissionResponse>>(await Admin.GetAsync($"/api/v1/admin/applications/{app.AppId}/permissions")));
    }
}

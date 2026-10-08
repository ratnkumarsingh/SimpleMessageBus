namespace MessageBroker.Dashboard;

/// <summary>The "Dashboard" configuration section.</summary>
public sealed class DashboardOptions
{
    public const string SectionName = "Dashboard";

    /// <summary>The broker's base address; REST and the admin hub (/hubs/admin) live under it.</summary>
    public Uri BrokerUrl { get; set; } = new("http://localhost:5080/");

    /// <summary>How long a sign-in lasts without activity.</summary>
    public int SessionHours { get; set; } = 8;

    /// <summary>While the live feed is down, pages re-read their data this often.</summary>
    public int FallbackPollSeconds { get; set; } = 10;

    /// <summary>Pages re-read at most this often when live events arrive.</summary>
    public int RefreshThrottleMs { get; set; } = 1000;

    /// <summary>Optional directory for the Data Protection keys that encrypt the sign-in cookie.</summary>
    public string? DataProtectionKeysDirectory { get; set; }
}

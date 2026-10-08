namespace MessageBroker.Application;

/// <summary>The "Broker" configuration section (spec section 14).</summary>
public sealed class BrokerOptions
{
    public const string SectionName = "Broker";

    public DatabaseOptions Database { get; set; } = new();
    public BootstrapOptions Bootstrap { get; set; } = new();
    public DispatcherOptions Dispatcher { get; set; } = new();
    public SubscriptionDefaults Defaults { get; set; } = new();
    public WebhookOptions Webhooks { get; set; } = new();
    public LimitOptions Limits { get; set; } = new();
    public RetentionOptions Retention { get; set; } = new();
}

public sealed class DatabaseOptions
{
    /// <summary>Run the DbUp deployer when the host starts. Turn off where DBAs apply the release script.</summary>
    public bool DeploySchemaOnStartup { get; set; } = true;
    /// <summary>Create the database when it is missing (development only).</summary>
    public bool CreateDatabase { get; set; }
}

/// <summary>
/// The first admin application. When AdminApiKey is set, startup ensures an admin application with
/// that key exists, so a new environment can be configured through the API.
/// </summary>
public sealed class BootstrapOptions
{
    public string AdminName { get; set; } = "broker-admin";
    public string? AdminApiKey { get; set; }
}

public sealed class DispatcherOptions
{
    /// <summary>Run the background loops (maintenance, retention, and later push delivery) in this host.</summary>
    public bool Enabled { get; set; } = true;

    public int PollIntervalMs { get; set; } = 1000;
    public int MaintenanceIntervalSeconds { get; set; } = 5;
}

public sealed class SubscriptionDefaults
{
    public int MaxAttempts { get; set; } = 4;
    public int LockDurationSeconds { get; set; } = 60;
    public int RetryBaseDelaySeconds { get; set; } = 30;
    public int RetryMaxDelaySeconds { get; set; } = 900;
    public int MaxConcurrentDeliveries { get; set; } = 8;
}

public sealed class WebhookOptions
{
    public int TimeoutSeconds { get; set; } = 30;
    /// <summary>Seeds broker.WebhookAllowedHosts on startup; Admins maintain the list through the API.</summary>
    public List<string> AllowedHosts { get; set; } = [];
    /// <summary>False only for local development against plain-HTTP receivers.</summary>
    public bool RequireHttps { get; set; } = true;
    public int CircuitFailureThreshold { get; set; } = 5;
    public int CircuitOpenSeconds { get; set; } = 60;
}

public sealed class LimitOptions
{
    public int MaxPayloadBytes { get; set; } = 262_144;
    public int MaxPropertiesBytes { get; set; } = 4_096;
}

public sealed class RetentionOptions
{
    public int CompletedDays { get; set; } = 14;
    public int DeadLetterDays { get; set; } = 90;
    public int IntervalMinutes { get; set; } = 60;
    public int BatchSize { get; set; } = 5000;
}

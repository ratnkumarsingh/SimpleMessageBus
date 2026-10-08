using Samples.Shared;

namespace ConsoleSubscriber;

/// <summary>
/// One of the three ways ConsoleSubscriber receives the notifications topic. Each channel is its own
/// subscription, so every notification arrives once per channel; each channel has its own feed and
/// handler (keyed services under <see cref="Name"/>) so one channel's deduplication does not hide
/// another's copy.
/// </summary>
public sealed record ConsoleChannel(string Name, Func<SampleSettings, SampleApp> App);

public static class ConsoleChannels
{
    public static readonly ConsoleChannel SignalR = new("SignalR", s => s.ConsoleSignalR);
    public static readonly ConsoleChannel Pull = new("Pull", s => s.ConsolePull);
    public static readonly ConsoleChannel Webhook = new("Webhook", s => s.ConsoleWebhook);

    public static readonly IReadOnlyList<ConsoleChannel> All = [SignalR, Pull, Webhook];
}

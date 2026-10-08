using MessageBroker.Domain;
using static MessageBroker.Domain.DeliveryStatus;

namespace MessageBroker.UnitTests.Domain;

/// <summary>U08 — delivery state machine and derived message status.</summary>
public class DeliveryStateMachineTests
{
    [Theory]
    [InlineData(Pending, Leased)]
    [InlineData(Pending, DeadLettered)]
    [InlineData(Pending, Cancelled)]
    [InlineData(Leased, Completed)]
    [InlineData(Leased, Pending)]
    [InlineData(Leased, DeadLettered)]
    [InlineData(Leased, Cancelled)]
    [InlineData(DeadLettered, Pending)]
    public void Allowed_transitions(DeliveryStatus from, DeliveryStatus to) =>
        Assert.True(DeliveryStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(Pending, Completed)]
    [InlineData(Completed, Pending)]
    [InlineData(Completed, DeadLettered)]
    [InlineData(Cancelled, Pending)]
    [InlineData(DeadLettered, Leased)]
    [InlineData(DeadLettered, Completed)]
    public void Forbidden_transitions(DeliveryStatus from, DeliveryStatus to) =>
        Assert.False(DeliveryStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(Completed, true)]
    [InlineData(Cancelled, true)]
    [InlineData(DeadLettered, false)]
    [InlineData(Pending, false)]
    [InlineData(Leased, false)]
    public void Terminal_states(DeliveryStatus status, bool terminal) =>
        Assert.Equal(terminal, DeliveryStateMachine.IsTerminal(status));

    [Theory]
    [InlineData(MessageStatus.InProgress, Pending, Completed)]
    [InlineData(MessageStatus.InProgress, Leased, DeadLettered)]
    [InlineData(MessageStatus.Completed, Completed, Completed)]
    [InlineData(MessageStatus.Completed, Completed, Cancelled)]
    [InlineData(MessageStatus.PartiallyDeadLettered, Completed, DeadLettered)]
    [InlineData(MessageStatus.DeadLettered, DeadLettered, DeadLettered)]
    [InlineData(MessageStatus.DeadLettered, DeadLettered, Cancelled)]
    public void Derived_message_status(MessageStatus expected, params DeliveryStatus[] deliveries) =>
        Assert.Equal(expected, DeliveryStateMachine.DeriveMessageStatus(deliveries));

    [Fact]
    public void No_deliveries_or_only_cancelled_is_completed()
    {
        Assert.Equal(MessageStatus.Completed, DeliveryStateMachine.DeriveMessageStatus([]));
        Assert.Equal(MessageStatus.Completed, DeliveryStateMachine.DeriveMessageStatus([Cancelled]));
    }
}

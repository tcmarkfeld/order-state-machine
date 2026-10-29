using Microsoft.Extensions.Logging.Abstractions;
using OrderStateMachine.Api.Orders;

namespace OrderStateMachine.Tests;

public class OrderTests
{
    private readonly FakePaymentGateway _payments = new();
    private readonly OrderDependencies _dependencies;

    public OrderTests()
    {
        _dependencies = new OrderDependencies(
            _payments, new FakeOrderFulfillment(), new SteppingClock(), NullLogger<OrderDependencies>.Instance);
    }

    public static TheoryData<IOrderState> TerminalStates =>
        [new CompleteState(), new RejectedState(), new CancelledState(), new NeedsAttentionState()];

    public static TheoryData<IOrderState> StatesWithoutResolve =>
        [new InitializedState(), new PaymentAuthorizedState(), new CompleteState(), new RejectedState(), new CancelledState()];

    [Theory]
    [MemberData(nameof(TerminalStates))]
    public async Task TerminalStates_AllowNoActions(IOrderState state)
    {
        var order = new Order(Guid.NewGuid(), DateTimeOffset.UnixEpoch);

        await Assert.ThrowsAsync<InvalidOrderTransitionException>(() => state.AuthorizePaymentAsync(order, _dependencies));
        await Assert.ThrowsAsync<InvalidOrderTransitionException>(() => state.CompleteAsync(order, _dependencies));
    }

    [Theory]
    [MemberData(nameof(StatesWithoutResolve))]
    public async Task OnlyNeedsAttention_AllowsResolve(IOrderState state)
    {
        var order = new Order(Guid.NewGuid(), DateTimeOffset.UnixEpoch);

        await Assert.ThrowsAsync<InvalidOrderTransitionException>(
            () => state.ResolveAsync(order, _dependencies, ResolutionAction.Void, "test"));
    }

    [Fact]
    public async Task PaymentAuthorized_CannotAuthorizeAgain()
    {
        var order = new Order(Guid.NewGuid(), DateTimeOffset.UnixEpoch);
        await order.AuthorizePaymentAsync(_dependencies);

        await Assert.ThrowsAsync<InvalidOrderTransitionException>(() => order.AuthorizePaymentAsync(_dependencies));
        Assert.Equal(1, _payments.AuthorizeCalls);
    }

    [Fact]
    public async Task Transition_RecordsFromToTimestampAndReason()
    {
        var order = new Order(Guid.NewGuid(), DateTimeOffset.UnixEpoch);

        await order.AuthorizePaymentAsync(_dependencies);

        var transition = order.History[^1];
        Assert.Equal(OrderState.Initialized, transition.From);
        Assert.Equal(OrderState.PaymentAuthorized, transition.To);
        Assert.True(transition.At > order.History[0].At);
        Assert.Equal("Payment authorized (auth_123)", transition.Reason);
    }
}

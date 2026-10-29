using Microsoft.Extensions.Logging.Abstractions;
using OrderStateMachine.Api.Gateways;
using OrderStateMachine.Api.Orders;

namespace OrderStateMachine.Tests;

public class OrderServiceTests
{
    private readonly FakePaymentGateway _payments = new();
    private readonly FakeOrderFulfillment _fulfillment = new();
    private readonly OrderService _service;

    public OrderServiceTests()
    {
        var dependencies = new OrderDependencies(
            _payments, _fulfillment, new SteppingClock(), NullLogger<OrderDependencies>.Instance)
        {
            GatewayTimeout = TimeSpan.FromMilliseconds(200),
        };
        _service = new OrderService(dependencies);
    }

    [Fact]
    public async Task HappyPath_ReachesCompleteWithFullHistory()
    {
        var order = _service.Create();

        await _service.AuthorizePaymentAsync(order.Id);
        await _service.CompleteAsync(order.Id);

        Assert.Equal(OrderState.Complete, order.State);
        Assert.Equal("auth_123", order.AuthorizationId);
        Assert.Equal(
            [OrderState.Initialized, OrderState.PaymentAuthorized, OrderState.Complete],
            order.History.Select(t => t.To));
        Assert.Equal(order.History.Select(t => t.At).Order(), order.History.Select(t => t.At));
        Assert.Empty(_payments.VoidCalls);
    }

    [Fact]
    public async Task PaymentDeclined_RejectsWithoutCleanup()
    {
        _payments.Authorize = () => OperationResult.Failure("Card declined");
        var order = _service.Create();

        await _service.AuthorizePaymentAsync(order.Id);

        Assert.Equal(OrderState.Rejected, order.State);
        Assert.Contains("Card declined", order.History[^1].Reason);
        Assert.Null(order.AuthorizationId);
        Assert.Empty(_payments.VoidCalls);
    }

    [Fact]
    public async Task CompletionFails_VoidSucceeds_Cancels()
    {
        _fulfillment.Complete = () => OperationResult.Failure("Tickets no longer available");
        var order = _service.Create();

        await _service.AuthorizePaymentAsync(order.Id);
        await _service.CompleteAsync(order.Id);

        Assert.Equal(OrderState.Cancelled, order.State);
        Assert.Equal(["auth_123"], _payments.VoidCalls);
        Assert.Contains("Tickets no longer available", order.History[^1].Reason);
    }

    [Fact]
    public async Task CompletionFails_VoidFails_NeedsAttention()
    {
        _fulfillment.Complete = () => OperationResult.Failure("Tickets no longer available");
        _payments.Void = () => OperationResult.Failure("Void rejected by payment provider");
        var order = _service.Create();

        await _service.AuthorizePaymentAsync(order.Id);
        await _service.CompleteAsync(order.Id);

        Assert.Equal(OrderState.NeedsAttention, order.State);
        Assert.Equal(["auth_123"], _payments.VoidCalls);
        Assert.Contains("Tickets no longer available", order.History[^1].Reason);
        Assert.Contains("Void rejected by payment provider", order.History[^1].Reason);
    }

    [Fact]
    public async Task CompletionThrows_DoesNotVoid_NeedsAttention()
    {
        // The request may have reached fulfillment, so tickets may exist. Voiding would give them away.
        _fulfillment.Complete = () => throw new HttpRequestException("connection reset");
        var order = _service.Create();

        await _service.AuthorizePaymentAsync(order.Id);
        await _service.CompleteAsync(order.Id);

        Assert.Equal(OrderState.NeedsAttention, order.State);
        Assert.Empty(_payments.VoidCalls);
        Assert.Contains("outcome unknown", order.History[^1].Reason);
        Assert.Contains("connection reset", order.History[^1].Reason);
    }

    [Fact]
    public async Task CompletionTimesOut_DoesNotVoid_NeedsAttention()
    {
        _fulfillment.CompleteDelay = TimeSpan.FromSeconds(10);
        var order = _service.Create();

        await _service.AuthorizePaymentAsync(order.Id);
        await _service.CompleteAsync(order.Id);

        Assert.Equal(OrderState.NeedsAttention, order.State);
        Assert.Empty(_payments.VoidCalls);
        Assert.Contains("No response within", order.History[^1].Reason);
        // The fake sees the same token fire; give its cancellation handler a moment to run.
        Assert.True(SpinWait.SpinUntil(() => _fulfillment.CompleteCancelled, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task CompletionFails_VoidThrows_NeedsAttention()
    {
        _fulfillment.Complete = () => OperationResult.Failure("Tickets no longer available");
        _payments.Void = () => throw new HttpRequestException("connection reset");
        var order = _service.Create();

        await _service.AuthorizePaymentAsync(order.Id);
        await _service.CompleteAsync(order.Id);

        Assert.Equal(OrderState.NeedsAttention, order.State);
        Assert.Equal(["auth_123"], _payments.VoidCalls);
        Assert.Contains("Void of payment auth_123 outcome unknown", order.History[^1].Reason);
    }

    [Fact]
    public async Task AuthorizationTimesOut_StaysInitializedAndCanBeRetried()
    {
        _payments.AuthorizeDelay = TimeSpan.FromSeconds(10);
        var order = _service.Create();

        await Assert.ThrowsAsync<OrderOutcomeUnknownException>(() => _service.AuthorizePaymentAsync(order.Id));
        Assert.Equal(OrderState.Initialized, order.State);

        _payments.AuthorizeDelay = TimeSpan.Zero;
        await _service.AuthorizePaymentAsync(order.Id);
        Assert.Equal(OrderState.PaymentAuthorized, order.State);
    }

    [Fact]
    public async Task AuthorizationOutcomeUnknown_IsNotTreatedAsDecline()
    {
        // A hold may exist, so rejecting would leave it on the card with nothing to void it.
        _payments.Authorize = () => OperationResult.Unknown("connection reset");
        var order = _service.Create();

        await Assert.ThrowsAsync<OrderOutcomeUnknownException>(() => _service.AuthorizePaymentAsync(order.Id));
        Assert.Equal(OrderState.Initialized, order.State);
        Assert.Single(order.History);
    }

    [Fact]
    public async Task AuthorizationSucceedsWithoutId_IsTreatedAsUnknown()
    {
        _payments.Authorize = () => new OperationResult(OperationOutcome.Succeeded);
        var order = _service.Create();

        await Assert.ThrowsAsync<OrderOutcomeUnknownException>(() => _service.AuthorizePaymentAsync(order.Id));
        Assert.Equal(OrderState.Initialized, order.State);
        Assert.Null(order.AuthorizationId);
    }

    [Fact]
    public async Task ConcurrentAuthorizations_AuthorizeOnlyOnce()
    {
        _payments.AuthorizeDelay = TimeSpan.FromMilliseconds(50);
        var order = _service.Create();

        var first = _service.AuthorizePaymentAsync(order.Id);
        var second = _service.AuthorizePaymentAsync(order.Id);

        // Whichever runs second sees PaymentAuthorized, where authorizing isn't allowed.
        await Assert.ThrowsAsync<InvalidOrderTransitionException>(() => Task.WhenAll(first, second));
        Assert.Equal(1, _payments.AuthorizeCalls);
        Assert.Equal(OrderState.PaymentAuthorized, order.State);
    }

    [Fact]
    public async Task CompletingBeforeAuthorizing_Throws()
    {
        var order = _service.Create();

        await Assert.ThrowsAsync<InvalidOrderTransitionException>(() => _service.CompleteAsync(order.Id));
        Assert.Equal(OrderState.Initialized, order.State);
        Assert.Single(order.History);
    }

    [Fact]
    public async Task ActionsOnUnknownOrder_ReturnNull()
    {
        Assert.Null(await _service.AuthorizePaymentAsync(Guid.NewGuid()));
        Assert.Null(await _service.CompleteAsync(Guid.NewGuid()));
        Assert.Null(await _service.ResolveAsync(Guid.NewGuid(), ResolutionAction.Void, "test"));
    }

    [Fact]
    public async Task Resolve_Complete_CompletesOrder()
    {
        var order = await CreateOrderNeedingAttentionAsync();

        await _service.ResolveAsync(order.Id, ResolutionAction.Complete, "Tickets restocked");

        Assert.Equal(OrderState.Complete, order.State);
        Assert.Equal("Tickets restocked", order.ResolutionReason);
        Assert.Equal(OrderState.NeedsAttention, order.History[^1].From);
        Assert.Equal([order.Id], _fulfillment.CompleteCalls);
    }

    [Fact]
    public async Task Resolve_Complete_FulfillmentAlreadyCompleted_DoesNotCompleteAgain()
    {
        var order = await CreateOrderNeedingAttentionAsync();
        _fulfillment.Status = () => FulfillmentStatus.Completed;

        await _service.ResolveAsync(order.Id, ResolutionAction.Complete, "Tickets were issued");

        Assert.Equal(OrderState.Complete, order.State);
        Assert.Empty(_fulfillment.CompleteCalls);
    }

    [Theory]
    [InlineData(PaymentStatus.NotFound)]
    [InlineData(PaymentStatus.Voided)]
    [InlineData(PaymentStatus.Refunded)]
    public async Task Resolve_Complete_PaymentNotTaken_StaysNeedsAttention(PaymentStatus status)
    {
        var order = await CreateOrderNeedingAttentionAsync();
        _payments.Status = () => status;

        await _service.ResolveAsync(order.Id, ResolutionAction.Complete, "Customer wants tickets");

        Assert.Equal(OrderState.NeedsAttention, order.State);
        Assert.Contains($"payment is {status}", order.History[^1].Reason);
        Assert.Empty(_fulfillment.CompleteCalls);
    }

    [Fact]
    public async Task Resolve_Complete_CompletionFails_StaysNeedsAttentionAndCanBeRetried()
    {
        var order = await CreateOrderNeedingAttentionAsync();
        _fulfillment.Complete = () => OperationResult.Failure("Tickets no longer available");

        await _service.ResolveAsync(order.Id, ResolutionAction.Complete, "First try");

        Assert.Equal(OrderState.NeedsAttention, order.State);
        Assert.Equal(OrderState.NeedsAttention, order.History[^1].From);
        Assert.Contains("completion failed: Tickets no longer available", order.History[^1].Reason);

        _fulfillment.Complete = () => OperationResult.Success("fulfillment_123");
        await _service.ResolveAsync(order.Id, ResolutionAction.Complete, "Second try");

        Assert.Equal(OrderState.Complete, order.State);
        Assert.Equal("Second try", order.ResolutionReason);
    }

    [Fact]
    public async Task Resolve_Void_PaymentAuthorized_VoidsAndCancels()
    {
        var order = await CreateOrderNeedingAttentionAsync();

        await _service.ResolveAsync(order.Id, ResolutionAction.Void, "Customer cancelled");

        Assert.Equal(OrderState.Cancelled, order.State);
        Assert.Equal(["auth_123"], _payments.VoidCalls);
        Assert.Empty(_payments.RefundCalls);
        Assert.Equal("Customer cancelled", order.ResolutionReason);
    }

    [Fact]
    public async Task Resolve_Void_PaymentComplete_RefundsAndCancels()
    {
        var order = await CreateOrderNeedingAttentionAsync();
        _payments.Status = () => PaymentStatus.Complete;

        await _service.ResolveAsync(order.Id, ResolutionAction.Void, "Customer was charged");

        Assert.Equal(OrderState.Cancelled, order.State);
        Assert.Equal(["auth_123"], _payments.RefundCalls);
        Assert.Empty(_payments.VoidCalls);
        Assert.Contains("refunded", order.History[^1].Reason);
    }

    [Theory]
    [InlineData(PaymentStatus.NotFound)]
    [InlineData(PaymentStatus.Voided)]
    [InlineData(PaymentStatus.Refunded)]
    public async Task Resolve_Void_NothingToRelease_Cancels(PaymentStatus status)
    {
        var order = await CreateOrderNeedingAttentionAsync();
        _payments.Status = () => status;

        await _service.ResolveAsync(order.Id, ResolutionAction.Void, "Already released");

        Assert.Equal(OrderState.Cancelled, order.State);
        Assert.Empty(_payments.VoidCalls);
        Assert.Empty(_payments.RefundCalls);
    }

    [Fact]
    public async Task Resolve_Void_RefundFails_StaysNeedsAttention()
    {
        var order = await CreateOrderNeedingAttentionAsync();
        _payments.Status = () => PaymentStatus.Complete;
        _payments.Refund = () => OperationResult.Failure("Refund rejected by payment provider");

        await _service.ResolveAsync(order.Id, ResolutionAction.Void, "Customer was charged");

        Assert.Equal(OrderState.NeedsAttention, order.State);
        Assert.Contains("refund of payment auth_123 failed: Refund rejected by payment provider", order.History[^1].Reason);
    }

    [Theory]
    [InlineData(ResolutionAction.Complete)]
    [InlineData(ResolutionAction.Void)]
    public async Task Resolve_PaymentStatusLookupThrows_StaysNeedsAttention(ResolutionAction action)
    {
        var order = await CreateOrderNeedingAttentionAsync();
        _payments.Status = () => throw new HttpRequestException("connection reset");

        await _service.ResolveAsync(order.Id, action, "Trying");

        Assert.Equal(OrderState.NeedsAttention, order.State);
        Assert.Contains("payment status lookup failed", order.History[^1].Reason);
        Assert.Contains("connection reset", order.History[^1].Reason);
        Assert.Empty(_payments.VoidCalls);
        Assert.Empty(_fulfillment.CompleteCalls);
    }

    [Fact]
    public async Task Resolve_Complete_FulfillmentStatusLookupThrows_StaysNeedsAttention()
    {
        var order = await CreateOrderNeedingAttentionAsync();
        _fulfillment.Status = () => throw new HttpRequestException("connection reset");

        await _service.ResolveAsync(order.Id, ResolutionAction.Complete, "Trying");

        Assert.Equal(OrderState.NeedsAttention, order.State);
        Assert.Contains("fulfillment status lookup failed", order.History[^1].Reason);
        Assert.Empty(_fulfillment.CompleteCalls);
    }

    [Fact]
    public async Task ResolvingOutsideNeedsAttention_Throws()
    {
        var order = _service.Create();

        await Assert.ThrowsAsync<InvalidOrderTransitionException>(
            () => _service.ResolveAsync(order.Id, ResolutionAction.Void, "test"));
        Assert.Single(order.History);
    }

    /// <summary>Completion and its void both fail, then the fakes are reset to succeed.</summary>
    private async Task<Order> CreateOrderNeedingAttentionAsync()
    {
        _fulfillment.Complete = () => OperationResult.Failure("Tickets no longer available");
        _payments.Void = () => OperationResult.Failure("Void rejected by payment provider");
        var order = _service.Create();
        await _service.AuthorizePaymentAsync(order.Id);
        await _service.CompleteAsync(order.Id);
        Assert.Equal(OrderState.NeedsAttention, order.State);

        _fulfillment.Complete = () => OperationResult.Success("fulfillment_123");
        _payments.Void = () => OperationResult.Success("auth_123");
        _fulfillment.CompleteCalls.Clear();
        _payments.VoidCalls.Clear();
        return order;
    }
}

using OrderStateMachine.Api.Gateways;

namespace OrderStateMachine.Tests;

public sealed class FakePaymentGateway : IPaymentGateway
{
    private int _authorizeCalls;

    public Func<OperationResult> Authorize { get; set; } = () => OperationResult.Success("auth_123");
    public TimeSpan AuthorizeDelay { get; set; }
    public int AuthorizeCalls => _authorizeCalls;
    public Func<OperationResult> Void { get; set; } = () => OperationResult.Success("auth_123");
    public List<string> VoidCalls { get; } = [];
    public Func<OperationResult> Refund { get; set; } = () => OperationResult.Success("auth_123");
    public List<string> RefundCalls { get; } = [];
    public Func<PaymentStatus> Status { get; set; } = () => PaymentStatus.Authorized;

    public async Task<OperationResult> AuthorizeAsync(Guid orderId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _authorizeCalls);
        await Task.Delay(AuthorizeDelay, cancellationToken);
        return Authorize();
    }

    public Task<OperationResult> VoidAsync(string authorizationId, CancellationToken cancellationToken)
    {
        VoidCalls.Add(authorizationId);
        return Task.FromResult(Void());
    }

    public Task<OperationResult> RefundAsync(string authorizationId, CancellationToken cancellationToken)
    {
        RefundCalls.Add(authorizationId);
        return Task.FromResult(Refund());
    }

    public Task<PaymentStatus> GetStatusAsync(Guid orderId, CancellationToken cancellationToken) => Task.FromResult(Status());
}

public sealed class FakeOrderFulfillment : IOrderFulfillment
{
    public Func<OperationResult> Complete { get; set; } = () => OperationResult.Success("fulfillment_123");
    public TimeSpan CompleteDelay { get; set; }
    public bool CompleteCancelled { get; private set; }
    public List<Guid> CompleteCalls { get; } = [];
    public Func<FulfillmentStatus> Status { get; set; } = () => FulfillmentStatus.NotFound;

    public async Task<OperationResult> CompleteAsync(Guid orderId, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(CompleteDelay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            CompleteCancelled = true;
            throw;
        }

        CompleteCalls.Add(orderId);
        return Complete();
    }

    public Task<FulfillmentStatus> GetStatusAsync(Guid orderId, CancellationToken cancellationToken) => Task.FromResult(Status());
}

/// <summary>Moves forward one second per read so each history entry gets a distinct, predictable timestamp.</summary>
public sealed class SteppingClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now = _now.AddSeconds(1);
}

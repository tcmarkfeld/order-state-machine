using OrderStateMachine.Api.Gateways;

namespace OrderStateMachine.Api.Orders;

/// <summary>What the state classes need from the outside world to do their work.</summary>
public sealed class OrderDependencies(
    IPaymentGateway payments,
    IOrderFulfillment fulfillment,
    TimeProvider clock,
    ILogger<OrderDependencies> logger)
{
    public IPaymentGateway Payments => payments;

    public IOrderFulfillment Fulfillment => fulfillment;

    public ILogger Logger => logger;

    public DateTimeOffset Now => clock.GetUtcNow();

    /// <summary>
    /// Upper bound on each gateway call. Without it, a hung dependency would hold the order's
    /// lock indefinitely. A call that runs out of time has an unknown outcome, not a failed one.
    /// </summary>
    public TimeSpan GatewayTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Passes the call a token that is cancelled after <see cref="GatewayTimeout"/>, and reports
    /// that as a <see cref="TimeoutException"/>. Any other exception propagates.
    /// </summary>
    /// <remarks>
    /// The token is deliberately not linked to the HTTP request, so a client disconnecting can't
    /// cancel a void halfway through. Cancelling only stops our side: a request that already
    /// reached the provider may still be processed, which is why a timeout is an unknown outcome.
    /// </remarks>
    private async Task<T> CallAsync<T>(Func<CancellationToken, Task<T>> call)
    {
        using var timeout = new CancellationTokenSource(GatewayTimeout, clock);
        try
        {
            // WaitAsync also enforces the timeout on an implementation that ignores the token.
            return await call(timeout.Token).WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"No response within {GatewayTimeout.TotalSeconds}s");
        }
    }

    /// <summary>
    /// Like <see cref="CallAsync"/>, but always returns a result: a thrown exception or a timeout
    /// becomes <see cref="OperationOutcome.Unknown"/>, because the request may have gone through.
    /// </summary>
    public async Task<OperationResult> CallSafelyAsync(Func<CancellationToken, Task<OperationResult>> call)
    {
        try
        {
            return await CallAsync(call);
        }
        catch (TimeoutException ex)
        {
            return OperationResult.Unknown(ex.Message);
        }
        catch (Exception ex)
        {
            return OperationResult.Unknown($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs a status lookup under the same timeout. Returns a null status and the error if the
    /// provider threw or didn't answer in time.
    /// </summary>
    public async Task<(TStatus? Status, string? Error)> LookupSafelyAsync<TStatus>(Func<CancellationToken, Task<TStatus>> lookup)
        where TStatus : struct
    {
        try
        {
            return (await CallAsync(lookup), null);
        }
        catch (TimeoutException ex)
        {
            return (null, ex.Message);
        }
        catch (Exception ex)
        {
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}

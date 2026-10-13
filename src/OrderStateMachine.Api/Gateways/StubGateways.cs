namespace OrderStateMachine.Api.Gateways;

/// <summary>
/// Switches for the stub dependencies, bound from the "Stubs" config section so each failure
/// path can be exercised against the running API.
/// </summary>
public sealed class StubOptions
{
    public bool DeclinePayment { get; set; }
    public bool FailCompletion { get; set; }
    public bool FailVoid { get; set; }
}

public sealed class StubPaymentGateway(StubOptions options) : IPaymentGateway
{
    public Task<OperationResult> AuthorizeAsync(Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult(options.DeclinePayment
            ? OperationResult.Failure("Card declined")
            : OperationResult.Success($"auth_{orderId:N}"));

    public Task<OperationResult> VoidAsync(string authorizationId, CancellationToken cancellationToken) =>
        Task.FromResult(options.FailVoid
            ? OperationResult.Failure("Void rejected by payment provider")
            : OperationResult.Success(authorizationId));
}

public sealed class StubOrderFulfillment(StubOptions options) : IOrderFulfillment
{
    public Task<OperationResult> CompleteAsync(Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult(options.FailCompletion
            ? OperationResult.Failure("Tickets no longer available")
            : OperationResult.Success($"fulfillment_{orderId:N}"));
}

namespace OrderStateMachine.Api.Gateways;

public interface IPaymentGateway
{
    /// <summary>
    /// Places a hold on the customer's payment method. <paramref name="orderId"/> doubles as the
    /// idempotency key so a retried request can't create a second hold.
    /// </summary>
    Task<OperationResult> AuthorizeAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>Releases a hold created by <see cref="AuthorizeAsync"/>.</summary>
    Task<OperationResult> VoidAsync(string authorizationId, CancellationToken cancellationToken);
}

/// <summary>Completes the order after payment is authorized, e.g. capturing payment and issuing tickets.</summary>
public interface IOrderFulfillment
{
    Task<OperationResult> CompleteAsync(Guid orderId, CancellationToken cancellationToken);
}

public enum OperationOutcome
{
    Succeeded,

    /// <summary>The dependency answered and said no. Nothing happened on its side.</summary>
    Failed,

    /// <summary>
    /// No definitive answer (timeout, dropped connection). The operation may or may not have
    /// happened, so it must not be treated as a failure.
    /// </summary>
    Unknown,
}

/// <summary>Outcome of a call to an external dependency. <see cref="Reference"/> is the provider's ID on success.</summary>
public sealed record OperationResult(OperationOutcome Outcome, string? Reference = null, string? Error = null)
{
    public bool Succeeded => Outcome == OperationOutcome.Succeeded;

    public static OperationResult Success(string reference) => new(OperationOutcome.Succeeded, Reference: reference);

    public static OperationResult Failure(string error) => new(OperationOutcome.Failed, Error: error);

    public static OperationResult Unknown(string error) => new(OperationOutcome.Unknown, Error: error);
}

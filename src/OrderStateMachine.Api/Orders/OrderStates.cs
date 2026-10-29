using OrderStateMachine.Api.Gateways;

namespace OrderStateMachine.Api.Orders;

/// <summary>
/// The actions that can be taken on an order. Every state implements all of them; what each one
/// does, and which state comes next, is up to the state.
/// </summary>
public interface IOrderState
{
    OrderState Name { get; }

    Task AuthorizePaymentAsync(Order order, OrderDependencies dependencies);

    Task CompleteAsync(Order order, OrderDependencies dependencies);

    Task ResolveAsync(Order order, OrderDependencies dependencies, ResolutionAction action, string reason);
}

/// <summary>
/// Makes every action illegal by default, so each state overrides only the actions it allows and
/// terminal states override nothing.
/// </summary>
public abstract class OrderStateBase : IOrderState
{
    public abstract OrderState Name { get; }

    public virtual Task AuthorizePaymentAsync(Order order, OrderDependencies dependencies) =>
        throw NotAllowed(order, "authorize payment");

    public virtual Task CompleteAsync(Order order, OrderDependencies dependencies) =>
        throw NotAllowed(order, "complete");

    public virtual Task ResolveAsync(Order order, OrderDependencies dependencies, ResolutionAction action, string reason) =>
        throw NotAllowed(order, "resolve");

    private InvalidOrderTransitionException NotAllowed(Order order, string action) =>
        new($"Cannot {action} for order {order.Id} while it is {Name}.");

    protected static void FlagForAttention(Order order, OrderDependencies dependencies, string reason)
    {
        order.MoveTo(new NeedsAttentionState(), dependencies.Now, reason);
        dependencies.Logger.LogError("Order {OrderId} needs manual resolution: {Reason}", order.Id, reason);
    }
}

public sealed class InitializedState : OrderStateBase
{
    public override OrderState Name => OrderState.Initialized;

    public override async Task AuthorizePaymentAsync(Order order, OrderDependencies dependencies)
    {
        var result = await dependencies.CallSafelyAsync(ct => dependencies.Payments.AuthorizeAsync(order.Id, ct));

        switch (result)
        {
            case { Succeeded: true, Reference: { } authorizationId }:
                order.AuthorizationId = authorizationId;
                order.MoveTo(new PaymentAuthorizedState(), dependencies.Now, $"Payment authorized ({authorizationId})");
                break;

            case { Outcome: OperationOutcome.Failed }:
                // Nothing was charged, so rejecting is the whole recovery.
                order.MoveTo(new RejectedState(), dependencies.Now, $"Payment declined: {result.Error}");
                break;

            default:
                // Unknown, or a success without the ID we'd need to void it later. A hold may exist, so
                // this is not a decline. Stay Initialized: a retry with the same idempotency key settles it.
                var reason = result.Succeeded ? "authorization ID missing" : result.Error;
                dependencies.Logger.LogWarning("Authorization outcome unknown for order {OrderId}: {Reason}", order.Id, reason);
                throw new OrderOutcomeUnknownException($"Authorization outcome unknown: {reason}. Retry to settle it.");
        }
    }
}

/// <summary>
/// Money is on hold, so every path out of this state must end somewhere definite: completed,
/// voided, or flagged for a person. Nothing here is allowed to leave the order stuck.
/// </summary>
public sealed class PaymentAuthorizedState : OrderStateBase
{
    public override OrderState Name => OrderState.PaymentAuthorized;

    public override async Task CompleteAsync(Order order, OrderDependencies dependencies)
    {
        var completion = await dependencies.CallSafelyAsync(ct => dependencies.Fulfillment.CompleteAsync(order.Id, ct));
        if (completion.Succeeded)
        {
            order.MoveTo(new CompleteState(), dependencies.Now, "Order completed");
            return;
        }

        var authorizationId = order.AuthorizationId!;
        if (completion.Outcome == OperationOutcome.Unknown)
        {
            // Tickets may already have been issued. Voiding now could give them away for free,
            // so leave the authorization in place and let a person check.
            FlagForAttention(
                order,
                dependencies,
                $"Completion outcome unknown: {completion.Error}. Payment {authorizationId} left in place because tickets may have been issued.");
            return;
        }

        // Fulfillment definitively failed: money is on hold for tickets the fan won't get.
        var voided = await dependencies.CallSafelyAsync(ct => dependencies.Payments.VoidAsync(authorizationId, ct));
        if (voided.Succeeded)
        {
            order.MoveTo(
                new CancelledState(),
                dependencies.Now,
                $"Completion failed: {completion.Error}. Payment {authorizationId} voided.");
            return;
        }

        // Failed or unknown, the hold may still be on the card. Never report this as a clean cancellation.
        FlagForAttention(
            order,
            dependencies,
            $"Completion failed: {completion.Error}. Void of payment {authorizationId} {(voided.Outcome == OperationOutcome.Unknown ? "outcome unknown" : "failed")}: {voided.Error}.");
    }
}

// Terminal states allow no actions, so they inherit OrderStateBase's "not allowed" defaults.

public sealed class CompleteState : OrderStateBase
{
    public override OrderState Name => OrderState.Complete;
}

public sealed class RejectedState : OrderStateBase
{
    public override OrderState Name => OrderState.Rejected;
}

public sealed class CancelledState : OrderStateBase
{
    public override OrderState Name => OrderState.Cancelled;
}

/// <summary>
/// Automation stopped here, so an operator decides whether to complete or void the order. Both
/// check the providers by idempotency key first, and any failure leaves the order here with the
/// reason in its history, so the resolve can be retried.
/// </summary>
public sealed class NeedsAttentionState : OrderStateBase
{
    public override OrderState Name => OrderState.NeedsAttention;

    public override async Task ResolveAsync(Order order, OrderDependencies dependencies, ResolutionAction action, string reason)
    {
        order.ResolutionReason = reason;

        var (payment, error) = await dependencies.LookupSafelyAsync(ct => dependencies.Payments.GetStatusAsync(order.Id, ct));
        if (payment is not { } paymentStatus)
        {
            FlagForAttention(order, dependencies, $"Resolve ({action}) failed: payment status lookup failed: {error}.");
            return;
        }

        if (action == ResolutionAction.Complete)
        {
            await ResolveByCompletingAsync(order, dependencies, paymentStatus);
        }
        else
        {
            await ResolveByVoidingAsync(order, dependencies, paymentStatus);
        }
    }

    private static async Task ResolveByCompletingAsync(Order order, OrderDependencies dependencies, PaymentStatus payment)
    {
        if (payment is not (PaymentStatus.Authorized or PaymentStatus.Complete))
        {
            FlagForAttention(order, dependencies, $"Resolve (Complete) failed: payment is {payment}.");
            return;
        }

        var (fulfillment, error) = await dependencies.LookupSafelyAsync(ct => dependencies.Fulfillment.GetStatusAsync(order.Id, ct));
        if (fulfillment is null)
        {
            FlagForAttention(order, dependencies, $"Resolve (Complete) failed: fulfillment status lookup failed: {error}.");
            return;
        }

        if (fulfillment == FulfillmentStatus.Completed)
        {
            order.MoveTo(new CompleteState(), dependencies.Now, "Resolved: fulfillment had already completed");
            return;
        }

        var completion = await dependencies.CallSafelyAsync(ct => dependencies.Fulfillment.CompleteAsync(order.Id, ct));
        if (completion.Succeeded)
        {
            order.MoveTo(new CompleteState(), dependencies.Now, "Resolved: order completed");
            return;
        }

        FlagForAttention(
            order,
            dependencies,
            $"Resolve (Complete) failed: completion {(completion.Outcome == OperationOutcome.Unknown ? "outcome unknown" : "failed")}: {completion.Error}.");
    }

    private static async Task ResolveByVoidingAsync(Order order, OrderDependencies dependencies, PaymentStatus payment)
    {
        if (payment is not (PaymentStatus.Authorized or PaymentStatus.Complete))
        {
            // Voided, refunded or never created: no money to release.
            order.MoveTo(new CancelledState(), dependencies.Now, $"Resolved: payment was {payment}, nothing to release");
            return;
        }

        // A completed payment means the customer was charged, so it has to be refunded rather than voided.
        var refund = payment == PaymentStatus.Complete;
        var authorizationId = order.AuthorizationId!;
        var result = await dependencies.CallSafelyAsync(ct => refund
            ? dependencies.Payments.RefundAsync(authorizationId, ct)
            : dependencies.Payments.VoidAsync(authorizationId, ct));
        if (result.Succeeded)
        {
            order.MoveTo(
                new CancelledState(),
                dependencies.Now,
                $"Resolved: payment {authorizationId} {(refund ? "refunded" : "voided")}");
            return;
        }

        FlagForAttention(
            order,
            dependencies,
            $"Resolve (Void) failed: {(refund ? "refund" : "void")} of payment {authorizationId} {(result.Outcome == OperationOutcome.Unknown ? "outcome unknown" : "failed")}: {result.Error}.");
    }
}

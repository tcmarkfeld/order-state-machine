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

    private InvalidOrderTransitionException NotAllowed(Order order, string action) =>
        new($"Cannot {action} for order {order.Id} while it is {Name}.");
}

public sealed class InitializedState : OrderStateBase
{
    public override OrderState Name => OrderState.Initialized;

    public override async Task AuthorizePaymentAsync(Order order, OrderDependencies dependencies)
    {
        // An exception or timeout here is left to propagate: the order stays Initialized and the
        // retry is safe because the order ID is the gateway's idempotency key.
        var result = await dependencies.CallAsync(ct => dependencies.Payments.AuthorizeAsync(order.Id, ct));

        if (result.Succeeded)
        {
            order.AuthorizationId = result.Reference;
            order.MoveTo(new PaymentAuthorizedState(), dependencies.Now, $"Payment authorized ({result.Reference})");
        }
        else
        {
            // Nothing was charged, so rejecting is the whole recovery.
            order.MoveTo(new RejectedState(), dependencies.Now, $"Payment declined: {result.Error}");
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

    private static void FlagForAttention(Order order, OrderDependencies dependencies, string reason)
    {
        order.MoveTo(new NeedsAttentionState(), dependencies.Now, reason);
        dependencies.Logger.LogError("Order {OrderId} needs manual resolution: {Reason}", order.Id, reason);
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

/// <summary>Terminal for automation. A manual-resolution action would be added here as an override.</summary>
public sealed class NeedsAttentionState : OrderStateBase
{
    public override OrderState Name => OrderState.NeedsAttention;
}

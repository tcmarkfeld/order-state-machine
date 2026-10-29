using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace OrderStateMachine.Api.Orders;

/// <summary>
/// The name of each state, used in history and the API.
/// The behavior of each state lives in its class in OrderStates.cs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<OrderState>))]
public enum OrderState
{
    Initialized,
    PaymentAuthorized,
    Complete,

    /// <summary>Payment was declined. Nothing was charged, so no cleanup is needed.</summary>
    Rejected,

    /// <summary>Completion failed after authorization and the authorization was voided.</summary>
    Cancelled,

    /// <summary>
    /// Automation couldn't reach a clean end state: the void failed, or an outcome is unknown.
    /// A hold may still exist on the customer's card.
    /// </summary>
    NeedsAttention,
}

/// <summary>What an operator chooses when resolving an order in <see cref="OrderState.NeedsAttention"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ResolutionAction>))]
public enum ResolutionAction
{
    Complete,
    Void,
}

public sealed record StateTransition(OrderState? From, OrderState To, DateTimeOffset At, string Reason);

public sealed class InvalidOrderTransitionException(string message) : InvalidOperationException(message);

/// <summary>
/// A gateway gave no definitive answer, so the order was left in its current state. Retrying is
/// safe because the order ID is the gateway's idempotency key.
/// </summary>
public sealed class OrderOutcomeUnknownException(string message) : Exception(message);

/// <summary>
/// The state machine's context. It holds the current <see cref="IOrderState"/> and forwards every
/// action to it, so what an action does (or whether it's allowed at all) depends on the state.
/// </summary>
public sealed class Order
{
    private IOrderState _state = new InitializedState();

    public Order(Guid id, DateTimeOffset createdAt)
    {
        Id = id;
        History = [new StateTransition(null, _state.Name, createdAt, "Order created")];
    }

    public Guid Id { get; }

    public string? AuthorizationId { get; internal set; }

    /// <summary>Why the operator resolved the order, from the latest resolve attempt.</summary>
    public string? ResolutionReason { get; internal set; }

    /// <summary>Derived from <see cref="History"/> so a reader can't see a state and history that disagree.</summary>
    public OrderState State => History[^1].To;

    /// <summary>
    /// Every state the order has been in, oldest first. Immutable so readers always get a
    /// snapshot that can't change while they enumerate it.
    /// </summary>
    public ImmutableList<StateTransition> History { get; private set; }

    /// <summary>One action at a time per order, so two concurrent requests can't both authorize or both void.</summary>
    internal SemaphoreSlim Lock { get; } = new(1, 1);

    public Task AuthorizePaymentAsync(OrderDependencies dependencies) => _state.AuthorizePaymentAsync(this, dependencies);

    public Task CompleteAsync(OrderDependencies dependencies) => _state.CompleteAsync(this, dependencies);

    public Task ResolveAsync(OrderDependencies dependencies, ResolutionAction action, string reason) =>
        _state.ResolveAsync(this, dependencies, action, reason);

    /// <summary>Called only by state classes, which are the ones that decide what comes next.</summary>
    internal void MoveTo(IOrderState next, DateTimeOffset at, string reason)
    {
        History = History.Add(new StateTransition(_state.Name, next.Name, at, reason));
        _state = next;
    }
}

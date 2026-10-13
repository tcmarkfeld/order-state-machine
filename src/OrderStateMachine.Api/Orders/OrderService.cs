using System.Collections.Concurrent;

namespace OrderStateMachine.Api.Orders;

/// <summary>
/// Stores orders in memory and runs actions on them one at a time. What an action does is decided
/// by the order's current state, not here.
/// </summary>
public sealed class OrderService(OrderDependencies dependencies)
{
    private readonly ConcurrentDictionary<Guid, Order> _orders = new();

    // One action at a time per order, so two concurrent requests can't both authorize or both void.
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public Order Create()
    {
        var order = new Order(Guid.NewGuid(), dependencies.Now);
        _orders[order.Id] = order;
        return order;
    }

    public Order? Get(Guid id) => _orders.GetValueOrDefault(id);

    /// <summary>Returns null if the order doesn't exist.</summary>
    /// <exception cref="InvalidOrderTransitionException">The current state doesn't allow this action.</exception>
    public Task<Order?> AuthorizePaymentAsync(Guid id) => RunExclusiveAsync(id, order => order.AuthorizePaymentAsync(dependencies));

    /// <inheritdoc cref="AuthorizePaymentAsync"/>
    public Task<Order?> CompleteAsync(Guid id) => RunExclusiveAsync(id, order => order.CompleteAsync(dependencies));

    private async Task<Order?> RunExclusiveAsync(Guid id, Func<Order, Task> action)
    {
        if (Get(id) is not { } order)
        {
            return null;
        }

        var orderLock = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await orderLock.WaitAsync();
        try
        {
            await action(order);
            return order;
        }
        finally
        {
            orderLock.Release();
        }
    }
}

using Microsoft.AspNetCore.Mvc;
using OrderStateMachine.Api.Orders;

namespace OrderStateMachine.Api.Controllers;

[ApiController]
[Route("orders")]
public sealed class OrdersController(OrderService orders) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<OrderResponse>(StatusCodes.Status201Created)]
    public ActionResult<OrderResponse> Create()
    {
        var order = orders.Create();
        return CreatedAtAction(nameof(GetOrder), new { id = order.Id }, OrderResponse.From(order));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<OrderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<OrderResponse> GetOrder(Guid id) =>
        orders.Get(id) is { } order ? OrderResponse.From(order) : NotFound();

    /// <summary>Initialized → PaymentAuthorized, or Rejected if the payment is declined.</summary>
    [HttpPost("{id:guid}/authorize")]
    [ProducesResponseType<OrderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<OrderResponse>> AuthorizePayment(Guid id) => RunAsync(() => orders.AuthorizePaymentAsync(id));

    /// <summary>PaymentAuthorized → Complete, Cancelled or NeedsAttention depending on how completion and recovery go.</summary>
    [HttpPost("{id:guid}/complete")]
    [ProducesResponseType<OrderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<OrderResponse>> CompletePayment(Guid id) => RunAsync(() => orders.CompleteAsync(id));

    private async Task<ActionResult<OrderResponse>> RunAsync(Func<Task<Order?>> action)
    {
        try
        {
            return await action() is { } order ? OrderResponse.From(order) : NotFound();
        }
        catch (InvalidOrderTransitionException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}

public sealed record OrderResponse(
    Guid Id,
    OrderState State,
    string? AuthorizationId,
    IReadOnlyList<StateTransition> History)
{
    public static OrderResponse From(Order order) =>
        new(order.Id, order.State, order.AuthorizationId, order.History);
}

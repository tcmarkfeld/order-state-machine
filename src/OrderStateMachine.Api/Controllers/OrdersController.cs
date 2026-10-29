using System.ComponentModel.DataAnnotations;
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
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public Task<ActionResult<OrderResponse>> AuthorizePayment(Guid id) => RunAsync(() => orders.AuthorizePaymentAsync(id));

    /// <summary>PaymentAuthorized → Complete, Cancelled or NeedsAttention depending on how completion and recovery go.</summary>
    [HttpPost("{id:guid}/complete")]
    [ProducesResponseType<OrderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<OrderResponse>> Complete(Guid id) => RunAsync(() => orders.CompleteAsync(id));

    /// <summary>NeedsAttention → Complete or Cancelled, or back to NeedsAttention with the reason if it fails and can be retried.</summary>
    [HttpPost("{id:guid}/resolve")]
    [ProducesResponseType<OrderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<OrderResponse>> Resolve(Guid id, ResolveOrderRequest request) =>
        RunAsync(() => orders.ResolveAsync(id, request.Action!.Value, request.Reason));

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
        catch (OrderOutcomeUnknownException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}

// Action is nullable so a missing value fails validation instead of defaulting to Complete.
public sealed record ResolveOrderRequest([Required] ResolutionAction? Action, [Required] string Reason);

public sealed record OrderResponse(
    Guid Id,
    OrderState State,
    string? AuthorizationId,
    string? ResolutionReason,
    IReadOnlyList<StateTransition> History)
{
    public static OrderResponse From(Order order)
    {
        // Read History once so State and History come from the same snapshot.
        var history = order.History;
        return new(order.Id, history[^1].To, order.AuthorizationId, order.ResolutionReason, history);
    }
}

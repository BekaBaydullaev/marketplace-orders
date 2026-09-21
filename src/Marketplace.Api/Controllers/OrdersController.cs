using Marketplace.Api.Auth;
using Marketplace.Api.Contracts;
using Marketplace.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Marketplace.Api.Controllers;

[ApiController]
[Authorize]
[Route("orders")]
public class OrdersController(OrderService orderService) : ApiControllerBase
{
    private const int MaxIdempotencyKeyLength = 100;

    [HttpPost]
    public async Task<ActionResult<OrderResponse>> Create([FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CreateOrderRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > MaxIdempotencyKeyLength)
        {
            var detail = $"Idempotency-Key header is required and must be at most {MaxIdempotencyKeyLength} characters.";
            return Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await orderService.CreateAsync(User.GetUserId(), idempotencyKey, request, ct);
        if (!result.IsSuccess)
        {
            return Failure(result.Error);
        }

        if (result.Value.IsReplayed)
        {
            Response.Headers["Idempotent-Replayed"] = "true";
        }

        var order = result.Value.Order;
        return CreatedAtAction(nameof(GetById), new { id = order.Id }, order);
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<OrderResponse>> GetById(long id, CancellationToken ct)
    {
        var result = await orderService.GetByIdAsync(id, User.GetUserId(), ct);
        return result.IsSuccess ? Ok(result.Value) : Failure(result.Error);
    }

    [HttpPost("{id:long}/pay")]
    public async Task<ActionResult<OrderResponse>> Pay(long id, CancellationToken ct)
    {
        var result = await orderService.PayAsync(id, User.GetUserId(), ct);
        return result.IsSuccess ? Ok(result.Value) : Failure(result.Error);
    }

    [HttpPost("{id:long}/cancel")]
    public async Task<ActionResult<OrderResponse>> Cancel(long id, CancellationToken ct)
    {
        var result = await orderService.CancelAsync(id, User.GetUserId(), ct);
        return result.IsSuccess ? Ok(result.Value) : Failure(result.Error);
    }
}
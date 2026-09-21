using Marketplace.Api.Contracts;

namespace Marketplace.Api.Services;

public record OrderCreationResult(OrderResponse Order, bool IsReplayed);
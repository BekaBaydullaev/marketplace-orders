namespace Marketplace.Api.Contracts;

public record CreateProductRequest(string Name, decimal Price, int Stock);

public record ProductResponse(long Id, string Name, decimal Price, int Stock, DateTimeOffset CreatedAt);
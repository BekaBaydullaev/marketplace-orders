using Marketplace.Api.Caching;
using Marketplace.Api.Contracts;
using Marketplace.Api.Models;
using Marketplace.Api.Repositories;

namespace Marketplace.Api.Services;

public class ProductService(ProductRepository productRepository, RedisCache cache)
{
    private const decimal MaxPrice = 9_999_999_999.99m;
    private static readonly TimeSpan ProductCacheTtl = TimeSpan.FromMinutes(5);

    public async Task<ServiceResult<ProductResponse>> CreateAsync(CreateProductRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200)
        {
            return ServiceError.Validation("Name is required and must be at most 200 characters.");
        }

        if (request.Price <= 0 || request.Price > MaxPrice || decimal.Round(request.Price, 2) != request.Price)
        {
            return ServiceError.Validation("Price must be positive and have at most 2 decimal places.");
        }

        if (request.Stock < 0)
        {
            return ServiceError.Validation("Stock cannot be negative.");
        }

        var product = await productRepository.InsertAsync(request.Name.Trim(), request.Price, request.Stock, ct);
        return product.ToResponse();
    }

    public async Task<ServiceResult<ProductResponse>> GetByIdAsync(long productId, CancellationToken ct)
    {
        var cacheKey = CacheKeys.Product(productId);
        var product = await cache.GetAsync<Product>(cacheKey);

        if (product is null)
        {
            product = await productRepository.GetByIdAsync(productId, ct);
            if (product is null)
            {
                return ServiceError.NotFound($"Product {productId} not found.");
            }

            await cache.SetAsync(cacheKey, product, ProductCacheTtl);
        }

        return product.ToResponse();
    }
}
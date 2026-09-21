using Marketplace.Api.Services;
using Microsoft.Extensions.Options;

namespace Marketplace.Api.BackgroundJobs;

public class ExpiredOrdersJob(IServiceScopeFactory scopeFactory, IOptions<OrderOptions> options, ILogger<ExpiredOrdersJob> logger) : BackgroundService
{
    private const int BatchSize = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.ExpirationCheckInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var orderService = scope.ServiceProvider.GetRequiredService<OrderService>();

                var cancelledCount = await orderService.CancelExpiredAsync(BatchSize, stoppingToken);
                if (cancelledCount > 0)
                {
                    logger.LogInformation("Cancelled {Count} expired orders", cancelledCount);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Expired orders cancellation failed");
            }
        }
    }
}
using System.Text.Json;
using StackExchange.Redis;

namespace Marketplace.Api.Caching;

public class RedisCache(IConnectionMultiplexer redis, ILogger<RedisCache> logger)
{
    public async Task<T?> GetAsync<T>(string key) where T : class
    {
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(key);
            return value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<T>(value.ToString());
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            logger.LogWarning(ex, "Cache read failed for {Key}", key);
            return null;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan ttl)
    {
        try
        {
            await redis.GetDatabase().StringSetAsync(key, JsonSerializer.Serialize(value), ttl);
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            logger.LogWarning(ex, "Cache write failed for {Key}", key);
        }
    }

    public async Task RemoveAsync(params string[] keys)
    {
        try
        {
            await redis.GetDatabase().KeyDeleteAsync(keys.Select(x => (RedisKey)x).ToArray());
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            logger.LogWarning(ex, "Cache invalidation failed for {Keys}", string.Join(", ", keys));
        }
    }
}
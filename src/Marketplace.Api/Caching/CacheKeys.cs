namespace Marketplace.Api.Caching;

public static class CacheKeys
{
    public static string Product(long id) => $"v1:product:{id}";
    public static string Order(long id) => $"v1:order:{id}";
}
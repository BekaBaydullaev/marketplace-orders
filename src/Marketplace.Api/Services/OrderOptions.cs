namespace Marketplace.Api.Services;

public class OrderOptions
{
    public const string SectionName = "Orders";

    public TimeSpan PaymentTimeout { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan ExpirationCheckInterval { get; set; } = TimeSpan.FromSeconds(30);
}
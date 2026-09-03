namespace KaringLatencyMonitor.Core.Services;

public class KaringApiException : Exception
{
    public KaringApiException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class KaringControllerUnavailableException : KaringApiException
{
    public KaringControllerUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class KaringUnauthorizedException : KaringApiException
{
    public KaringUnauthorizedException()
        : base("Karing 控制器拒绝访问，请检查 API secret。")
    {
    }
}

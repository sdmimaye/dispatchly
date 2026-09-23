namespace Dispatchly.Transport.PostgreSql;

internal static class DeliveryBackoff
{
    public static TimeSpan ForAttempt(TimeSpan visibility, TimeSpan maxBackoff, int attempt)
    {
        var shift = Math.Min(Math.Max(attempt - 1, 0), 20);
        var factor = 1d;
        for (var i = 0; i < shift; i++)
        {
            factor *= 2;
        }

        var ticks = visibility.Ticks * factor;
        if (double.IsInfinity(ticks) || ticks >= maxBackoff.Ticks)
        {
            return maxBackoff;
        }

        return TimeSpan.FromTicks((long)ticks);
    }
}

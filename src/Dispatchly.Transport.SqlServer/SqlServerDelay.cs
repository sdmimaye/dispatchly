namespace Dispatchly;

internal static class SqlServerDelay
{
    public static int Milliseconds(TimeSpan value, string paramName)
    {
        var milliseconds = Math.Ceiling(value.TotalMilliseconds);
        if (milliseconds < 1 || milliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(paramName, "The interval must be between 1 millisecond and 24 days.");
        }

        return (int)milliseconds;
    }
}

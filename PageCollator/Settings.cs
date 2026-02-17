namespace PageCollator;

/// <summary>
/// Configuration for rate-limiting and retry behaviour.
/// Loaded from the "RateLimiting" section in appsettings.json.
/// </summary>
public sealed class RateLimitingSettings
{
    public const string SectionName = "RateLimiting";

    /// <summary>Maximum sustained requests per second.</summary>
    public int RequestsPerSecond { get; set; } = 5;

    /// <summary>Maximum number of retry attempts on transient / 429 failures.</summary>
    public int MaxRetryAttempts { get; set; } = 5;

    /// <summary>Median delay (in seconds) before the first retry. Subsequent retries grow exponentially with jitter.</summary>
    public int MedianFirstRetryDelaySeconds { get; set; } = 2;
}

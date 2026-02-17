using Microsoft.Extensions.Logging;

namespace PageCollator;

/// <summary>
/// Typed HttpClient service responsible for fetching individual pages from the API.
/// The underlying HttpClient has resilience policies (rate limiter + retry) applied
/// via the DI pipeline in Program.cs, so this class just makes simple GET calls.
/// </summary>
public sealed class ApiPageFetcher
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ApiPageFetcher> _logger;

    public string BaseUrl { get; set; } = string.Empty;

    public ApiPageFetcher(HttpClient httpClient, ILogger<ApiPageFetcher> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Fetches a single page and returns the raw JSON response body as a string.
    /// </summary>
    public async Task<string> FetchPageAsync(int page, CancellationToken cancellationToken = default)
    {
        var url = $"{BaseUrl.TrimEnd('/')}/page/{page}";

        _logger.LogDebug("Requesting page {Page}: {Url}", page, url);

        var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}

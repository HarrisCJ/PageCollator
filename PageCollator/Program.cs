using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PageCollator;
using Polly;

// ──────────────────────────────────────────────
// 1. Prompt user for runtime configuration
// ──────────────────────────────────────────────
Console.WriteLine("╔══════════════════════════════════════════════╗");
Console.WriteLine("║        Page Collator — Configuration         ║");
Console.WriteLine("╚══════════════════════════════════════════════╝");
Console.WriteLine();

Console.Write("API endpoint URL: ");
var baseUrl = Console.ReadLine()?.Trim() ?? string.Empty;
if (string.IsNullOrWhiteSpace(baseUrl))
{
    Console.Error.WriteLine("Error: API endpoint URL is required.");
    return;
}

Console.Write("Bearer token: ");
var bearerToken = Console.ReadLine()?.Trim() ?? string.Empty;
if (string.IsNullOrWhiteSpace(bearerToken))
{
    Console.Error.WriteLine("Error: Bearer token is required.");
    return;
}

Console.Write("Total pages to fetch [397]: ");
var pagesInput = Console.ReadLine()?.Trim();
int totalPages = string.IsNullOrWhiteSpace(pagesInput) ? 397 : int.Parse(pagesInput);

// Remove the PageQueryParam prompt entirely — page number is now a path segment

Console.Write("Output file path [output.json]: ");
var outputFilePath = Console.ReadLine()?.Trim();
if (string.IsNullOrWhiteSpace(outputFilePath)) outputFilePath = "output.json";

Console.WriteLine();

// ──────────────────────────────────────────────
// 2. Build the host (DI, config, logging, HTTP)
// ──────────────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);

// Bind rate-limiting settings from appsettings.json (sensible defaults)
builder.Services.Configure<RateLimitingSettings>(builder.Configuration.GetSection(RateLimitingSettings.SectionName));
var rlSettings = builder.Configuration.GetSection(RateLimitingSettings.SectionName).Get<RateLimitingSettings>() ?? new RateLimitingSettings();

// Register the typed HttpClient with resilience pipeline
builder.Services
    .AddHttpClient<ApiPageFetcher>(client =>
    {
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", bearerToken);
        client.Timeout = TimeSpan.FromMinutes(5);
    })
    .AddResilienceHandler("api-pipeline", (pipelineBuilder, context) =>
    {
        // ── Rate Limiter ──────────────────────────────────
        // Token-bucket: allows short bursts up to the limit,
        // then replenishes tokens at a steady rate.
        // This caps the OUTBOUND request rate so we never
        // hammer the server faster than it can handle.
        pipelineBuilder.AddRateLimiter(new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = rlSettings.RequestsPerSecond,            // max burst size
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),        // refill window
            TokensPerPeriod = rlSettings.RequestsPerSecond,       // tokens added per window
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 500                                       // queue depth for waiting requests
        }));

        // ── Retry with exponential backoff + jitter ───────
        // Handles 429 Too Many Requests, 5xx server errors,
        // and transient network exceptions. Honours the
        // Retry-After header when the server sends one.
        pipelineBuilder.AddRetry(new Polly.Retry.RetryStrategyOptions<HttpResponseMessage>
        {
            MaxRetryAttempts = rlSettings.MaxRetryAttempts,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromSeconds(rlSettings.MedianFirstRetryDelaySeconds),
            ShouldHandle = args => ValueTask.FromResult(
                args.Outcome.Result?.StatusCode is
                    HttpStatusCode.TooManyRequests or
                    HttpStatusCode.ServiceUnavailable or
                    HttpStatusCode.InternalServerError or
                    HttpStatusCode.BadGateway or
                    HttpStatusCode.GatewayTimeout or
                    HttpStatusCode.RequestTimeout
                || args.Outcome.Exception is HttpRequestException or TaskCanceledException),
            OnRetry = args =>
            {
                var logger = context.ServiceProvider.GetService<ILoggerFactory>()?
                    .CreateLogger("RetryPolicy");

                // Honour Retry-After header if present
                if (args.Outcome.Result?.Headers.RetryAfter is { } retryAfter)
                {
                    var delay = retryAfter.Delta ?? TimeSpan.FromSeconds(5);
                    logger?.LogWarning(
                        "Retry attempt {Attempt} — server asked to wait {Delay}s (Retry-After)",
                        args.AttemptNumber, delay.TotalSeconds);
                }
                else
                {
                    logger?.LogWarning(
                        "Retry attempt {Attempt} — waiting {Delay}s (status: {Status}, exception: {Exception})",
                        args.AttemptNumber,
                        args.RetryDelay.TotalSeconds,
                        args.Outcome.Result?.StatusCode,
                        args.Outcome.Exception?.Message);
                }

                return ValueTask.CompletedTask;
            }
        });
    });

using var host = builder.Build();

// ──────────────────────────────────────────────
// 3. Run the page-fetching and file-stitching
// ──────────────────────────────────────────────
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PageCollator");
var fetcher = host.Services.GetRequiredService<ApiPageFetcher>();

// Pass runtime values to the fetcher
fetcher.BaseUrl = baseUrl;

var fullOutputPath = Path.GetFullPath(outputFilePath);

logger.LogInformation("Starting collation of {TotalPages} pages from {BaseUrl}", totalPages, baseUrl);
logger.LogInformation("Output will be written to: {OutputFile}", fullOutputPath);
logger.LogInformation("Rate limit: ~{Rps} req/s  |  Max retries per request: {Retries}",
    rlSettings.RequestsPerSecond, rlSettings.MaxRetryAttempts);

var stopwatch = Stopwatch.StartNew();

// Stream-stitch: write each page's inner array content directly to disk.
// Only one page's raw string is in memory at a time (~15-40 MB).
await using (var fileStream = new FileStream(
    outputFilePath,
    FileMode.Create,
    FileAccess.Write,
    FileShare.None,
    bufferSize: 131_072,    // 128 KB write buffer
    useAsync: true))
await using (var writer = new StreamWriter(fileStream, System.Text.Encoding.UTF8, bufferSize: 131_072))
{
    await writer.WriteAsync('[');

    bool isFirstPage = true;

    for (int page = 1; page <= totalPages; page++)
    {
        string raw;
        try
        {
            raw = await fetcher.FetchPageAsync(page);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch page {Page} after all retries — aborting.", page);
            throw;
        }

        // Strip the outer [ and ] from the page's JSON array so we can
        // concatenate the inner elements into a single top-level array.
        var trimmed = raw.Trim();

        if (trimmed.Length < 2 || trimmed[0] != '[' || trimmed[^1] != ']')
        {
            // Not a JSON array — log a warning and write as-is (wrapped in the outer array).
            logger.LogWarning("Page {Page} response is not a JSON array — appending raw content.", page);

            if (!isFirstPage)
                await writer.WriteAsync(',');

            await writer.WriteAsync(raw);
            isFirstPage = false;
        }
        else
        {
            // Slice off the outer brackets
            var inner = trimmed[1..^1].Trim();

            // Skip empty pages (the array was [])
            if (inner.Length == 0)
            {
                logger.LogDebug("Page {Page} returned an empty array — skipping.", page);
                continue;
            }

            if (!isFirstPage)
                await writer.WriteAsync(',');

            // Write the inner content (the comma-separated elements)
            await writer.WriteAsync(inner);
            isFirstPage = false;
        }

        await writer.FlushAsync();

        // Progress logging every 10 pages and on the last page
        if (page % 10 == 0 || page == totalPages)
        {
            logger.LogInformation(
                "Progress: {Page}/{Total} pages fetched  |  Elapsed: {Elapsed}",
                page, totalPages, stopwatch.Elapsed.ToString(@"hh\:mm\:ss"));
        }
    }

    await writer.WriteAsync(']');
    await writer.FlushAsync();
}

stopwatch.Stop();

var fileInfo = new FileInfo(outputFilePath);

Console.WriteLine();
Console.WriteLine("════════════════════════════════════════════════");
Console.WriteLine($"  Done! {totalPages} pages collated successfully.");
Console.WriteLine($"  File size: {fileInfo.Length / (1024.0 * 1024.0):F2} MB");
Console.WriteLine($"  Elapsed:   {stopwatch.Elapsed:hh\\:mm\\:ss}");
Console.WriteLine($"  Output:    {fullOutputPath}");
Console.WriteLine("════════════════════════════════════════════════");

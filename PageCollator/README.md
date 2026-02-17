# PageCollator

`PageCollator` is a .NET console app that fetches a paginated API and stitches all pages into a **single JSON array** written to disk. Each page is streamed to the output file to reduce memory usage.

## Features

- Streams page results to disk (no full in-memory aggregation)
- Built-in rate limiting + retries with exponential backoff
- Interactive prompts at runtime for API URL, bearer token, page count, and output file path

## Requirements

- .NET SDK **10.0** (or newer)

## Quick Start

From the repo folder:

```bash
dotnet build
```

```bash
dotnet run --project PageCollator
```

You’ll be prompted for:

```
API endpoint URL: https://api.example.com/rest/of/url
Bearer token: <your token>
Total pages to fetch [397]:
Output file path [output.json]:
```

### URL format

The page number is appended as a **path segment**:

```
{BaseUrl}/page/1
{BaseUrl}/page/2
...
```

For example, if you enter:

```
https://api.example.com/rest/of/url
```

The first request will be:

```
https://api.example.com/rest/of/url/page/1
```

## Output

- A **single JSON array** is produced at your chosen output path.
- The app prints the **absolute output path** on completion.

Example summary:

```
Done! 397 pages collated successfully.
File size: 8432.17 MB
Elapsed:   00:12:34
Output:    /absolute/path/to/output.json
```

## Rate limiting & retries

Rate limiting is configured in `appsettings.json`:

```json
{
  "RateLimiting": {
    "RequestsPerSecond": 5,
    "MaxRetryAttempts": 5,
    "MedianFirstRetryDelaySeconds": 2
  }
}
```

- **RequestsPerSecond**: sustained request rate (token bucket)
- **MaxRetryAttempts**: retries on 429/5xx/transient failures
- **MedianFirstRetryDelaySeconds**: base delay for exponential backoff (with jitter)

## Publish (self-contained)

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish/win
```

```bash
dotnet publish -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -o ./publish/mac
```

```bash
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o ./publish/linux
```

**Important:** Keep `appsettings.json` alongside the published executable so rate limiting settings load correctly.

## Troubleshooting

- **Output file only contains `[`**: the app exited before the first page was written (e.g., invalid URL/token). Re-run with correct inputs.
- **Other users see your build path in exceptions**: run the executable with:
  ```bash
  PageCollator.exe --contentRoot .
  ```
  so `appsettings.json` is loaded relative to the executable.

---

If you want a different URL pattern or pagination scheme, open an issue or edit `ApiPageFetcher` in `PageCollator/ApiPageFetcher.cs`.

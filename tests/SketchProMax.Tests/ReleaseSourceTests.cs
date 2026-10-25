using SketchProMax.Update;

/// <summary>Where the updater reads the latest release: GitHub's API first, the github.com release page on any failure of it.</summary>
internal static class ReleaseSourceTests
{
    private sealed record R(string Version);

    public static void Run(Action<string, bool, string> check)
    {
        (R? Result, string? Why) Go(Func<CancellationToken, Task<ReleaseSource.Answer<R>>> api, double timeoutSeconds = 5, CancellationToken ct = default) =>
            ReleaseSource.ApiThenWebAsync(api, _ => Task.FromResult<R?>(new R("web")), TimeSpan.FromSeconds(timeoutSeconds), ct).GetAwaiter().GetResult();

        var ok = Go(_ => Task.FromResult(ReleaseSource.Found(new R("api"))));
        check("update: the API's answer is used when it comes", ok.Result?.Version == "api" && ok.Why is null, ok.Why ?? "");
        var none = Go(_ => Task.FromResult(ReleaseSource.Found<R>(null)));
        check("update: the API saying there is no release is believed", none.Result is null && none.Why is null, none.Why ?? "");
        var limited = Go(_ => Task.FromResult(ReleaseSource.Fallback<R>("GitHub's API answered 403")));
        check("update: an error status falls back to the web page", limited.Result?.Version == "web" && limited.Why!.Contains("403"), limited.Why ?? "");
        var refused = Go(_ => throw new HttpRequestException("A connection attempt failed because the connected party did not properly respond"));
        check("update: a connection error falls back to the web page", refused.Result?.Version == "web" && refused.Why!.Contains("could not be reached"), refused.Why ?? "");
        var slow = Go(async t => { await Task.Delay(TimeSpan.FromSeconds(30), t); return ReleaseSource.Found(new R("api")); }, timeoutSeconds: 0.2);
        check("update: an API that doesn't answer in time falls back to the web page", slow.Result?.Version == "web" && slow.Why!.Contains("within"), slow.Why ?? "");
        var garbled = Go(_ => throw new System.Text.Json.JsonException("bad"));
        check("update: an unreadable API answer falls back to the web page", garbled.Result?.Version == "web", garbled.Why ?? "");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var threw = false;
            try { Go(t => { t.ThrowIfCancellationRequested(); return Task.FromResult(ReleaseSource.Found(new R("api"))); }, ct: cancelled.Token); }
            catch (OperationCanceledException) { threw = true; }
            check("update: the caller's own cancel is not swallowed", threw, "");
        }

        check("update: tag from the releases/latest redirect", ReleaseSource.TagFromRedirect("https://github.com/o/r/releases/tag/v0.33.2") == "v0.33.2"
            && ReleaseSource.TagFromRedirect("https://github.com/o/r/releases/tag/v1.0.0?x=1") == "v1.0.0"
            && ReleaseSource.TagFromRedirect("https://github.com/o/r/releases") is null && ReleaseSource.TagFromRedirect(null) is null, "");
        check("update: the package's github.com address", ReleaseSource.DownloadUrl("o/r", "v0.33.2", "0.33.2") == "https://github.com/o/r/releases/download/v0.33.2/SketchProMax-0.33.2.zip",
            ReleaseSource.DownloadUrl("o/r", "v0.33.2", "0.33.2"));
    }
}

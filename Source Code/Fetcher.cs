using System.Diagnostics;
using System.Net;
using FrooxEngine;
using FrooxEngine.Store;
using SkyFrost.Base;

namespace LocalInventoryExport;

internal enum FetchStatus
{
    Ok,
    Cached,
    Missing,
    RateLimited,
    ServerError,
    NoResponse,
    Aborted,
    DiskError
}

internal readonly record struct FetchResult(FetchStatus Status, string? Path, long Bytes, int Code, string Detail, double RetryAfterSeconds);

internal static class Throttle
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static long _lastStart = long.MinValue;

    internal static async Task BeforeRequest(CancellationToken cancel)
    {
        if (!LocalInventoryExportMod.UseDelay)
        {
            Interlocked.Exchange(ref _lastStart, Clock.ElapsedMilliseconds);
            return;
        }
        while (true)
        {
            long due = _lastStart == long.MinValue ? 0 : _lastStart + LocalInventoryExportMod.DelayMs;
            long wait = due - Clock.ElapsedMilliseconds;
            if (wait <= 0)
                break;

            await Task.Delay((int)Math.Min(wait, 250), cancel).ConfigureAwait(false);
        }
        Interlocked.Exchange(ref _lastStart, Clock.ElapsedMilliseconds);
    }

    internal static long BytesPerSecond => LocalInventoryExportMod.UseBandwidth ? LocalInventoryExportMod.BandwidthKb * 1024L : 0L;
}

internal sealed class PauseAbortException : Exception
{
}

internal sealed class DiskWriteException(string message, Exception inner) : IOException(message, inner)
{
}

internal static class Fetcher
{
    private const int BufferSize = 32768;
    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(45);

    internal static async Task<bool> IsCached(Engine engine, Uri url)
    {
        AssetRecord? record = await engine.LocalDB.TryFetchAssetRecordAsync(url).ConfigureAwait(false);
        return HasFile(record);
    }

    private static bool HasFile(AssetRecord? record)
    {
        if (record is null || string.IsNullOrEmpty(record.path))
            return false;

        try
        {
            return File.Exists(record.path) && new FileInfo(record.path).Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static async Task<FetchResult> Fetch(Engine engine, Uri url, CancellationToken cancel)
    {
        AssetRecord? cached = await engine.LocalDB.TryFetchAssetRecordAsync(url).ConfigureAwait(false);
        if (HasFile(cached))
            return new FetchResult(FetchStatus.Cached, cached!.path, 0, 200, "", 0);

        await Throttle.BeforeRequest(cancel).ConfigureAwait(false);
        if (Exporter.PauseRequested)
            return new FetchResult(FetchStatus.Aborted, null, 0, 0, "Paused", 0);

        EngineSkyFrostInterface cloud = engine.Cloud;
        Uri address = ((SkyFrostInterface)cloud).Assets.DBToHttp(url, DB_Endpoint.Default);
        string temp = engine.LocalDB.GetTempFilePath();
        Exporter.CountRequest();
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(HeaderTimeout);
            using HttpRequestMessage request = ((SkyFrostInterface)cloud).Api.CreateRequest(address, false, HttpMethod.Get);
            using HttpResponseMessage response = await ((SkyFrostInterface)cloud).Api.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            int code = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                double retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 0;
                FetchStatus status = response.StatusCode switch
                {
                    HttpStatusCode.NotFound => FetchStatus.Missing,
                    HttpStatusCode.Forbidden => FetchStatus.Missing,
                    HttpStatusCode.Gone => FetchStatus.Missing,
                    HttpStatusCode.TooManyRequests => FetchStatus.RateLimited,
                    _ => FetchStatus.ServerError
                };
                return new FetchResult(status, null, 0, code, response.StatusCode.ToString(), retryAfter);
            }
            long total = response.Content.Headers.ContentLength ?? -1;
            long written = 0;
            long limit = Throttle.BytesPerSecond;
            Stopwatch speed = Stopwatch.StartNew();
            using (System.IO.Stream network = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            using (FileStream file = OpenTemp(temp))
            {
                byte[] buffer = new byte[BufferSize];
                while (true)
                {
                    if (Exporter.PauseRequested)
                        throw new PauseAbortException();

                    timeout.CancelAfter(StallTimeout);
                    int read = await network.ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token).ConfigureAwait(false);
                    if (read <= 0)
                        break;

                    await WriteTemp(file, buffer, read, cancel).ConfigureAwait(false);
                    written += read;
                    Exporter.CountBytes(read);
                    if (limit > 0)
                    {
                        double wait = written / (double)limit - speed.Elapsed.TotalSeconds;
                        if (wait > 0.01)
                            await Task.Delay(TimeSpan.FromSeconds(Math.Min(wait, 5)), cancel).ConfigureAwait(false);
                    }
                }
                await FlushTemp(file, cancel).ConfigureAwait(false);
            }
            if (total >= 0 && written != total)
            {
                TryDelete(temp);
                return new FetchResult(FetchStatus.NoResponse, null, written, code, $"Size mismatch, got {written} of {total} bytes", 0);
            }
            if (written == 0)
            {
                TryDelete(temp);
                return new FetchResult(FetchStatus.NoResponse, null, 0, code, "Empty response", 0);
            }
            string stored;
            try
            {
                stored = await engine.LocalDB.StoreCacheRecordAsync(url, temp).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new DiskWriteException(ex.Message, ex);
            }
            Exporter.CountAsset();
            return new FetchResult(FetchStatus.Ok, stored, written, code, "", 0);
        }
        catch (DiskWriteException ex)
        {
            TryDelete(temp);
            return new FetchResult(FetchStatus.DiskError, null, 0, 0, ex.Message, 0);
        }
        catch (PauseAbortException)
        {
            TryDelete(temp);
            return new FetchResult(FetchStatus.Aborted, null, 0, 0, "Paused", 0);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            TryDelete(temp);
            return new FetchResult(FetchStatus.Aborted, null, 0, 0, "Stopped", 0);
        }
        catch (OperationCanceledException)
        {
            TryDelete(temp);
            return new FetchResult(FetchStatus.NoResponse, null, 0, 0, "The cloud did not answer in time", 0);
        }
        catch (HttpRequestException ex)
        {
            TryDelete(temp);
            return new FetchResult(FetchStatus.NoResponse, null, 0, 0, ex.Message, 0);
        }
        catch (IOException ex)
        {
            TryDelete(temp);
            return new FetchResult(FetchStatus.NoResponse, null, 0, 0, ex.Message, 0);
        }
    }

    private static FileStream OpenTemp(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DiskWriteException(ex.Message, ex);
        }
    }

    private static async Task WriteTemp(FileStream file, byte[] buffer, int count, CancellationToken cancel)
    {
        try
        {
            await file.WriteAsync(buffer.AsMemory(0, count), cancel).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new DiskWriteException(ex.Message, ex);
        }
    }

    private static async Task FlushTemp(FileStream file, CancellationToken cancel)
    {
        try
        {
            await file.FlushAsync(cancel).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new DiskWriteException(ex.Message, ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
        }
    }
}

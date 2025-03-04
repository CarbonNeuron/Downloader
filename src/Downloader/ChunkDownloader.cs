﻿using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Downloader
{
    internal class ChunkDownloader
    {
        private const int TimeoutIncrement = 10;
        private ThrottledStream sourceStream;
        public event EventHandler<DownloadProgressChangedEventArgs> DownloadProgressChanged;
        public DownloadConfiguration Configuration { get; protected set; }
        public Chunk Chunk { get; protected set; }

        public ChunkDownloader(Chunk chunk, DownloadConfiguration config)
        {
            Chunk = chunk;
            Configuration = config;
            Configuration.PropertyChanged += ConfigurationPropertyChanged;
        }

        private void ConfigurationPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Configuration.MaximumBytesPerSecond) &&
                sourceStream?.CanRead == true)
            {
                sourceStream.BandwidthLimit = Configuration.MaximumSpeedPerChunk;
            }
        }

        public async Task<Chunk> Download(Request downloadRequest,
            PauseToken pause, CancellationToken cancellationToken)
        {
            try
            {
                Chunk.Timeout += TimeoutIncrement; // increase reader timeout
                await DownloadChunk(downloadRequest, pause, cancellationToken).ConfigureAwait(false);
                return Chunk;
            }
            catch (TaskCanceledException) // when stream reader timeout occurred 
            {
                // re-request and continue downloading...
                return await Download(downloadRequest, pause, cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException) // when stream reader cancel/timeout occurred 
            {
                // re-request and continue downloading...
                return await Download(downloadRequest, pause, cancellationToken).ConfigureAwait(false);
            }
            catch (WebException) when (Chunk.CanTryAgainOnFailover())
            {
                // when the host forcibly closed the connection.
                await Task.Delay(Chunk.Timeout, cancellationToken).ConfigureAwait(false);
                // re-request and continue downloading...
                return await Download(downloadRequest, pause, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (Chunk.CanTryAgainOnFailover() &&
                                          (error.HasSource("System.Net.Http") ||
                                           error.HasSource("System.Net.Sockets") ||
                                           error.HasSource("System.Net.Security") ||
                                           error.InnerException is SocketException))
            {
                await Task.Delay(Chunk.Timeout, cancellationToken).ConfigureAwait(false);
                // re-request and continue downloading...
                return await Download(downloadRequest, pause, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await Task.Yield();
            }
        }

        private async Task DownloadChunk(Request downloadRequest, PauseToken pauseToken, CancellationToken cancelToken)
        {
            cancelToken.ThrowIfCancellationRequested();
            if (Chunk.IsDownloadCompleted() == false)
            {
                HttpWebRequest request = downloadRequest.GetRequest();
                SetRequestRange(request);
                using HttpWebResponse downloadResponse = request.GetResponse() as HttpWebResponse;
                if (downloadResponse.StatusCode == HttpStatusCode.OK ||
                    downloadResponse.StatusCode == HttpStatusCode.PartialContent ||
                    downloadResponse.StatusCode == HttpStatusCode.Created ||
                    downloadResponse.StatusCode == HttpStatusCode.Accepted ||
                    downloadResponse.StatusCode == HttpStatusCode.ResetContent)
                {
                    Configuration.RequestConfiguration.CookieContainer = request.CookieContainer;
                    using Stream responseStream = downloadResponse?.GetResponseStream();
                    if (responseStream != null)
                    {
                        using (sourceStream = new ThrottledStream(responseStream, Configuration.MaximumSpeedPerChunk))
                        {
                            await ReadStream(sourceStream, pauseToken, cancelToken).ConfigureAwait(false);
                        }
                    }
                }
                else
                {
                    throw new WebException($"Download response status was {downloadResponse.StatusCode}: {downloadResponse.StatusDescription}");
                }
            }
        }

        private void SetRequestRange(HttpWebRequest request)
        {
            // has limited range
            if (Chunk.End > 0 &&
                (Configuration.ChunkCount > 1 || Chunk.Position > 0 || Configuration.RangeDownload))
            {
                request.AddRange(Chunk.Start + Chunk.Position, Chunk.End);
            }
        }

        internal async Task ReadStream(Stream stream, PauseToken pauseToken, CancellationToken cancelToken)
        {
            int readSize = 1;
            CancellationToken? innerToken = null;
            try
            {
                // close stream on cancellation because, it's not work on .Net Framework
                using (cancelToken.Register(stream.Close))
                {
                    while (CanReadStream() && readSize > 0)
                    {
                        cancelToken.ThrowIfCancellationRequested();
                        await pauseToken.WaitWhilePausedAsync().ConfigureAwait(false);
                        byte[] buffer = new byte[Configuration.BufferBlockSize];
                        using var innerCts = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
                        innerCts.CancelAfter(Chunk.Timeout);
                        innerToken = innerCts.Token;
                        using (innerToken?.Register(stream.Close))
                        {
                            // if innerToken timeout occurs, close the stream just during the reading stream
                            readSize = await stream.ReadAsync(buffer, 0, buffer.Length, innerToken.Value).ConfigureAwait(false);
                        }
                        await Chunk.Storage.WriteAsync(buffer, 0, readSize, cancelToken).ConfigureAwait(false);
                        Chunk.Position += readSize;

                        OnDownloadProgressChanged(new DownloadProgressChangedEventArgs(Chunk.Id) {
                            TotalBytesToReceive = Chunk.Length,
                            ReceivedBytesSize = Chunk.Position,
                            ProgressedByteSize = readSize,
                            ReceivedBytes = buffer.Take(readSize).ToArray()
                        });
                    }
                }
            }
            catch (ObjectDisposedException exp) // When closing stream manually, ObjectDisposedException will be thrown
            {
                cancelToken.ThrowIfCancellationRequested();
                if (innerToken?.IsCancellationRequested == true)
                    throw new TaskCanceledException("The ReadAsync function has timed out", exp);

                throw; // throw origin stack trace of exception 
            }
        }

        private bool CanReadStream()
        {
            return Chunk.Length == 0 ||
                   Chunk.Length - Chunk.Position > 0;
        }

        private void OnDownloadProgressChanged(DownloadProgressChangedEventArgs e)
        {
            DownloadProgressChanged?.Invoke(this, e);
        }
    }
}
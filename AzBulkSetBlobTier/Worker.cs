using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AzBulkSetBlobTier
{
    public class Worker : BackgroundService
    {
        private const string DELIMITER = "/";
        private readonly ILogger<Worker> _logger;
        private readonly IHostApplicationLifetime _hostApplicationLifetime;
        private TelemetryClient _telemetryClient;
        private Config _config;
        private readonly ConcurrentBag<Task> _todo;
        private readonly SemaphoreSlim _slim;
        private long _blobCount;
        private long _blobBytes;
        private long _blobHotCount;
        private long _blobHotBytes;
        private long _blobCoolCount;
        private long _blobCoolBytes;
        private long _blobArchiveCount;
        private long _blobArchiveBytes;
        private long _blobArchiveToHotCount;
        private long _blobArchiveToHotBytes;
        private long _blobArchiveToCoolCount;
        private long _blobArchiveToCoolBytes;



        public Worker(ILogger<Worker> logger,
            IHostApplicationLifetime hostApplicationLifetime,
            TelemetryClient telemetryClient,
            Config config)
        {
            _logger = logger;
            _hostApplicationLifetime = hostApplicationLifetime;
            _telemetryClient = telemetryClient;
            _config = config;
            _todo = new ConcurrentBag<Task>();

            using (var op = _telemetryClient.StartOperation<DependencyTelemetry>("Setup"))
            {

                _logger.LogInformation($"Run = {_config.Run}");
                op.Telemetry.Properties.Add("Run", _config.Run);

                //Set the starting point to the root if not provided
                if (string.IsNullOrEmpty(_config.Prefix))
                {
                    _config.Prefix = string.Empty;
                }
                //If starting point is provided, ensure that it has the slash at the end
                else if (!_config.Prefix.EndsWith(DELIMITER))
                {
                    _config.Prefix = _config.Prefix + DELIMITER;
                }
                _logger.LogInformation($"Prefix = {_config.Prefix}");
                op.Telemetry.Properties.Add("Prefix", _config.Prefix);

                //Set the default thread count if one was not set
                if (_config.ThreadCount < 1)
                {
                    _config.ThreadCount = Environment.ProcessorCount * 8;
                }
                _logger.LogInformation($"ThreadCount = {_config.ThreadCount}");
                op.Telemetry.Properties.Add("ThreadCount", _config.ThreadCount.ToString());

                //The Semaphore ensures how many scans can happen at the same time
                _slim = new SemaphoreSlim(_config.ThreadCount);

                _logger.LogInformation($"WhatIf = {_config.WhatIf}");
                op.Telemetry.Properties.Add("WhatIf", _config.WhatIf.ToString());

                _logger.LogInformation($"TargetAccessTier = {_config.TargetAccessTier}");
                op.Telemetry.Properties.Add("TargetAccessTier", _config.TargetAccessTier);
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            stoppingToken.Register(() =>
            {
                _logger.LogInformation("Worker Cancelling");
            });

            try
            {
                await DoWork(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Operation Canceled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled Exception");
            }
            finally
            {
                _logger.LogInformation("Flushing App Insights");
                _telemetryClient.Flush();
                Task.Delay(5000).Wait();

                _hostApplicationLifetime.StopApplication();
            }

        }

        private async Task DoWork(CancellationToken stoppingToken)
        {
            using (var op = _telemetryClient.StartOperation<DependencyTelemetry>("Do Work"))
            {
                op.Telemetry.Properties.Add("Run", _config.Run);

                ProcessFolder(_config.Prefix, stoppingToken);

                //wait for enough to get the todo list so we don't exit before we started
                await Task.Delay(1000);

                // wait while there are any tasks that have not finished
                while (_todo.Any(x => !x.IsCompleted))
                {
                    LogStatus();
                    await Task.Delay(1000);
                }

                _logger.LogInformation("Done!");
                LogStatus();

                op.Telemetry.Metrics.Add("Blobs", _blobCount);
                op.Telemetry.Metrics.Add("Bytes", _blobBytes);
                op.Telemetry.Metrics.Add("Hot Blobs", _blobHotCount);
                op.Telemetry.Metrics.Add("Hot Bytes", _blobHotBytes);
                op.Telemetry.Metrics.Add("Cool Blobs", _blobCoolCount);
                op.Telemetry.Metrics.Add("Cool Bytes", _blobCoolBytes);
                op.Telemetry.Metrics.Add("Archive Blobs", _blobArchiveCount);
                op.Telemetry.Metrics.Add("Archive Bytes", _blobArchiveBytes);
                op.Telemetry.Metrics.Add("Archive To Hot Blobs", _blobArchiveToHotCount);
                op.Telemetry.Metrics.Add("Archive To Hot Bytes", _blobArchiveToHotBytes);
                op.Telemetry.Metrics.Add("Archive To Cool Blobs", _blobArchiveToCoolCount);
                op.Telemetry.Metrics.Add("Archive To Cool Bytes", _blobArchiveToCoolBytes);
            }
        }

        private void LogStatus()
        {
            _logger.LogInformation($"Blobs: {_blobCount:N0} in {BytesToTiB(_blobBytes):N2} TiB");
            _logger.LogInformation($"Hot Blobs: {_blobHotCount:N0} in {BytesToTiB(_blobHotBytes):N2} TiB");
            _logger.LogInformation($"Cool Blobs: {_blobCoolCount:N0} in {BytesToTiB(_blobCoolBytes):N2} TiB");
            _logger.LogInformation($"Archive Blobs: {_blobArchiveCount:N0} in {BytesToTiB(_blobArchiveBytes):N2} TiB");
            _logger.LogInformation($"Archive To Hot Blobs: {_blobArchiveToHotCount:N0} in {BytesToTiB(_blobArchiveToHotBytes):N2} TiB");
            _logger.LogInformation($"Archive To Cool Blobs: {_blobArchiveToCoolCount:N0} in {BytesToTiB(_blobArchiveToCoolBytes):N2} TiB");
            _logger.LogInformation($"Archive To Move Blobs: {_blobArchiveCount - _blobArchiveToHotCount - _blobArchiveToCoolCount:N0} in {BytesToTiB(_blobArchiveBytes - _blobArchiveToHotBytes - _blobArchiveToCoolBytes):N2} TiB");
        }

        private double BytesToTiB(long bytes)
        {
            return bytes / Math.Pow(2, 40);
        }

        private void ProcessFolder(string prefix, CancellationToken stoppingToken)
        {
            _logger.LogInformation($"Processing Folder {prefix}");

            //Create a new task to process the folder
            _todo.Add(Task.Run(async () =>
            {
                await _slim.WaitAsync(stoppingToken);

                using (var op = _telemetryClient.StartOperation<DependencyTelemetry>("ProcessFolder"))
                {
                    op.Telemetry.Properties.Add("Run", _config.Run);
                    op.Telemetry.Properties.Add("Prefix", prefix);

                    //Get a client to connect to the blob container
                    var blobServiceClient = new BlobServiceClient(_config.StorageConnectionString);
                    var blobContainerClient = blobServiceClient.GetBlobContainerClient(_config.Container);
                    var uris = new Stack<Uri>();

                    await foreach (var item in blobContainerClient.GetBlobsByHierarchyAsync(prefix: prefix, delimiter: DELIMITER, cancellationToken: stoppingToken))
                    {
                        //I found another folder, recurse
                        if (item.IsPrefix)
                        {
                            ProcessFolder(item.Prefix, stoppingToken);
                        }
                        //I found a file, write it out to the json file
                        else if (item.IsBlob)
                        {
                            //increment the counters
                            Interlocked.Add(ref _blobCount, 1);
                            Interlocked.Add(ref _blobBytes, item.Blob.Properties.ContentLength.GetValueOrDefault());

                            if (AccessTier.Hot.Equals(item.Blob.Properties.AccessTier))
                            {
                                Interlocked.Add(ref _blobHotCount, 1);
                                Interlocked.Add(ref _blobHotBytes, item.Blob.Properties.ContentLength.GetValueOrDefault());
                            }
                            else if (AccessTier.Cool.Equals(item.Blob.Properties.AccessTier))
                            {
                                Interlocked.Add(ref _blobCoolCount, 1);
                                Interlocked.Add(ref _blobCoolBytes, item.Blob.Properties.ContentLength.GetValueOrDefault());
                            }
                            else if (AccessTier.Archive.Equals(item.Blob.Properties.AccessTier))
                            {
                                Interlocked.Add(ref _blobArchiveCount, 1);
                                Interlocked.Add(ref _blobArchiveBytes, item.Blob.Properties.ContentLength.GetValueOrDefault());

                                if (item.Blob.Properties.ArchiveStatus.HasValue)
                                {
                                    if (item.Blob.Properties.ArchiveStatus.Value == ArchiveStatus.RehydratePendingToHot)
                                    {
                                        Interlocked.Add(ref _blobArchiveToHotCount, 1);
                                        Interlocked.Add(ref _blobArchiveToHotBytes, item.Blob.Properties.ContentLength.GetValueOrDefault());
                                    }
                                    else if (item.Blob.Properties.ArchiveStatus.Value == ArchiveStatus.RehydratePendingToCool)
                                    {
                                        Interlocked.Add(ref _blobArchiveToCoolCount, 1);
                                        Interlocked.Add(ref _blobArchiveToCoolBytes, item.Blob.Properties.ContentLength.GetValueOrDefault());
                                    }
                                }
                                else
                                {
                                    uris.Push(blobContainerClient.GetBlobClient(item.Blob.Name).Uri);
                                }
                            }
                        }

                        if (uris.Count > 250)
                        {
                            await ProcessBatch(blobServiceClient, uris.ToArray(), stoppingToken);
                            uris.Clear();
                        }
                    }

                    if (uris.Count > 0)
                    {
                        await ProcessBatch(blobServiceClient, uris.ToArray(), stoppingToken);
                        uris.Clear();
                    }
                }

                _slim.Release();

            }));

        }

        private async Task ProcessBatch(BlobServiceClient blobServiceClient, IEnumerable<Uri> uris, CancellationToken cancellationToken)
        {
            _logger.LogInformation($"Sending Batch of {uris.Count()} items");

            using (var op = _telemetryClient.StartOperation<DependencyTelemetry>("ProcessBatch"))
            {
                op.Telemetry.Properties.Add("Run", _config.Run);
                op.Telemetry.Metrics.Add("BatchSize", uris.Count());

                if (!_config.WhatIf)
                {
                    BlobBatchClient batch = blobServiceClient.GetBlobBatchClient();
                    if (_config.TargetAccessTier.Equals("Hot", StringComparison.InvariantCultureIgnoreCase))
                    {
                        await batch.SetBlobsAccessTierAsync(uris, AccessTier.Hot, cancellationToken: cancellationToken);
                    }
                    else
                    {
                        await batch.SetBlobsAccessTierAsync(uris, AccessTier.Cool, cancellationToken: cancellationToken);
                    }
                }
            }
        }
    }
}

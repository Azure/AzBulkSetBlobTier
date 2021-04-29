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
        private AccessTier _targetAccessTier;
        private AccessTier _sourceAccessTier;
        private bool _configValid = true;



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

                //Default the delimiter to a slash if not provided
                if (string.IsNullOrEmpty(_config.Delimiter))
                {
                    _config.Delimiter = "/";
                }
                _logger.LogInformation($"Delimiter = {_config.Delimiter}");
                op.Telemetry.Properties.Add("Delimiter", _config.Delimiter);

                //Set the starting point to the root if not provided
                if (string.IsNullOrEmpty(_config.Prefix))
                {
                    _config.Prefix = string.Empty;
                }
                //If starting point is provided, ensure that it has the delimiter at the end
                else if (!_config.Prefix.EndsWith(_config.Delimiter))
                {
                    _config.Prefix = _config.Prefix + _config.Delimiter;
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

                if (_config.TargetAccessTier.Equals("Hot", StringComparison.InvariantCultureIgnoreCase))
                {
                    _targetAccessTier = AccessTier.Hot;
                }
                else if (_config.TargetAccessTier.Equals("Cool", StringComparison.InvariantCultureIgnoreCase))
                {
                    _targetAccessTier = AccessTier.Cool;
                }
                else if (_config.TargetAccessTier.Equals("Archive", StringComparison.InvariantCultureIgnoreCase))
                {
                    _targetAccessTier = AccessTier.Cool;
                }
                else
                {
                    _logger.LogError($"Invalid Target Access Tier of {_config.TargetAccessTier} must be either Hot, Cool or Archive.");
                    _configValid = false;
                }

                _logger.LogInformation($"SourceAccessTier = {_config.SourceAccessTier}");
                op.Telemetry.Properties.Add("SourceAccessTier", _config.SourceAccessTier);

                if (_config.SourceAccessTier.Equals("Hot", StringComparison.InvariantCultureIgnoreCase))
                {
                    _sourceAccessTier = AccessTier.Hot;
                }
                else if (_config.SourceAccessTier.Equals("Cool", StringComparison.InvariantCultureIgnoreCase))
                {
                    _sourceAccessTier = AccessTier.Cool;
                }
                else if (_config.SourceAccessTier.Equals("Archive", StringComparison.InvariantCultureIgnoreCase))
                {
                    _sourceAccessTier = AccessTier.Cool;
                }
                else
                {
                    _logger.LogError($"Invalid Source Access Tier of {_config.SourceAccessTier} must be either Hot, Cool or Archive.");
                    _configValid = false;
                }

                if (_sourceAccessTier.Equals(_targetAccessTier))
                {
                    _logger.LogError($"Invalid Source/Target Access Tier they cannot be the same.");
                    _configValid = false;
                }

                if (string.IsNullOrEmpty(config.Container))
                {
                    _logger.LogError($"No Storage Container Name Provided.");
                    _configValid = false;
                }

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
                if (_configValid)
                {
                    await DoWork(stoppingToken);
                }
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
                    await Task.Delay(10000);
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

        /// <summary>
        /// Log information to the default logger
        /// </summary>
        private void LogStatus()
        {
            _logger.LogInformation($"Blobs: {_blobCount:N0} in {BytesToTiB(_blobBytes):N2} TiB");
            _logger.LogInformation($"Hot Blobs: {_blobHotCount:N0} in {BytesToTiB(_blobHotBytes):N2} TiB");
            _logger.LogInformation($"Cool Blobs: {_blobCoolCount:N0} in {BytesToTiB(_blobCoolBytes):N2} TiB");
            _logger.LogInformation($"Archive Blobs: {_blobArchiveCount:N0} in {BytesToTiB(_blobArchiveBytes):N2} TiB");
            _logger.LogInformation($"Archive To Hot Blobs: {_blobArchiveToHotCount:N0} in {BytesToTiB(_blobArchiveToHotBytes):N2} TiB");
            _logger.LogInformation($"Archive To Cool Blobs: {_blobArchiveToCoolCount:N0} in {BytesToTiB(_blobArchiveToCoolBytes):N2} TiB");
        }

        /// <summary>
        /// convert bytes to TiBs
        /// </summary>
        /// <param name="bytes"></param>
        /// <returns></returns>
        private double BytesToTiB(long bytes)
        {
            return bytes / Math.Pow(2, 40);
        }

        /// <summary>
        /// Process a Folder/Prefix of objects from your storage account
        /// </summary>
        /// <param name="prefix"></param>
        /// <param name="stoppingToken"></param>
        private void ProcessFolder(string prefix, CancellationToken stoppingToken)
        {
            _logger.LogInformation($"Processing Prefix {prefix}");

            //Create a new task to process the folder
            _todo.Add(Task.Run(async () =>
            {
                await _slim.WaitAsync(stoppingToken);

                using (var op = _telemetryClient.StartOperation<DependencyTelemetry>("ProcessPrefix"))
                {
                    op.Telemetry.Properties.Add("Run", _config.Run);
                    op.Telemetry.Properties.Add("Prefix", prefix);

                    //Get a client to connect to the blob container
                    var blobServiceClient = new BlobServiceClient(_config.StorageConnectionString);
                    var blobContainerClient = blobServiceClient.GetBlobContainerClient(_config.Container);
                    var uris = new Stack<Uri>();

                    await foreach (var item in blobContainerClient.GetBlobsByHierarchyAsync(prefix: prefix, delimiter: _config.Delimiter, cancellationToken: stoppingToken))
                    {
                        //I found another folder, recurse
                        if (item.IsPrefix)
                        {
                            ProcessFolder(item.Prefix, stoppingToken);
                        }
                        //I found a block blob, do I need to move it?
                        else if (item.IsBlob && BlobType.Block.Equals(item.Blob.Properties.BlobType))
                        {
                            InterlockedAdd(ref _blobCount, ref _blobBytes, item);

                            //Hot Blob
                            if (AccessTier.Hot.Equals(item.Blob.Properties.AccessTier))
                            {
                                InterlockedAdd(ref _blobHotCount, ref _blobHotBytes, item);

                                if (_sourceAccessTier.Equals(AccessTier.Hot))
                                {
                                    uris.Push(blobContainerClient.GetBlobClient(item.Blob.Name).Uri);
                                }
                            }

                            //Cool Blob
                            else if (AccessTier.Cool.Equals(item.Blob.Properties.AccessTier))
                            {
                                InterlockedAdd(ref _blobCoolCount, ref _blobCoolBytes, item);

                                if (_sourceAccessTier.Equals(AccessTier.Cool))
                                {
                                    uris.Push(blobContainerClient.GetBlobClient(item.Blob.Name).Uri);
                                }
                            }

                            //Archive Blob
                            else if (AccessTier.Archive.Equals(item.Blob.Properties.AccessTier))
                            {
                                InterlockedAdd(ref _blobArchiveCount, ref _blobArchiveBytes, item);

                                if (item.Blob.Properties.ArchiveStatus.HasValue)
                                {
                                    if (item.Blob.Properties.ArchiveStatus.Value == ArchiveStatus.RehydratePendingToHot)
                                    {
                                        InterlockedAdd(ref _blobArchiveToHotCount, ref _blobArchiveToHotBytes, item);
                                    }
                                    else if (item.Blob.Properties.ArchiveStatus.Value == ArchiveStatus.RehydratePendingToCool)
                                    {
                                        InterlockedAdd(ref _blobArchiveToCoolCount, ref _blobArchiveToCoolBytes, item);
                                    }
                                }
                                else
                                {
                                    // Only move Archive blobs if they are NOT already pending a move
                                    if (_sourceAccessTier.Equals(AccessTier.Archive))
                                    {
                                        uris.Push(blobContainerClient.GetBlobClient(item.Blob.Name).Uri);
                                    }
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

        /// <summary>
        /// increment the counter
        /// </summary>
        /// <param name="count">blob count counter</param>
        /// <param name="bytes">blob bytes counter</param>
        /// <param name="bhi">blob hierarchy item</param>
        private void InterlockedAdd(ref long count, ref long bytes, BlobHierarchyItem bhi)
        {
            Interlocked.Add(ref count, 1);
            Interlocked.Add(ref bytes, bhi.Blob.Properties.ContentLength.GetValueOrDefault());
        }

        /// <summary>
        /// Call the batch API with a bunch of URLs to change the tier of
        /// </summary>
        /// <param name="blobServiceClient">connection to blob service</param>
        /// <param name="uris">urls to move</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
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
                    await batch.SetBlobsAccessTierAsync(uris, _targetAccessTier, cancellationToken: cancellationToken);
                }
            }
        }
    }
}

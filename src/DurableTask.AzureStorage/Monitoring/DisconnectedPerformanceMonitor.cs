﻿//  ----------------------------------------------------------------------------------
//  Copyright Microsoft Corporation
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//  http://www.apache.org/licenses/LICENSE-2.0
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
//  ----------------------------------------------------------------------------------

namespace DurableTask.AzureStorage.Monitoring
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using DurableTask.AzureStorage.Storage;
    using Microsoft.WindowsAzure.Storage;

    /// <summary>
    /// Utility class for collecting performance information for a Durable Task hub without actually running inside a Durable Task worker.
    /// </summary>
    public class DisconnectedPerformanceMonitor
    {
        internal const int QueueLengthSampleSize = 5;
        internal const int MaxMessagesPerWorkerRatio = 100;

        static readonly int LowLatencyThreshold = 200; // milliseconds
        static readonly Random Random = new Random();

        readonly List<QueueMetricHistory> controlQueueLatencies = new List<QueueMetricHistory>();
        readonly QueueMetricHistory workItemQueueLatencies = new QueueMetricHistory(QueueLengthSampleSize);

        readonly AzureStorageOrchestrationServiceSettings settings;
        private readonly AzureStorageClient azureStorageClient;
        readonly int maxPollingLatency;
        readonly int highLatencyThreshold;

        int currentPartitionCount;
        int currentWorkItemQueueLength;
        int[] currentControlQueueLengths;

        /// <summary>
        /// Initializes a new instance of the <see cref="DisconnectedPerformanceMonitor"/> class.
        /// </summary>
        /// <param name="storageConnectionString">The connection string for the Azure Storage account to monitor.</param>
        /// <param name="taskHub">The name of the task hub within the specified storage account.</param>
        public DisconnectedPerformanceMonitor(string storageConnectionString, string taskHub)
            : this(CloudStorageAccount.Parse(storageConnectionString), taskHub)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DisconnectedPerformanceMonitor"/> class.
        /// </summary>
        /// <param name="storageAccount">The Azure Storage account to monitor.</param>
        /// <param name="taskHub">The name of the task hub within the specified storage account.</param>
        /// <param name="maxPollingIntervalMilliseconds">The maximum interval in milliseconds for polling control and work-item queues.</param>
        public DisconnectedPerformanceMonitor(
            CloudStorageAccount storageAccount,
            string taskHub,
            int? maxPollingIntervalMilliseconds = null)
            : this(storageAccount, GetSettings(taskHub, maxPollingIntervalMilliseconds))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DisconnectedPerformanceMonitor"/> class.
        /// </summary>
        /// <param name="storageAccount">The Azure Storage account to monitor.</param>
        /// <param name="settings">The orchestration service settings.</param>
        public DisconnectedPerformanceMonitor(
            CloudStorageAccount storageAccount,
            AzureStorageOrchestrationServiceSettings settings)
        {
            this.settings = settings;

            this.azureStorageClient = new AzureStorageClient(storageAccount, settings);

            this.maxPollingLatency = (int)settings.MaxQueuePollingInterval.TotalMilliseconds;
            this.highLatencyThreshold = Math.Min(this.maxPollingLatency, 1000);
        }

        /// <summary>
        /// Gets or sets a value to enable random scale-in (e.g. 10% of recommendations) when queue latencies are low.
        /// This property should be set to <c>false</c> for unit testing.
        /// </summary>
        public bool EnableRandomScaleDownOnLowLatency { get; set; } = true;

        internal virtual int PartitionCount => this.currentPartitionCount;

        internal List<QueueMetricHistory> ControlQueueLatencies => this.controlQueueLatencies;

        internal QueueMetricHistory WorkItemQueueLatencies => this.workItemQueueLatencies;

        static AzureStorageOrchestrationServiceSettings GetSettings(
            string taskHub,
            int? maxPollingIntervalMilliseconds = null)
        {
            var settings = new AzureStorageOrchestrationServiceSettings { TaskHubName = taskHub };
            if (maxPollingIntervalMilliseconds != null)
            {
                settings.MaxQueuePollingInterval = TimeSpan.FromMilliseconds(maxPollingIntervalMilliseconds.Value);
            }

            return settings;
        }

        /// <summary>
        /// Collects and returns a sampling of all performance metrics being observed by this instance as well as a scale
        /// recommendation.
        /// </summary>
        /// <param name="currentWorkerCount">The number of workers known to be processing messages for this task hub.</param>
        /// <returns>Returns a performance data summary with scale recommendation or <c>null</c> if data cannot be obtained.</returns>
        public virtual async Task<PerformanceHeartbeat> PulseAsync(int currentWorkerCount)
        {
            var heartbeatPayload = await this.PulseAsync();

            if (heartbeatPayload != null)
            {
                heartbeatPayload.ScaleRecommendation = MakeScaleRecommendation(currentWorkerCount);
            }

            return heartbeatPayload;
        }

        /// <summary>
        /// Collects and returns a sampling of all performance metrics being observed by this instance. Will not return a scale
        /// recommendation.
        /// </summary>
        /// <returns>Returns a performance data summary with no scale recommendation or <c>null</c> if data cannot be obtained.</returns>
        public virtual async Task<PerformanceHeartbeat> PulseAsync()
        {
            if (!await this.UpdateQueueMetrics())
            {
                return null;
            }

            var heartbeatPayload = new PerformanceHeartbeat
            {
                PartitionCount = this.PartitionCount,
                WorkItemQueueLatency = TimeSpan.FromMilliseconds(this.WorkItemQueueLatencies.Latest),
                WorkItemQueueLength = this.currentWorkItemQueueLength,
                WorkItemQueueLatencyTrend = this.WorkItemQueueLatencies.CurrentTrend,
                ControlQueueLengths = this.currentControlQueueLengths,
                ControlQueueLatencies = this.ControlQueueLatencies.Select(h => TimeSpan.FromMilliseconds(h.Latest)).ToList()
            };

            return heartbeatPayload;
        }

        internal virtual async Task<bool> UpdateQueueMetrics()
        {
            Queue workItemQueue = AzureStorageOrchestrationService.GetWorkItemQueue(this.azureStorageClient);
            Queue[] controlQueues = await AzureStorageOrchestrationService.GetControlQueuesAsync(
                this.azureStorageClient,
                defaultPartitionCount: AzureStorageOrchestrationServiceSettings.DefaultPartitionCount);

            Task<QueueMetric> workItemMetricTask = GetQueueMetricsAsync(workItemQueue);
            List<Task<QueueMetric>> controlQueueMetricTasks = controlQueues.Select(GetQueueMetricsAsync).ToList();

            var tasks = new List<Task>(controlQueueMetricTasks.Count + 1);
            tasks.Add(workItemMetricTask);
            tasks.AddRange(controlQueueMetricTasks);

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (StorageException e) when (e.RequestInformation?.HttpStatusCode == 404)
            {
                // The queues are not yet provisioned.
                this.settings.Logger.GeneralWarning(
                    this.azureStorageClient.StorageAccountName,
                    this.settings.TaskHubName,
                    $"Task hub has not been provisioned: {e.RequestInformation.ExtendedErrorInformation?.ErrorMessage}");
                return false;
            }

            QueueMetric workItemQueueMetric = workItemMetricTask.Result;
            this.WorkItemQueueLatencies.Add((int)workItemQueueMetric.Latency.TotalMilliseconds);

            int i;
            for (i = 0; i < controlQueueMetricTasks.Count; i++)
            {
                QueueMetric controlQueueMetric = controlQueueMetricTasks[i].Result;
                if (i >= this.ControlQueueLatencies.Count)
                {
                    this.ControlQueueLatencies.Add(new QueueMetricHistory(QueueLengthSampleSize));
                }

                this.ControlQueueLatencies[i].Add((int)controlQueueMetric.Latency.TotalMilliseconds);
            }

            // Handle the case where the number of control queues has been reduced since we last checked.
            while (i < this.ControlQueueLatencies.Count && this.ControlQueueLatencies.Count > 0)
            {
                this.ControlQueueLatencies.RemoveAt(this.ControlQueueLatencies.Count - 1);
            }

            this.currentPartitionCount = controlQueues.Length;
            this.currentWorkItemQueueLength = workItemQueueMetric.Length;
            this.currentControlQueueLengths = controlQueueMetricTasks.Select(t => t.Result.Length).ToArray();

            return true;
        }

        async Task<QueueMetric> GetQueueMetricsAsync(Queue queue)
        {
            Task<TimeSpan> latencyTask = GetQueueLatencyAsync(queue);
            Task<int> lengthTask = GetQueueLengthAsync(queue);
            await Task.WhenAll(latencyTask, lengthTask);

            TimeSpan latency = latencyTask.Result;
            int length = lengthTask.Result;

            if (latency == TimeSpan.MinValue)
            {
                // No available queue messages (peek returned null)
                latency = TimeSpan.Zero;
                length = 0;
            }

            return new QueueMetric { Latency = latency, Length = length };
        }

        static async Task<TimeSpan> GetQueueLatencyAsync(Queue queue)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            QueueMessage firstMessage = await queue.PeekMessageAsync();
            if (firstMessage == null)
            {
                return TimeSpan.MinValue;
            }

            // Make sure we always return a non-negative timespan in the success case.
            TimeSpan latency = now.Subtract(firstMessage.InsertionTime.GetValueOrDefault(now));
            return latency < TimeSpan.Zero ? TimeSpan.Zero : latency;
        }

        static async Task<int> GetQueueLengthAsync(Queue queue)
        {
            await queue.FetchAttributesAsync();
            return queue.ApproximateMessageCount.GetValueOrDefault(0);
        }

        struct QueueMetric
        {
            public TimeSpan Latency { get; set; }
            public int Length { get; set; }
        }

        /// <summary>
        /// Gets the scale-related status of the work-item queue.
        /// </summary>
        /// <returns>The approximate number of messages in the work-item queue.</returns>
        protected virtual async Task<WorkItemQueueData> GetWorkItemQueueStatusAsync()
        {
            Queue workItemQueue = AzureStorageOrchestrationService.GetWorkItemQueue(this.azureStorageClient);

            DateTimeOffset now = DateTimeOffset.Now;

            Task fetchTask = workItemQueue.FetchAttributesAsync();
            Task<QueueMessage> peekTask = workItemQueue.PeekMessageAsync();
            await Task.WhenAll(fetchTask, peekTask);

            int queueLength = workItemQueue.ApproximateMessageCount.GetValueOrDefault(0);
            TimeSpan age = now.Subtract((peekTask.Result?.InsertionTime).GetValueOrDefault(now));
            if (age < TimeSpan.Zero)
            {
                age = TimeSpan.Zero;
            }

            return new WorkItemQueueData
            {
                QueueLength = queueLength,
                FirstMessageAge = age,
            };
        }

        /// <summary>
        /// Gets the approximate aggreate length (sum) of the all known control queues.
        /// </summary>
        /// <returns>The approximate number of messages across all control queues.</returns>
        protected virtual async Task<ControlQueueData> GetAggregateControlQueueLengthAsync()
        {
            Queue[] controlQueues = await AzureStorageOrchestrationService.GetControlQueuesAsync(
                this.azureStorageClient,
                defaultPartitionCount: AzureStorageOrchestrationServiceSettings.DefaultPartitionCount);

            // There is one queue per partition.
            var result = new ControlQueueData();
            result.PartitionCount = controlQueues.Length;

            // We treat all control queues like one big queue and sum the lengths together.
            foreach (Queue queue in controlQueues)
            {
                await queue.FetchAttributesAsync();
                int queueLength = queue.ApproximateMessageCount.GetValueOrDefault(0);
                result.AggregateQueueLength += queueLength;
            }

            return result;
        }

        /// <summary>
        /// Calculates a Scale Recommendation based on in-memory performance metrics.
        /// </summary>
        /// <param name="workerCount">The number of workers known to be processing messages for this task hub.</param>
        /// <returns>Returns a scale recommendation</returns>
        public virtual ScaleRecommendation MakeScaleRecommendation(int workerCount)
        {
            return MakeScaleRecommendation(workerCount, this.PartitionCount, this.WorkItemQueueLatencies, this.ControlQueueLatencies);
        }

        /// <summary>
        /// Calculates a Scale Recommendation based on passed-in performance metrics.
        /// </summary>
        /// <param name="workerCount">The number of workers known to be processing messages for this task hub.</param>
        /// <param name="performanceHeartbeats">Previously collected, chronologically-ordered performance metrics.</param>
        /// <returns>Returns a scale recommendation</returns>
        public virtual ScaleRecommendation MakeScaleRecommendation(int workerCount, PerformanceHeartbeat[] performanceHeartbeats)
        {
            if (performanceHeartbeats == null || performanceHeartbeats.Length == 0)
            {
                return new ScaleRecommendation(ScaleAction.None, keepWorkersAlive: true, reason: "No heartbeat metrics");
            }

            int partitionCount = performanceHeartbeats.Last().PartitionCount;
            QueueMetricHistory workItemQueueLatencyHistory = new QueueMetricHistory(QueueLengthSampleSize);
            List<QueueMetricHistory> controlQueueLatencyHistory = new List<QueueMetricHistory>();

            foreach (PerformanceHeartbeat heartbeat in performanceHeartbeats)
            {
                workItemQueueLatencyHistory.Add((int)heartbeat.WorkItemQueueLatency.TotalMilliseconds);

                for (int i = 0; i < heartbeat.ControlQueueLatencies.Count; ++i)
                {
                    if (controlQueueLatencyHistory.Count <= i)
                    {
                        controlQueueLatencyHistory.Add(new QueueMetricHistory(QueueLengthSampleSize));
                    }
                    controlQueueLatencyHistory[i].Add((int)heartbeat.ControlQueueLatencies[i].TotalMilliseconds);
                }
            }

            return MakeScaleRecommendation(workerCount, partitionCount, workItemQueueLatencyHistory, controlQueueLatencyHistory);
        }

        internal ScaleRecommendation MakeScaleRecommendation(
            int workerCount,
            int partitionCount,
            QueueMetricHistory workItemQueueLatencyHistory,
            List<QueueMetricHistory> controlQueueLatencyHistory)
        {
            // REVIEW: Is zero latency a reliable indicator of idle?
            bool taskHubIsIdle = IsIdle(workItemQueueLatencyHistory) && controlQueueLatencyHistory.TrueForAll(IsIdle);
            if (workerCount == 0 && !taskHubIsIdle)
            {
                return new ScaleRecommendation(ScaleAction.AddWorker, keepWorkersAlive: true, reason: "First worker");
            }

            // Wait until we have enough samples before making specific recommendations
            if (!workItemQueueLatencyHistory.IsFull || !controlQueueLatencyHistory.TrueForAll(h => h.IsFull))
            {
                return new ScaleRecommendation(ScaleAction.None, keepWorkersAlive: !taskHubIsIdle, reason: "Not enough samples");
            }

            if (taskHubIsIdle)
            {
                return new ScaleRecommendation(
                    scaleAction: workerCount > 0 ? ScaleAction.RemoveWorker : ScaleAction.None,
                    keepWorkersAlive: false,
                    reason: "Task hub is idle");
            }
            else if (this.IsHighLatency(workItemQueueLatencyHistory))
            {
                return new ScaleRecommendation(
                    ScaleAction.AddWorker,
                    keepWorkersAlive: true,
                    reason: $"Work-item queue latency: {workItemQueueLatencyHistory.Latest} > {this.highLatencyThreshold}");
            }
            else if (workerCount > partitionCount && IsIdle(workItemQueueLatencyHistory))
            {
                return new ScaleRecommendation(
                    ScaleAction.RemoveWorker,
                    keepWorkersAlive: true,
                    reason: $"Work-items idle, #workers > partitions ({workerCount} > {partitionCount})");
            }

            // Control queues are partitioned; only scale-out if there are more partitions than workers.
            if (workerCount < controlQueueLatencyHistory.Count(this.IsHighLatency))
            {
                // Some control queues are busy, so scale out until workerCount == partitionCount.
                QueueMetricHistory metric = controlQueueLatencyHistory.First(this.IsHighLatency);
                return new ScaleRecommendation(
                    ScaleAction.AddWorker,
                    keepWorkersAlive: true,
                    reason: $"High control queue latency: {metric.Latest} > {this.highLatencyThreshold}");
            }
            else if (workerCount > controlQueueLatencyHistory.Count(h => !IsIdle(h)) && IsIdle(workItemQueueLatencyHistory))
            {
                // If the work item queues are idle, scale down to the number of non-idle control queues.
                return new ScaleRecommendation(
                    ScaleAction.RemoveWorker,
                    keepWorkersAlive: controlQueueLatencyHistory.Any(IsIdle),
                    reason: $"One or more control queues idle");
            }
            else if (workerCount > 1)
            {
                // If all queues are operating efficiently, it can be hard to know if we need to reduce the worker count.
                // We want to avoid the case where a constant trickle of load after a big scale-out prevents scaling back in.
                // We also want to avoid scaling in unnecessarily when we've reached optimal scale-out. To balance these
                // goals, we check for low latencies and vote to scale down 10% of the time when we see this. The thought is
                // that it's a slow scale-in that will get automatically corrected once latencies start increasing again.
                bool tryRandomScaleDown = this.EnableRandomScaleDownOnLowLatency && Random.Next(10) == 0;
                if (tryRandomScaleDown &&
                    controlQueueLatencyHistory.TrueForAll(IsLowLatency) &&
                    workItemQueueLatencyHistory.TrueForAll(latency => latency < LowLatencyThreshold))
                {
                    return new ScaleRecommendation(
                        ScaleAction.RemoveWorker,
                        keepWorkersAlive: true,
                        reason: $"All queues are not busy");
                }
            }

            // Load exists, but none of our scale filters were triggered, so we assume that the current worker
            // assignments are close to ideal for the current workload.
            return new ScaleRecommendation(ScaleAction.None, keepWorkersAlive: true, reason: $"Queue latencies are healthy");
        }

        bool IsHighLatency(QueueMetricHistory history)
        {
            if (history.Previous == 0)
            {
                // If previous was zero, the queue may have been idle, which means
                // backoff polling might have been the reason for the latency.
                return history.Latest >= this.maxPollingLatency;
            }

            return history.Latest >= this.highLatencyThreshold;
        }

        static bool IsLowLatency(QueueMetricHistory history)
        {
            return history.Latest <= LowLatencyThreshold && history.Previous <= LowLatencyThreshold;
        }

        static bool IsIdle(QueueMetricHistory history)
        {
            return history.IsAllZeros();
        }

        /// <summary>
        /// Data structure containing the number of partitions and the aggregate
        /// number of messages across those control queue partitions.
        /// </summary>
        public struct ControlQueueData
        {
            /// <summary>
            /// Gets or sets the number of control queue partitions.
            /// </summary>
            public int PartitionCount { get; internal set; }

            /// <summary>
            /// Gets or sets the number of messages across all control queues.
            /// </summary>
            public int AggregateQueueLength { get; internal set; }
        }

        /// <summary>
        /// Data structure containing scale-related statistics for the work-item queue.
        /// </summary>
        public struct WorkItemQueueData
        {
            /// <summary>
            /// Gets or sets the number of messages in the work-item queue.
            /// </summary>
            public int QueueLength { get; internal set; }

            /// <summary>
            /// Gets or sets the age of the first message in the work-item queue.
            /// </summary>
            public TimeSpan FirstMessageAge { get; internal set; }
        }

        internal class QueueMetricHistory
        {
            const double TrendThreshold = 0.0;

            readonly int[] history;
            int next;
            int count;
            int latestValue;
            int previousValue;
            double? currentTrend;

            public QueueMetricHistory(int maxSize)
            {
                this.history = new int[maxSize];
            }

            public bool IsFull
            {
                get { return this.count == this.history.Length; }
            }

            public int Latest => this.latestValue;

            public int Previous => this.previousValue;

            public bool IsTrendingUpwards => this.CurrentTrend > TrendThreshold;

            public bool IsTrendingDownwards => this.CurrentTrend < -TrendThreshold;

            public double CurrentTrend
            {
                get
                {
                    if (!this.IsFull)
                    {
                        return 0.0;
                    }

                    if (!this.currentTrend.HasValue)
                    {
                        int firstIndex = this.IsFull ? this.next : 0;
                        int first = this.history[firstIndex];
                        if (first == 0)
                        {
                            // discard trend information when the first item is a zero.
                            this.currentTrend = 0.0;
                        }
                        else
                        {
                            int sum = 0;
                            for (int i = 0; i < this.history.Length; i++)
                            {
                                sum += this.history[i];
                            }

                            double average = (double)sum / this.history.Length;
                            this.currentTrend = (average - first) / first;
                        }
                    }

                    return this.currentTrend.Value;
                }
            }

            public void Add(int value)
            {
                this.history[this.next++] = value;
                if (this.count < this.history.Length)
                {
                    this.count++;
                }

                if (this.next >= this.history.Length)
                {
                    this.next = 0;
                }

                this.previousValue = this.latestValue;
                this.latestValue = value;

                // invalidate any existing trend information
                this.currentTrend = null;
            }

            public bool IsAllZeros()
            {
                return Array.TrueForAll(this.history, i => i == 0);
            }

            public bool TrueForAll(Predicate<int> predicate)
            {
                return Array.TrueForAll(this.history, predicate);
            }

            public override string ToString()
            {
                var builder = new StringBuilder();
                builder.Append('[');

                for (int i = 0; i < this.history.Length; i++)
                {
                    int index = (i + this.next) % this.history.Length;
                    builder.Append(this.history[index]).Append(',');
                }

                builder.Remove(builder.Length - 1, 1).Append(']');
                return builder.ToString();
            }

            static void ThrowIfNegative(string paramName, double value)
            {
                if (value < 0.0)
                {
                    throw new ArgumentOutOfRangeException(paramName, value, $"{paramName} cannot be negative.");
                }
            }

            static void ThrowIfPositive(string paramName, double value)
            {
                if (value > 0.0)
                {
                    throw new ArgumentOutOfRangeException(paramName, value, $"{paramName} cannot be positive.");
                }
            }
        }
    }
}

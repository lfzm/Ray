﻿using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Ray.Core.EventBus;
using Ray.Core.Services.Abstractions;
using Ray.Core.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ray.EventBus.Kafka
{
    public class ConsumerManager : IConsumerManager
    {
        readonly ILogger<ConsumerManager> logger;
        readonly IKafkaClient client;
        readonly IKafkaEventBusContainer kafkaEventBusContainer;
        readonly IServiceProvider provider;
        readonly KafkaEventBusOptions kafkaEventBusOptions;
        readonly IGrainFactory grainFactory;
        public ConsumerManager(
            ILogger<ConsumerManager> logger,
            IKafkaClient client,
            IGrainFactory grainFactory,
            IServiceProvider provider,
            IOptions<KafkaEventBusOptions> kafkaEventBusOptions,
            IKafkaEventBusContainer kafkaEventBusContainer)
        {
            this.provider = provider;
            this.client = client;
            this.logger = logger;
            this.kafkaEventBusOptions = kafkaEventBusOptions.Value;
            this.kafkaEventBusContainer = kafkaEventBusContainer;
            this.grainFactory = grainFactory;
        }
        private ConcurrentDictionary<string, List<ConsumerRunner>> NodeRunnerDict { get; } = new ConcurrentDictionary<string, List<ConsumerRunner>>();
        private ConcurrentDictionary<string, long> LockDict { get; } = new ConcurrentDictionary<string, long>();
        private Timer HeathCheckTimer { get; set; }
        private Timer DistributedMonitorTime { get; set; }
        private Timer DistributedHoldTimer { get; set; }
        const int lockHoldingSeconds = 60;
        int distributedMonitorTimeLock = 0;
        int distributedHoldTimerLock = 0;
        int heathCheckTimerLock = 0;

        public async Task Start()
        {
            if (kafkaEventBusOptions.Nodes != default && kafkaEventBusOptions.Nodes.Length > 0)
            {
                DistributedMonitorTime = new Timer(state => DistributedStart().Wait(), null, 1000, 60 * 2 * 1000);
                DistributedHoldTimer = new Timer(state => DistributedHold().Wait(), null, 20 * 1000, 20 * 1000);
            }
            else
            {
                await Start("default", null);
            }
            HeathCheckTimer = new Timer(state => { HeathCheck().Wait(); }, null, 5 * 1000, 10 * 1000);
        }
        private async Task DistributedStart()
        {
            try
            {
                if (Interlocked.CompareExchange(ref distributedMonitorTimeLock, 1, 0) == 0)
                {
                    foreach (var node in kafkaEventBusOptions.Nodes)
                    {
                        if (!NodeRunnerDict.TryGetValue(node, out var consumerRunners))
                        {
                            int weight = NodeRunnerDict.Count > 0 ? 100 : 99;
                            var (isOk, lockId, expectMillisecondDelay) = await grainFactory.GetGrain<IWeightHoldLock>(node).Lock(weight, lockHoldingSeconds);

                            if (!isOk && expectMillisecondDelay > 0)
                            {
                                await Task.Delay(expectMillisecondDelay + 100);
                                (isOk, lockId, expectMillisecondDelay) = await grainFactory.GetGrain<IWeightHoldLock>(node).Lock(weight, lockHoldingSeconds);
                                if (isOk)
                                    await Task.Delay(10 * 1000);
                            }
                            if (isOk)
                            {
                                await Start(node, kafkaEventBusOptions.Nodes);
                                LockDict.TryAdd(node, lockId);
                                break;
                            }
                        }
                    }
                    Interlocked.Exchange(ref distributedMonitorTimeLock, 0);
                }
            }
            catch (Exception exception)
            {
                logger.LogError(exception.InnerException ?? exception, nameof(DistributedStart));
                Interlocked.Exchange(ref distributedMonitorTimeLock, 0);
            }
        }
        private async Task DistributedHold()
        {
            try
            {
                if (Interlocked.CompareExchange(ref distributedHoldTimerLock, 1, 0) == 0)
                {
                    foreach (var lockKV in LockDict)
                    {
                        if (LockDict.TryGetValue(lockKV.Key, out var lockId))
                        {
                            var holdResult = await grainFactory.GetGrain<IWeightHoldLock>(lockKV.Key).Hold(lockId, lockHoldingSeconds);
                            if (!holdResult && NodeRunnerDict.TryGetValue(lockKV.Key, out var consumerRunners))
                            {
                                consumerRunners.ForEach(runner => runner.Close());
                                NodeRunnerDict.TryRemove(lockKV.Key, out var _);
                                LockDict.TryRemove(lockKV.Key, out var _);
                            }
                        }
                    }
                    Interlocked.Exchange(ref distributedHoldTimerLock, 0);
                }
            }
            catch (Exception exception)
            {
                logger.LogError(exception.InnerException ?? exception, nameof(DistributedHold));
                Interlocked.Exchange(ref distributedHoldTimerLock, 0);
            }
        }
        private async Task Start(string node = null, string[] nodeList = null)
        {
            var consumers = kafkaEventBusContainer.GetConsumers();
            var hash = nodeList == null || nodeList.Length == 0 ? null : new ConsistentHash(nodeList);
            var consumerList = new SortedList<int, ConsumerRunner>();
            var rd = new Random((int)DateTimeOffset.UtcNow.Ticks);
            foreach (var consumer in consumers)
            {
                if (consumer is KafkaConsumer value)
                {
                    for (int i = 0; i < value.Topics.Count; i++)
                    {
                        var topic = value.Topics[i];
                        var hashNode = hash != null && nodeList.Length > 1 ? hash.GetNode(topic) : node;
                        if (node == hashNode)
                        {
                            consumerList.Add(rd.Next(), new ConsumerRunner(client, provider.GetService<ILogger<ConsumerRunner>>(), value, topic));
                        }
                    }
                }
            }
            await Work(node, consumerList.Values.ToList());
        }
        private async Task Work(string node, List<ConsumerRunner> consumerRunners)
        {
            if (consumerRunners != default)
            {
                if (NodeRunnerDict.TryAdd(node, consumerRunners))
                {
                    for (int i = 0; i < consumerRunners.Count; i++)
                    {
                        await consumerRunners[i].Run();
                        await Task.Delay(kafkaEventBusOptions.QueueStartMillisecondsDelay);
                    }
                }
            }
        }
        private async Task HeathCheck()
        {
            try
            {
                if (Interlocked.CompareExchange(ref heathCheckTimerLock, 1, 0) == 0)
                {
                    foreach (var runners in NodeRunnerDict.Values)
                    {
                        await Task.WhenAll(runners.Select(runner => runner.HeathCheck()));
                    }
                    Interlocked.Exchange(ref heathCheckTimerLock, 0);
                }
            }
            catch (Exception exception)
            {
                logger.LogError(exception.InnerException ?? exception, nameof(HeathCheck));
                Interlocked.Exchange(ref heathCheckTimerLock, 0);
            }
        }
        public void Stop()
        {
            if (NodeRunnerDict != null)
            {
                foreach (var runners in NodeRunnerDict.Values)
                {
                    foreach (var runner in runners)
                    {
                        runner.Close();
                    }
                }
            }
            HeathCheckTimer?.Dispose();
            DistributedMonitorTime?.Dispose();
            DistributedHoldTimer?.Dispose();
        }
    }
}

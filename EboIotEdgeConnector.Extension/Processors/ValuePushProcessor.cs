﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ews.Common;
using Mongoose.Common;
using Mongoose.Common.Attributes;
using Mongoose.Process;
using Mongoose.Process.Ews;
using MQTTnet;
using SxL.Common;

namespace EboIotEdgeConnector.Extension
{
    [ConfigurationDefaults("Value Push Processor",
        "This processor gets runtime values from EBO, and pushes them to Azure as defined by the signal CSV file.")]
    public class ValuePushProcessor : EboIotEdgeConnectorProcessorWithMqttBase, ILongRunningProcess
    {
        private const int MaxItemsPerSubscription = 500;

        #region Execute_Subclass - Override
        protected override IEnumerable<Prompt> Execute_Subclass()
        {
            try
            {
                StartMqttClient().Wait();
            }
            catch (Exception ex)
            {
                Logger.LogError(LogCategory.Processor, this.Name, $"Starting MQTT Client failed");
                Prompts.Add(ex.ToPrompt());
                return Prompts;
            }

            if (Signals == null)
            {
                Prompts.Add(new Prompt
                {
                    Message = "There are no signals in the cache, please run the SetupProcessor or verify that it has run successfully.",
                    Severity = PromptSeverity.MayNotContinue
                });
                return Prompts;
            }

            // Read existing subscriptions
            if (!ReadExistingSubscriptions(Signals).Result)
            {
                Prompts.Add(new Prompt {Message = $"Did not successfully read all existing subscriptions."});
            }

            // Subscribe and read new subscriptions
            if (!SubscribeAndReadNew(Signals).Result)
            {
                Prompts.Add(new Prompt {Message = $"Did not successfully read all new subscriptions."});
            }

            ManagedMqttClient.StopAsync().Wait();
            ManagedMqttClient.Dispose();
    
            // Update the cache with new values..
            Signals = Signals;
            return Prompts;
        }
        #endregion

        #region ReadExistingSubscription
        private async Task<bool> ReadExistingSubscriptions(List<Signal> signals)
        {
            Logger.LogTrace(LogCategory.Processor, this.Name, $"Reading existing subscriptions..");

            var activeSubscriptions = Cache.RetrieveItem($"ActiveSubscriptions", () => new List<string>(), CacheTenantId, 0) as List<string>;
            var activeSubscriptionsToIterate = activeSubscriptions.ToList();
            foreach (var sub in activeSubscriptionsToIterate)
            {
                var subscription = Cache.RetrieveItem($"ActiveSubscriptions#{sub}", CacheTenantId);
                Logger.LogDebug(LogCategory.Processor, $"Reading existing subscription: {sub}");
                try
                {
                    CheckCancellationToken();

                    var si = new SubscriptionReader
                    {
                        Address = EboEwsSettings.Address,
                        UserName = EboEwsSettings.UserName,
                        Password = EboEwsSettings.Password,
                        SubscriptionEventType = EwsSubscriptionEventTypeEnum.ValueItemChanged,
                        SubscriptionId = sub
                    };

                    // Attempt to update the values by reading the subscription, if this fails return all Prompts
                    if (!await UpdateValues(si))
                    {
                        if (!si.IsResubscribeRequired) return false;
                        activeSubscriptions.Remove(sub);
                        Cache.DeleteItem($"ActiveSubscriptions#{sub}", CacheTenantId);
                    }

                    // It's possible that the subscription id has changed if it failed to be renewed/ updated... reset it here
                    if (si.SubsciptionChanged)
                    {
                        Logger.LogDebug(LogCategory.Processor, $"Subscription Id {sub} has changed to {si.SubscriptionId}, updating cache values to represent this");
                        activeSubscriptions.Remove(sub);
                        activeSubscriptions.Add(si.SubscriptionId);
                        Cache.DeleteItem($"ActiveSubscriptions#{sub}", CacheTenantId);
                        Cache.AddOrUpdateItem(subscription, $"ActiveSubscriptions#{si.SubscriptionId}", CacheTenantId, 0);
                    }
                }

                catch (Exception)
                {
                    activeSubscriptions.Remove(sub);
                    Cache.DeleteItem($"ActiveSubscriptions#{sub}", CacheTenantId);
                }
            }

            // Save any changes to cache
            Cache.AddOrUpdateItem(activeSubscriptions, $"ActiveSubscriptions", CacheTenantId, 0);
            return true;
        }
        #endregion
        #region SubscribeAndReadNew
        private async Task<bool> SubscribeAndReadNew(List<Signal> signals)
        {
            Logger.LogTrace(LogCategory.Processor, this.Name, $"Creating and reading new subscriptions..");
            var activeSubscriptions = Cache.RetrieveItem($"ActiveSubscriptions", () => new List<string>(), CacheTenantId, 0);

            var subscribedIds = new List<string>();

            foreach (var subscription in activeSubscriptions)
            {
                if (Cache.RetrieveItem($"ActiveSubscriptions#{subscription}", CacheTenantId) is List<string> currentSub) subscribedIds.AddRange(currentSub);
            }

            var unsubscribedIds = signals.Select(a => a.EwsId).Where(a => !subscribedIds.Contains(a)).ToList();

            while (unsubscribedIds.Any())
            {
                try
                {
                    CheckCancellationToken();
                    var si = new SubscriptionReader
                    {
                        Address = EboEwsSettings.Address,
                        UserName = EboEwsSettings.UserName,
                        Password = EboEwsSettings.Password,
                        SubscriptionEventType = EwsSubscriptionEventTypeEnum.ValueItemChanged,
                        Ids = signals.Select(a => a.EwsId).Take(MaxItemsPerSubscription).ToList()
                    };

                    // Attempt to update the values by reading the subscription, if this fails return all false as this could go on forever.
                    if (!await UpdateValues(si)) return false;

                    Cache.AddOrUpdateItem(si.SubscribedItems, $"ActiveSubscriptions#{si.SubscriptionId}", CacheTenantId, 0);
                    unsubscribedIds = unsubscribedIds.Skip(MaxItemsPerSubscription).ToList();

                    activeSubscriptions.Add(si.SubscriptionId);
                    Cache.AddOrUpdateItem(activeSubscriptions, $"ActiveSubscriptions", CacheTenantId, 0);

                    // Add any prompts generated from reader to the list of prompts
                    Prompts.AddRange(si.ReadData().Prompts);

                    if (si.FailedSubscribedItems.Any()) Logger.LogInfo(LogCategory.Processor, this.Name, $"Some items failed to be subscribed to: {si.FailedSubscribedItems.ToJSON()}");
                }

                catch (Exception ex)
                {
                    Prompts.Add(ex.ToPrompt());
                    break;
                }
            }

            return true;

            // TODO: How to handle subscriptions to value items that keep failing?
        }
        #endregion
        #region UpdateValues
        private async Task<bool> UpdateValues(SubscriptionReader si)
        {
            var results = si.ReadData();
            if (!results.Success)
            {
                Prompts.AddRange(results.Prompts);
                return false;
            }

            var devices = results.DataRead.GroupBy(a => a.ValueItemChangeEvent.Id.Remove(a.ValueItemChangeEvent.Id.LastIndexOf('/')).Remove(0,2));

            foreach (var device in devices)
            {
                var observations = new List<Observation>();
                var deviceMessage = new IotEdgeMessage
                {
                    Format = "rec2.3",
                    Observations = observations,
                    DeviceId = device.Key
                };

                AddUpdatedValuesToMessage(observations, device.Key, device.ToList(), si.CachedSubscribedItems);

                var messageBuilder = new MqttApplicationMessageBuilder();
                var message = messageBuilder.WithRetainFlag().WithAtLeastOnceQoS().WithTopic(ValuePushTopic).WithPayload(deviceMessage.ToJson()).Build();

                await ManagedMqttClient.PublishAsync(message);
            }

            return true;
        }
        #endregion
        #region AddUpdatedValuesToMessage
        private void AddUpdatedValuesToMessage(List<Observation> observations, string devicePath, List<SubscriptionResultItem> pointsToAdd, List<string> pointsMonitoredBySub)
        {
            foreach (var eventz in pointsToAdd)
            {
                var signal = Signals.FirstOrDefault(a => a.EwsId == eventz.ValueItemChangeEvent.Id);
                if (signal == null)
                {
                    Logger.LogInfo(LogCategory.Processor, this.Name, $"Signal with EWS ID of {eventz.ValueItemChangeEvent.Id} does not exist.. Skipping this..");
                    continue;
                }

                signal.Value = eventz.ValueItemChangeEvent.Value;
                signal.LastUpdateTime = eventz.ValueItemChangeEvent.TimeStamp.ToUniversalTime();
                if (signal.SendOnUpdate)
                {
                    HandleAddingToObservationsList(observations, signal);
                }
            }

            foreach (var signal in Signals.Where(a => pointsMonitoredBySub.Contains(a.EwsId)))
            {
                if (signal.LastSendTime != null && signal.LastSendTime.Value.AddSeconds(signal.SendTime) > DateTimeOffset.Now) continue;
                if (observations.All(a => $"{devicePath}/{a.SensorId}" != signal.DatabasePath))
                {
                    HandleAddingToObservationsList(observations, signal);
                }
            }
        }
        #endregion

        #region HandleMqttApplicationMessageReceived - Override
        public override void HandleMqttApplicationMessageReceived(string topic, string decodedMessage)
        {
            // In theory, this should not be receiving observations, just log this was unexpected
            Logger.LogInfo(LogCategory.Processor, this.Name, $"{this.Name} unexpectedely received a message..");
        }
        #endregion
        #region SubscribeToMqttTopics - Override
        public override void SubscribeToMqttTopics()
        {
            // Not topics to subscribe to, intentionally blank
        }
        #endregion
    }
}

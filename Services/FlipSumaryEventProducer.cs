using System;
using Confluent.Kafka;
using Confluent.SchemaRegistry.Serdes;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;

namespace Coflnet.Sky.SkyAuctionTracker.Services
{
    /// <summary>
    /// 
    /// </summary>
    public class FlipSumaryEventProducer : IDisposable
    {
        private readonly string topic;
        private IProducer<string, string> producer;

        /// <summary>
        /// Create new instance of <see cref="FlipSumaryEventProducer"/>
        /// </summary>
        /// <param name="config"></param>
        /// <param name="kafkaCreate"></param>
        public FlipSumaryEventProducer(IConfiguration config, Kafka.KafkaCreator kafkaCreate)
        {
            producer = kafkaCreate.BuildProducer<string, string>();
            topic = config["TOPICS:FLIP_SUMMARY"];
            _ = kafkaCreate.CreateTopicIfNotExist(topic);
        }

        /// <summary>
        /// Disposes the producer
        /// </summary>
        public void Dispose()
        {
            producer.Dispose();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="details"></param>
        public void Produce(FlipSumaryEvent details)
        {
            var data = new Message<string, string> { Key = details.SellUuid, Value = JsonConvert.SerializeObject(details) };
            producer.Produce(topic, data);
        }
    }
}
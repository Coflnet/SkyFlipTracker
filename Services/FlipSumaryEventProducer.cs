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
        public FlipSumaryEventProducer(IConfiguration config)
        {
            producer = new ProducerBuilder<string, string>(new ProducerConfig
            {
                BootstrapServers = config["KAFKA_HOST"],
                ClientId = "sky-fliptracker",
                BatchSize = 16384*16,
                LingerMs = 10,
                MessageSendMaxRetries = 3,
                CompressionType = CompressionType.Gzip
            }).Build();
            topic = config["TOPICS:FLIP_SUMMARY"];
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
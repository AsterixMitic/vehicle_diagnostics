using MassTransit;
using Microsoft.Extensions.Options;
using VehicleDiagnostics.Contracts.Internal;

namespace VehicleDiagnostics.Infrastructure.Messaging;

/// <summary>
/// Same-device messages are serialized through a partitioner keyed by device identifier
/// (design D4, ADR-0004): order per device, parallelism across devices, valid while the
/// consumer runs in a single process.
/// </summary>
public sealed class DecodeRawFramesConsumerDefinition(IOptions<BusOptions> options)
    : ConsumerDefinition<DecodeRawFramesConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<DecodeRawFramesConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        ArgumentNullException.ThrowIfNull(endpointConfigurator);
        ArgumentNullException.ThrowIfNull(consumerConfigurator);
        endpointConfigurator.PrefetchCount = options.Value.PrefetchCount;
        IPartitioner partitioner = endpointConfigurator.CreatePartitioner(options.Value.PartitionCount);
        consumerConfigurator.Message<RawFramesReceived>(m => m.UsePartitioner(partitioner, ctx => ctx.Message.DeviceId));
    }
}

public sealed class PersistDecodedSignalsConsumerDefinition(IOptions<BusOptions> options)
    : ConsumerDefinition<PersistDecodedSignalsConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<PersistDecodedSignalsConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        ArgumentNullException.ThrowIfNull(endpointConfigurator);
        ArgumentNullException.ThrowIfNull(consumerConfigurator);
        endpointConfigurator.PrefetchCount = options.Value.PrefetchCount;
        IPartitioner partitioner = endpointConfigurator.CreatePartitioner(options.Value.PartitionCount);
        consumerConfigurator.Message<SignalsDecoded>(m => m.UsePartitioner(partitioner, ctx => ctx.Message.DeviceId));
    }
}

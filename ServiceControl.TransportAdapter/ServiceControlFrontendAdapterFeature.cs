﻿using System;
using System.Threading.Tasks;
using Metrics;
using NServiceBus;
using NServiceBus.ConsistencyGuarantees;
using NServiceBus.Features;
using NServiceBus.ObjectBuilder;
using NServiceBus.Transport;

namespace ServiceControl.TransportAdapter
{
    class ServiceControlFrontendAdapterFeature : Feature
    {
        protected override void Setup(FeatureConfigurationContext context)
        {
            var auditForwardMeter = Metric.Meter("Audit", Unit.Custom("Messages"));

            var retrySatelliteAddress = GetSatelliteAddress(context, "retry");
            var requiredTransactionSupport = context.Settings.GetRequiredTransactionModeForReceives();

            var serviceControlErrorQueue = context.Settings.GetOrDefault<string>("ServiceControl.ErrorQueue") ?? "error";
            var serviceControlAuditQueue = context.Settings.GetOrDefault<string>("ServiceControl.AuditQueue") ?? "audit";
            var serviceControlHeartbeatQueue = context.Settings.GetOrDefault<string>("ServiceControl.HeartbeatQueue") ?? "Particular.ServiceControl";

            context.AddSatelliteReceiver("ForwardErrors", GetSatelliteAddress(context, "error"), requiredTransactionSupport, new PushRuntimeSettings(), HandleFailure,
                (builder, messageContext) =>
                {
                    Console.WriteLine("Forwarding failed message");
                    messageContext.Headers["ServiceControl.Adapter.RetryTo"] = retrySatelliteAddress;

                    return Forward(builder, messageContext, serviceControlErrorQueue);
                });

            context.AddSatelliteReceiver("ForwardHeartbeats", GetSatelliteAddress(context, "control"), requiredTransactionSupport, new PushRuntimeSettings(), HandleFailure,
                (builder, messageContext) => Forward(builder, messageContext, serviceControlHeartbeatQueue));

            context.AddSatelliteReceiver("ForwardAudits", GetSatelliteAddress(context, "audit"), requiredTransactionSupport, new PushRuntimeSettings(), HandleFailure,
                async (builder, messageContext) =>
                {
                    //Console.WriteLine("Forwarding processed message");
                    await Forward(builder, messageContext, serviceControlAuditQueue).ConfigureAwait(false);
                    auditForwardMeter.Mark();
                });
        }

        static string GetSatelliteAddress(FeatureConfigurationContext context, string suffix)
        {
            var satelliteLogicalAddress = context.Settings.LogicalAddress().CreateQualifiedAddress(suffix);
            var satelliteAddress = context.Settings.GetTransportAddress(satelliteLogicalAddress);
            return satelliteAddress;
        }

        static RecoverabilityAction HandleFailure(RecoverabilityConfig config, ErrorContext context)
        {
            return RecoverabilityAction.ImmediateRetry(); //For now
        }

        static Task Forward(IBuilder builder, MessageContext context, string queue)
        {
            var forwarder = builder.Build<Forwarder>();

            return forwarder.Forward(queue, context.MessageId, context.Headers, context.Body,
                context.TransportTransaction);
        }
    }
}
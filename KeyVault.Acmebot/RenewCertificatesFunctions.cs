﻿using System.Threading.Tasks;

using DurableTask.TypedProxy;

using KeyVault.Acmebot.Contracts;

using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Extensions.Logging;

namespace KeyVault.Acmebot
{
    public class RenewCertificatesFunctions
    {
        [FunctionName(nameof(RenewCertificates))]
        public async Task RenewCertificates([OrchestrationTrigger] IDurableOrchestrationContext context, ILogger log)
        {
            var activity = context.CreateActivityProxy<ISharedFunctions>();

            var certificates = await activity.GetCertificates(context.CurrentUtcDateTime);

            if (certificates.Count == 0)
            {
                log.LogInformation("Certificates are not found");

                return;
            }

            foreach (var certificate in certificates)
            {
                log.LogInformation($"{certificate.Id} - {certificate.Attributes.Expires}");

                await context.CallSubOrchestratorAsync(nameof(SharedFunctions.IssueCertificate), certificate.Policy.X509CertificateProperties.SubjectAlternativeNames.DnsNames);
            }
        }

        [FunctionName(nameof(RenewCertificates_Timer))]
        public static async Task RenewCertificates_Timer(
            [TimerTrigger("0 0 0 * * 1,3,5")] TimerInfo timer,
            [DurableClient] IDurableClient starter,
            ILogger log)
        {
            // Function input comes from the request content.
            var instanceId = await starter.StartNewAsync(nameof(RenewCertificates), null);

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");
        }
    }
}
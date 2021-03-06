﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

using ACMESharp.Authorizations;
using ACMESharp.Protocol;

using DnsClient;

using DurableTask.TypedProxy;

using KeyVault.Acmebot.Contracts;
using KeyVault.Acmebot.Internal;
using KeyVault.Acmebot.Models;

using Microsoft.Azure.KeyVault;
using Microsoft.Azure.KeyVault.Models;
using Microsoft.Azure.Management.Dns;
using Microsoft.Azure.Management.Dns.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Extensions.Options;

namespace KeyVault.Acmebot
{
    public class SharedFunctions : ISharedFunctions
    {
        public SharedFunctions(IHttpClientFactory httpClientFactory, LookupClient lookupClient,
                               IAcmeProtocolClientFactory acmeProtocolClientFactory, IOptions<LetsEncryptOptions> options,
                               KeyVaultClient keyVaultClient, DnsManagementClient dnsManagementClient)
        {
            _httpClientFactory = httpClientFactory;
            _lookupClient = lookupClient;
            _acmeProtocolClientFactory = acmeProtocolClientFactory;
            _options = options.Value;
            _keyVaultClient = keyVaultClient;
            _dnsManagementClient = dnsManagementClient;
        }

        private const string InstanceIdKey = "InstanceId";

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly LookupClient _lookupClient;
        private readonly IAcmeProtocolClientFactory _acmeProtocolClientFactory;
        private readonly LetsEncryptOptions _options;
        private readonly KeyVaultClient _keyVaultClient;
        private readonly DnsManagementClient _dnsManagementClient;

        [FunctionName(nameof(IssueCertificate))]
        public async Task IssueCertificate([OrchestrationTrigger] IDurableOrchestrationContext context)
        {
            var dnsNames = context.GetInput<string[]>();

            var activity = context.CreateActivityProxy<ISharedFunctions>();

            await activity.Dns01Precondition(dnsNames);

            var orderDetails = await activity.Order(dnsNames);

            var challenges = new List<ChallengeResult>();

            foreach (var authorization in orderDetails.Payload.Authorizations)
            {
                var result = await activity.Dns01Authorization((authorization, context.ParentInstanceId ?? context.InstanceId));

                await activity.CheckDnsChallenge(result);

                challenges.Add(result);
            }

            await activity.AnswerChallenges(challenges);

            await activity.CheckIsReady(orderDetails);

            await activity.FinalizeOrder((dnsNames, orderDetails));
        }

        [FunctionName(nameof(GetCertificates))]
        public async Task<IList<CertificateBundle>> GetCertificates([ActivityTrigger] DateTime currentDateTime)
        {
            var certificates = await _keyVaultClient.GetAllCertificatesAsync(_options.VaultBaseUrl);

            var list = certificates.Where(x => x.Tags != null && x.Tags.TryGetValue("Issuer", out var issuer) && issuer == "letsencrypt.org")
                                   .Where(x => (x.Attributes.Expires.Value - currentDateTime).TotalDays < 30)
                                   .ToArray();

            var bundles = new List<CertificateBundle>();

            foreach (var item in list)
            {
                bundles.Add(await _keyVaultClient.GetCertificateAsync(item.Id));
            }

            return bundles;
        }

        [FunctionName(nameof(GetZones))]
        public Task<IList<Zone>> GetZones([ActivityTrigger] object input = null)
        {
            return _dnsManagementClient.Zones.ListAllAsync();
        }

        [FunctionName(nameof(Order))]
        public async Task<OrderDetails> Order([ActivityTrigger] string[] hostNames)
        {
            var acmeProtocolClient = await _acmeProtocolClientFactory.CreateClientAsync();

            return await acmeProtocolClient.CreateOrderAsync(hostNames);
        }

        [FunctionName(nameof(Dns01Precondition))]
        public async Task Dns01Precondition([ActivityTrigger] string[] hostNames)
        {
            var zones = await _dnsManagementClient.Zones.ListAllAsync();

            foreach (var hostName in hostNames)
            {
                if (!zones.Any(x => hostName.EndsWith(x.Name)))
                {
                    throw new InvalidOperationException($"Azure DNS zone \"{hostName}\" is not found");
                }
            }
        }

        [FunctionName(nameof(Dns01Authorization))]
        public async Task<ChallengeResult> Dns01Authorization([ActivityTrigger] (string, string) input)
        {
            var (authzUrl, instanceId) = input;

            var acmeProtocolClient = await _acmeProtocolClientFactory.CreateClientAsync();

            var authz = await acmeProtocolClient.GetAuthorizationDetailsAsync(authzUrl);

            var challenge = authz.Challenges.First(x => x.Type == "dns-01");

            var challengeValidationDetails = AuthorizationDecoder.ResolveChallengeForDns01(authz, challenge, acmeProtocolClient.Signer);

            var zone = (await _dnsManagementClient.Zones.ListAllAsync()).First(x => challengeValidationDetails.DnsRecordName.EndsWith(x.Name));

            var resourceGroup = ExtractResourceGroup(zone.Id);

            var acmeDnsRecordName = challengeValidationDetails.DnsRecordName.Replace("." + zone.Name, "");

            RecordSet recordSet;

            try
            {
                recordSet = await _dnsManagementClient.RecordSets.GetAsync(resourceGroup, zone.Name, acmeDnsRecordName, RecordType.TXT);
            }
            catch
            {
                recordSet = null;
            }

            if (recordSet != null)
            {
                if (recordSet.Metadata == null || !recordSet.Metadata.TryGetValue(InstanceIdKey, out var dnsInstanceId) || dnsInstanceId != instanceId)
                {
                    recordSet.Metadata = new Dictionary<string, string>
                    {
                        { InstanceIdKey, instanceId }
                    };

                    recordSet.TxtRecords.Clear();
                }

                recordSet.TTL = 60;

                recordSet.TxtRecords.Add(new TxtRecord(new[] { challengeValidationDetails.DnsRecordValue }));
            }
            else
            {
                recordSet = new RecordSet
                {
                    TTL = 60,
                    Metadata = new Dictionary<string, string>
                    {
                        { InstanceIdKey, instanceId }
                    },
                    TxtRecords = new[]
                    {
                        new TxtRecord(new[] { challengeValidationDetails.DnsRecordValue })
                    }
                };
            }

            await _dnsManagementClient.RecordSets.CreateOrUpdateAsync(resourceGroup, zone.Name, acmeDnsRecordName, RecordType.TXT, recordSet);

            return new ChallengeResult
            {
                Url = challenge.Url,
                DnsRecordName = challengeValidationDetails.DnsRecordName,
                DnsRecordValue = challengeValidationDetails.DnsRecordValue
            };
        }

        [FunctionName(nameof(CheckDnsChallenge))]
        public async Task CheckDnsChallenge([ActivityTrigger] ChallengeResult challenge)
        {
            var queryResult = await _lookupClient.QueryAsync(challenge.DnsRecordName, QueryType.TXT);

            var txtRecords = queryResult.Answers
                                        .OfType<DnsClient.Protocol.TxtRecord>()
                                        .ToArray();

            if (txtRecords.Length == 0)
            {
                throw new RetriableActivityException($"{challenge.DnsRecordName} did not resolve.");
            }

            if (!txtRecords.Any(x => x.Text.Contains(challenge.DnsRecordValue)))
            {
                throw new RetriableActivityException($"{challenge.DnsRecordName} value is not correct.");
            }
        }

        [FunctionName(nameof(CheckIsReady))]
        public async Task CheckIsReady([ActivityTrigger] OrderDetails orderDetails)
        {
            var acmeProtocolClient = await _acmeProtocolClientFactory.CreateClientAsync();

            orderDetails = await acmeProtocolClient.GetOrderDetailsAsync(orderDetails.OrderUrl, orderDetails);

            if (orderDetails.Payload.Status == "pending")
            {
                throw new RetriableActivityException("ACME domain validation is pending.");
            }

            if (orderDetails.Payload.Status == "invalid")
            {
                throw new InvalidOperationException("Invalid order status. Required retry at first.");
            }
        }

        [FunctionName(nameof(AnswerChallenges))]
        public async Task AnswerChallenges([ActivityTrigger] IList<ChallengeResult> challenges)
        {
            var acmeProtocolClient = await _acmeProtocolClientFactory.CreateClientAsync();

            foreach (var challenge in challenges)
            {
                await acmeProtocolClient.AnswerChallengeAsync(challenge.Url);
            }
        }

        [FunctionName(nameof(FinalizeOrder))]
        public async Task FinalizeOrder([ActivityTrigger] (string[], OrderDetails) input)
        {
            var (hostNames, orderDetails) = input;

            var certificateName = hostNames[0].Replace("*", "wildcard").Replace(".", "-");

            byte[] csr;

            try
            {
                var request = await _keyVaultClient.CreateCertificateAsync(_options.VaultBaseUrl, certificateName, new CertificatePolicy
                {
                    X509CertificateProperties = new X509CertificateProperties
                    {
                        SubjectAlternativeNames = new SubjectAlternativeNames(dnsNames: hostNames)
                    }
                }, tags: new Dictionary<string, string>
                {
                    { "Issuer", "letsencrypt.org" }
                });

                csr = request.Csr;
            }
            catch (KeyVaultErrorException ex) when (ex.Response.StatusCode == HttpStatusCode.Conflict)
            {
                var base64Csr = await _keyVaultClient.GetPendingCertificateSigningRequestAsync(_options.VaultBaseUrl, certificateName);

                csr = Convert.FromBase64String(base64Csr);
            }

            var acmeProtocolClient = await _acmeProtocolClientFactory.CreateClientAsync();

            var finalize = await acmeProtocolClient.FinalizeOrderAsync(orderDetails.Payload.Finalize, csr);

            var httpClient = _httpClientFactory.CreateClient();

            var certificateData = await httpClient.GetByteArrayAsync(finalize.Payload.Certificate);

            var x509Certificates = new X509Certificate2Collection();

            x509Certificates.ImportFromPem(certificateData);

            await _keyVaultClient.MergeCertificateAsync(_options.VaultBaseUrl, certificateName, x509Certificates);
        }

        private static string ExtractResourceGroup(string resourceId)
        {
            var values = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);

            return values[3];
        }
    }
}
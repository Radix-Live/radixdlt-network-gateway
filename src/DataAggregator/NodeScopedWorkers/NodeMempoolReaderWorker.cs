/* Copyright 2021 Radix Publishing Ltd incorporated in Jersey (Channel Islands).
 *
 * Licensed under the Radix License, Version 1.0 (the "License"); you may not use this
 * file except in compliance with the License. You may obtain a copy of the License at:
 *
 * radixfoundation.org/licenses/LICENSE-v1
 *
 * The Licensor hereby grants permission for the Canonical version of the Work to be
 * published, distributed and used under or by reference to the Licensor’s trademark
 * Radix ® and use of any unregistered trade names, logos or get-up.
 *
 * The Licensor provides the Work (and each Contributor provides its Contributions) on an
 * "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied,
 * including, without limitation, any warranties or conditions of TITLE, NON-INFRINGEMENT,
 * MERCHANTABILITY, or FITNESS FOR A PARTICULAR PURPOSE.
 *
 * Whilst the Work is capable of being deployed, used and adopted (instantiated) to create
 * a distributed ledger it is your responsibility to test and validate the code, together
 * with all logic and performance of that code under all foreseeable scenarios.
 *
 * The Licensor does not make or purport to make and hereby excludes liability for all
 * and any representation, warranty or undertaking in any form whatsoever, whether express
 * or implied, to any entity or person, including any representation, warranty or
 * undertaking, as to the functionality security use, value or other characteristics of
 * any distributed ledger nor in respect the functioning or value of any tokens which may
 * be created stored or transferred using the Work. The Licensor does not warrant that the
 * Work or any use of the Work complies with any law or regulation in any territory where
 * it may be implemented or used or that it will be appropriate for any specific purpose.
 *
 * Neither the licensor nor any current or former employees, officers, directors, partners,
 * trustees, representatives, agents, advisors, contractors, or volunteers of the Licensor
 * shall be liable for any direct or indirect, special, incidental, consequential or other
 * losses of any kind, in tort, contract or otherwise (including but not limited to loss
 * of revenue, income or profits, or loss of use or data, or loss of reputation, or loss
 * of any economic or other opportunity of whatsoever nature or howsoever arising), arising
 * out of or in connection with (without limitation of any use, misuse, of any ledger system
 * or use made or its functionality or any performance or operation of any code or protocol
 * caused by bugs or programming or logic errors or otherwise);
 *
 * A. any offer, purchase, holding, use, sale, exchange or transmission of any
 * cryptographic keys, tokens or assets created, exchanged, stored or arising from any
 * interaction with the Work;
 *
 * B. any failure in a transmission or loss of any token or assets keys or other digital
 * artefacts due to errors in transmission;
 *
 * C. bugs, hacks, logic errors or faults in the Work or any communication;
 *
 * D. system software or apparatus including but not limited to losses caused by errors
 * in holding or transmitting tokens by any third-party;
 *
 * E. breaches or failure of security including hacker attacks, loss or disclosure of
 * password, loss of private key, unauthorised use or misuse of such passwords or keys;
 *
 * F. any losses including loss of anticipated savings or other benefits resulting from
 * use of the Work or any changes to the Work (however implemented).
 *
 * You are solely responsible for; testing, validating and evaluation of all operation
 * logic, functionality, security and appropriateness of using the Work for any commercial
 * or non-commercial purpose and for any reproduction or redistribution by You of the
 * Work. You assume all risks associated with Your use of the Work and the exercise of
 * permissions under this License.
 */

using Common.Extensions;
using Common.Utilities;
using DataAggregator.GlobalServices;
using DataAggregator.GlobalWorkers;
using DataAggregator.NodeScopedServices;
using DataAggregator.NodeScopedServices.ApiReaders;
using Prometheus;
using RadixCoreApi.Generated.Model;

namespace DataAggregator.NodeScopedWorkers;

/// <summary>
/// Responsible for syncing the mempool from a node.
/// </summary>
public class NodeMempoolReaderWorker : LoopedWorkerBase, INodeWorker
{
    private static readonly Gauge _mempoolSizeUnScoped = Metrics
        .CreateGauge(
            "node_mempool_size_total",
            "Current size of node mempool.",
            new GaugeConfiguration { LabelNames = new[] { "node" } }
        );

    private static readonly Counter _mempoolItemsAddedUnScoped = Metrics
        .CreateCounter(
            "node_mempool_added_count",
            "Transactions added to node mempool.",
            new CounterConfiguration { LabelNames = new[] { "node" } }
        );

    private static readonly Counter _mempoolItemsRemovedUnScoped = Metrics
        .CreateCounter(
            "node_mempool_removed_count",
            "Transactions removed from node mempool.",
            new CounterConfiguration { LabelNames = new[] { "node" } }
        );

    private readonly ILogger<NodeMempoolReaderWorker> _logger;
    private readonly IServiceProvider _services;
    private readonly INetworkConfigurationProvider _networkConfigurationProvider;
    private readonly IMempoolTrackerService _mempoolTrackerService;
    private readonly INodeConfigProvider _nodeConfig;
    private readonly Gauge.Child _mempoolSize;
    private readonly Counter.Child _mempoolItemsAdded;
    private readonly Counter.Child _mempoolItemsRemoved;

    private readonly Dictionary<byte[], TransactionData> _currentTransactions = new(ByteArrayEqualityComparer.Default);

    // NB - So that we can get new transient dependencies each iteration, we create most dependencies
    //      from the service provider.
    public NodeMempoolReaderWorker(
        ILogger<NodeMempoolReaderWorker> logger,
        IServiceProvider services,
        INetworkConfigurationProvider networkConfigurationProvider,
        IMempoolTrackerService mempoolTrackerService,
        INodeConfigProvider nodeConfig
    )
        : base(logger, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60))
    {
        _logger = logger;
        _services = services;
        _networkConfigurationProvider = networkConfigurationProvider;
        _mempoolTrackerService = mempoolTrackerService;
        _nodeConfig = nodeConfig;
        _mempoolSize = _mempoolSizeUnScoped.WithLabels(nodeConfig.NodeAppSettings.Name);
        _mempoolItemsAdded = _mempoolItemsAddedUnScoped.WithLabels(nodeConfig.NodeAppSettings.Name);
        _mempoolItemsRemoved = _mempoolItemsRemovedUnScoped.WithLabels(nodeConfig.NodeAppSettings.Name);
    }

    protected override async Task DoWork(CancellationToken stoppingToken)
    {
        await FetchAndShareMempoolTransactions(stoppingToken);
    }

    private async Task FetchAndShareMempoolTransactions(CancellationToken stoppingToken)
    {
        var coreApiProvider = _services.GetRequiredService<ICoreApiProvider>();

        var mempoolContents = await coreApiProvider.MempoolApi.MempoolPostAsync(
            new MempoolRequest(
                _networkConfigurationProvider.GetNetworkIdentifierForApiRequests()
            ),
            stoppingToken
        );

        _mempoolSize.Set(mempoolContents.TransactionIdentifiers.Count);

        var idsInMempool = mempoolContents.TransactionIdentifiers
            .Select(ti => ti.Hash.ConvertFromHex())
            .ToHashSet(ByteArrayEqualityComparer.Default);

        var transactionsToRemove = _currentTransactions.Keys
            .Except(idsInMempool)
            .ToHashSet(ByteArrayEqualityComparer.Default);

        foreach (var id in transactionsToRemove)
        {
            _currentTransactions.Remove(id);
        }

        _mempoolItemsRemoved.Inc(transactionsToRemove.Count);

        var transactionIdsToAdd = idsInMempool
            .Except(_currentTransactions.Keys)
            .ToHashSet(ByteArrayEqualityComparer.Default);

        var (transactionsToAdd, transactionFetchMs) = await CodeStopwatch.TimeInMs(
            async () => await FetchTransactions(coreApiProvider, transactionIdsToAdd, stoppingToken)
        );

        _mempoolItemsAdded.Inc(transactionsToAdd.Count);

        foreach (var transaction in transactionsToAdd)
        {
            _currentTransactions.Add(transaction.Id, transaction.TransactionData);
        }

        if (transactionsToAdd.Count > 0 || transactionsToRemove.Count > 0)
        {
            _logger.LogInformation(
                "Node mempool updated: {TransactionsAdded} transactions added and {TransactionsRemoved} transactions removed",
                transactionsToAdd.Count,
                transactionsToRemove.Count
            );
            _logger.LogInformation(
                "Node mempool transaction contents fetched in {TransactionFetchMs}ms",
                transactionFetchMs
            );
        }

        _mempoolTrackerService.RegisterNodeMempool(_nodeConfig.NodeAppSettings.Name, new NodeMempoolContents(_currentTransactions));
    }

    private async Task<List<TransactionDataWithId>> FetchTransactions(
        ICoreApiProvider coreApiProvider,
        HashSet<byte[]> transactionsToFetch,
        CancellationToken stoppingToken
    )
    {
        var list = new List<TransactionDataWithId>();

        // Fetch them sequentially instead of in parallel to try to avoid overloading the node
        foreach (var transactionId in transactionsToFetch)
        {
            var transactionData = await FetchTransaction(coreApiProvider, transactionId, 3, stoppingToken);
            if (transactionData != null)
            {
                list.Add(transactionData);
            }
        }

        return list;
    }

    private async Task<TransactionDataWithId?> FetchTransaction(
        ICoreApiProvider coreApiProvider,
        byte[] transactionId,
        int retryCount,
        CancellationToken stoppingToken
    )
    {
        for (int i = 0; i < retryCount; i++)
        {
            var result = await FetchTransaction(coreApiProvider, transactionId, stoppingToken);

            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    private async Task<TransactionDataWithId?> FetchTransaction(
        ICoreApiProvider coreApiProvider,
        byte[] transactionId,
        CancellationToken stoppingToken
    )
    {
        try
        {
            var response = await coreApiProvider.MempoolApi.MempoolTransactionPostAsync(
                new MempoolTransactionRequest(
                    _networkConfigurationProvider.GetNetworkIdentifierForApiRequests(),
                    new TransactionIdentifier(transactionId.ToHex())
                ),
                stoppingToken
            );

            return new TransactionDataWithId(
                transactionId,
                new TransactionData(
                    DateTime.UtcNow,
                    response.Transaction.Metadata.Hex.ConvertFromHex(),
                    response.Transaction
                )
            );
        }
        catch (Exception)
        {
            return null;
        }
    }
}

﻿using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using Stratis.Bitcoin.Features.RPC;
using Stratis.Bitcoin.Features.Wallet;
using Stratis.Bitcoin.Features.Wallet.Controllers;
using Stratis.Bitcoin.Features.Wallet.Interfaces;
using Stratis.Bitcoin.Features.Wallet.Models;
using Stratis.Bitcoin.IntegrationTests.Common;
using Stratis.Bitcoin.IntegrationTests.Common.EnvironmentMockUpHelpers;
using Xunit;

namespace Stratis.Bitcoin.IntegrationTests.Wallet
{
    public class WalletTests
    {
        [Fact]
        public void WalletCanReceiveAndSendCorrectly()
        {
            using (NodeBuilder builder = NodeBuilder.Create(this))
            {
                CoreNode stratisSender = builder.CreateStratisPowNode();
                CoreNode stratisReceiver = builder.CreateStratisPowNode();

                builder.StartAll();
                stratisSender.NotInIBD();
                stratisReceiver.NotInIBD();

                // get a key from the wallet
                Mnemonic mnemonic1 = stratisSender.FullNode.WalletManager().CreateWallet("123456", "mywallet");
                Mnemonic mnemonic2 = stratisReceiver.FullNode.WalletManager().CreateWallet("123456", "mywallet");
                Assert.Equal(12, mnemonic1.Words.Length);
                Assert.Equal(12, mnemonic2.Words.Length);
                HdAddress addr = stratisSender.FullNode.WalletManager().GetUnusedAddress(new WalletAccountReference("mywallet", "account 0"));
                Features.Wallet.Wallet wallet = stratisSender.FullNode.WalletManager().GetWalletByName("mywallet");
                Key key = wallet.GetExtendedPrivateKeyForAddress("123456", addr).PrivateKey;

                stratisSender.SetDummyMinerSecret(new BitcoinSecret(key, stratisSender.FullNode.Network));
                int maturity = (int)stratisSender.FullNode.Network.Consensus.CoinbaseMaturity;
                stratisSender.GenerateStratisWithMiner(maturity + 5);
                // wait for block repo for block sync to work

                TestHelper.WaitLoop(() => TestHelper.IsNodeSynced(stratisSender));

                // the mining should add coins to the wallet
                long total = stratisSender.FullNode.WalletManager().GetSpendableTransactionsInWallet("mywallet").Sum(s => s.Transaction.Amount);
                Assert.Equal(Money.COIN * 105 * 50, total);

                // sync both nodes
                stratisSender.CreateRPCClient().AddNode(stratisReceiver.Endpoint, true);
                TestHelper.WaitLoop(() => TestHelper.AreNodesSynced(stratisReceiver, stratisSender));

                // send coins to the receiver
                HdAddress sendto = stratisReceiver.FullNode.WalletManager().GetUnusedAddress(new WalletAccountReference("mywallet", "account 0"));
                Transaction trx = stratisSender.FullNode.WalletTransactionHandler().BuildTransaction(CreateContext(stratisSender.FullNode.Network,
                    new WalletAccountReference("mywallet", "account 0"), "123456", sendto.ScriptPubKey, Money.COIN * 100, FeeType.Medium, 101));

                // broadcast to the other node
                stratisSender.FullNode.NodeService<WalletController>().SendTransaction(new SendTransactionRequest(trx.ToHex()));

                // wait for the trx to arrive
                TestHelper.WaitLoop(() => stratisReceiver.CreateRPCClient().GetRawMempool().Length > 0);
                TestHelper.WaitLoop(() => stratisReceiver.FullNode.WalletManager().GetSpendableTransactionsInWallet("mywallet").Any());

                long receivetotal = stratisReceiver.FullNode.WalletManager().GetSpendableTransactionsInWallet("mywallet").Sum(s => s.Transaction.Amount);
                Assert.Equal(Money.COIN * 100, receivetotal);
                Assert.Null(stratisReceiver.FullNode.WalletManager().GetSpendableTransactionsInWallet("mywallet").First().Transaction.BlockHeight);

                // generate two new blocks do the trx is confirmed
                stratisSender.GenerateBlockManually(new List<Transaction>(new[] { stratisSender.FullNode.Network.CreateTransaction(trx.ToBytes()) }));
                stratisSender.GenerateStratisWithMiner(1);

                // wait for block repo for block sync to work
                TestHelper.WaitLoop(() => TestHelper.IsNodeSynced(stratisSender));
                TestHelper.WaitLoop(() => TestHelper.AreNodesSynced(stratisReceiver, stratisSender));

                TestHelper.WaitLoop(() => maturity + 6 == stratisReceiver.FullNode.WalletManager().GetSpendableTransactionsInWallet("mywallet").First().Transaction.BlockHeight);
            }
        }

        [Fact]
        public void CanMineAndSendToAddress()
        {
            using (NodeBuilder builder = NodeBuilder.Create(this))
            {
                CoreNode stratisNodeSync = builder.CreateStratisPowNode();
                builder.StartAll();

                // Move a wallet file to the right folder and restart the wallet manager to take it into account.
                this.InitializeTestWallet(stratisNodeSync.FullNode.DataFolder.WalletPath);
                var walletManager = stratisNodeSync.FullNode.NodeService<IWalletManager>() as WalletManager;
                walletManager.Start();

                RPCClient rpc = stratisNodeSync.CreateRPCClient();
                rpc.SendCommand(RPCOperations.generate, 10);
                Assert.Equal(10, rpc.GetBlockCount());

                BitcoinPubKeyAddress address = new Key().PubKey.GetAddress(rpc.Network);
                uint256 tx = rpc.SendToAddress(address, Money.Coins(1.0m));
                Assert.NotNull(tx);
            }
        }

        [Fact]
        public void WalletCanReorg()
        {
            // this test has 4 parts:
            // send first transaction from one wallet to another and wait for it to be confirmed
            // send a second transaction and wait for it to be confirmed
            // connected to a longer chain that couse a reorg back so the second trasnaction is undone
            // mine the second transaction back in to the main chain

            using (NodeBuilder builder = NodeBuilder.Create(this))
            {
                CoreNode stratisSender = builder.CreateStratisPowNode();
                CoreNode stratisReceiver = builder.CreateStratisPowNode();
                CoreNode stratisReorg = builder.CreateStratisPowNode();

                builder.StartAll();
                stratisSender.NotInIBD();
                stratisReceiver.NotInIBD();
                stratisReorg.NotInIBD();

                // get a key from the wallet
                Mnemonic mnemonic1 = stratisSender.FullNode.WalletManager().CreateWallet("123456", "mywallet");
                Mnemonic mnemonic2 = stratisReceiver.FullNode.WalletManager().CreateWallet("123456", "mywallet");
                Assert.Equal(12, mnemonic1.Words.Length);
                Assert.Equal(12, mnemonic2.Words.Length);
                HdAddress addr = stratisSender.FullNode.WalletManager().GetUnusedAddress(new WalletAccountReference("mywallet", "account 0"));
                Features.Wallet.Wallet wallet = stratisSender.FullNode.WalletManager().GetWalletByName("mywallet");
                Key key = wallet.GetExtendedPrivateKeyForAddress("123456", addr).PrivateKey;

                stratisSender.SetDummyMinerSecret(new BitcoinSecret(key, stratisSender.FullNode.Network));
                stratisReorg.SetDummyMinerSecret(new BitcoinSecret(key, stratisSender.FullNode.Network));

                int maturity = (int)stratisSender.FullNode.Network.Consensus.CoinbaseMaturity;
                stratisSender.GenerateStratisWithMiner(maturity + 15);

                int currentBestHeight = maturity + 15;

                // wait for block repo for block sync to work
                TestHelper.WaitLoop(() => TestHelper.IsNodeSynced(stratisSender));

                // the mining should add coins to the wallet
                long total = stratisSender.FullNode.WalletManager().GetSpendableTransactionsInWallet("mywallet").Sum(s => s.Transaction.Amount);
                Assert.Equal(Money.COIN * currentBestHeight * 50, total);

                // sync all nodes
                stratisReceiver.CreateRPCClient().AddNode(stratisSender.Endpoint, true);
                stratisReceiver.CreateRPCClient().AddNode(stratisReorg.Endpoint, true);
                stratisSender.CreateRPCClient().AddNode(stratisReorg.Endpoint, true);
                TestHelper.WaitLoop(() => TestHelper.AreNodesSynced(stratisReceiver, stratisSender));
                TestHelper.WaitLoop(() => TestHelper.AreNodesSynced(stratisReceiver, stratisReorg));

                // Build Transaction 1
                // ====================
                // send coins to the receiver
                HdAddress sendto = stratisReceiver.FullNode.WalletManager().GetUnusedAddress(new WalletAccountReference("mywallet", "account 0"));
                Transaction transaction1 = stratisSender.FullNode.WalletTransactionHandler().BuildTransaction(CreateContext(stratisSender.FullNode.Network, new WalletAccountReference("mywallet", "account 0"), "123456", sendto.ScriptPubKey, Money.COIN * 100, FeeType.Medium, 101));

                // broadcast to the other node
                stratisSender.FullNode.NodeService<WalletController>().SendTransaction(new SendTransactionRequest(transaction1.ToHex()));

                // wait for the trx to arrive
                TestHelper.WaitLoop(() => stratisReceiver.CreateRPCClient().GetRawMempool().Length > 0);
                Assert.NotNull(stratisReceiver.CreateRPCClient().GetRawTransaction(transaction1.GetHash(), false));
                TestHelper.WaitLoop(() => stratisReceiver.FullNode.WalletManager().GetSpendableTransactionsInWallet("mywallet").Any());

                long receivetotal = stratisReceiver.FullNode.WalletManager().GetSpendableTransactionsInWallet("mywallet").Sum(s => s.Transaction.Amount);
                Assert.Equal(Money.COIN * 100, receivetotal);
                Assert.Null(stratisReceiver.FullNode.WalletManager().GetSpendableTransactionsInWallet("mywallet").First().Transaction.BlockHeight);

                // generate two new blocks so the trx is confirmed
                stratisSender.GenerateStratisWithMiner(1);
                int transaction1MinedHeight = currentBestHeight + 1;
                stratisSender.GenerateStratisWithMiner(1);
                currentBestHeight = currentBestHeight + 2;

                // wait for block repo for block sync to work
                TestHelper.WaitLoop(() => TestHelper.IsNodeSynced(stratisSender));
                TestHelper.WaitLoop(() => TestHelper.AreNodesSynced(stratisReceiver, stratisSender));
                TestHelper.WaitLoop(() => TestHelper.AreNodesSynced(stratisReceiver, stratisReorg));
                Assert.Equal(currentBestHeight, stratisReceiver.FullNode.Chain.Tip.Height);
                TestHelper.WaitLoop(() => transaction1MinedHeight == stratisReceiver.FullNode.WalletManager().GetSpendableTransactionsInWallet("mywallet").First().Transaction.BlockHeight);

                // Build Transaction 2
                // ====================
                // remove the reorg node
                stratisReceiver.CreateRPCClient().RemoveNode(stratisReorg.Endpoint);
                stratisSender.CreateRPCClient().RemoveNode(stratisReorg.Endpoint);
                TestHelper.WaitLoop(() => !TestHelper.IsNodeConnected(stratisReorg));
                ChainedHeader forkblock = stratisReceiver.FullNode.Chain.Tip;

                // send more coins to the wallet
                sendto = stratisReceiver.FullNode.WalletManager().GetUnusedAddress(new WalletAccountReference("mywallet", "account 0"));
                Transaction transaction2 = stratisSender.FullNode.WalletTransactionHandler().BuildTransaction(CreateContext(stratisSender.FullNode.Network, new WalletAccountReference("mywallet", "account 0"), "123456", sendto.ScriptPubKey, Money.COIN * 10, FeeType.Medium, 101));
                stratisSender.FullNode.NodeService<WalletController>().SendTransaction(new SendTransactionRequest(transaction2.ToHex()));
                // wait for the trx to arrive
                TestHelper.WaitLoop(() => stratisReceiver.CreateRPCClient().GetRawMempool().Length > 0);
                Assert.NotNull(stratisReceiver.CreateRPCClient().GetRawTransaction(transaction2.GetHash(), false));
                TestHelper.WaitLoop(() => stratisReceiver.FullNode.WalletManager().GetSpendableTransactionsInWallet("mywallet").Any());
                long newamount = stratisReceiver.FullNode.WalletManager().GetSpendableTransactionsInWallet("mywallet").Sum(s => s.Transaction.Amount);
                Assert.Equal(Money.COIN * 110, newamount);
                Assert.Contains(stratisReceiver.FullNode.WalletManager().GetSpendableTransactionsInWallet("mywallet"), b => b.Transaction.BlockHeight == null);

                // mine more blocks so its included in the chain

                stratisSender.GenerateStratisWithMiner(1);
                int transaction2MinedHeight = currentBestHeight + 1;
                stratisSender.GenerateStratisWithMiner(1);
                currentBestHeight = currentBestHeight + 2;
                TestHelper.WaitLoop(() => TestHelper.IsNodeSynced(stratisSender));
                TestHelper.WaitLoop(() => TestHelper.AreNodesSynced(stratisReceiver, stratisSender));
                Assert.Equal(currentBestHeight, stratisReceiver.FullNode.Chain.Tip.Height);
                TestHelper.WaitLoop(() => stratisReceiver.FullNode.WalletManager().GetSpendableTransactionsInWallet("mywallet").Any(b => b.Transaction.BlockHeight == transaction2MinedHeight));

                // create a reorg by mining on two different chains
                // ================================================
                // advance both chains, one chin is longer
                stratisSender.GenerateStratisWithMiner(2);
                stratisReorg.GenerateStratisWithMiner(10);
                currentBestHeight = forkblock.Height + 10;
                TestHelper.WaitLoop(() => TestHelper.IsNodeSynced(stratisSender));
                TestHelper.WaitLoop(() => TestHelper.IsNodeSynced(stratisReorg));

                // connect the reorg chain
                stratisReceiver.CreateRPCClient().AddNode(stratisReorg.Endpoint, true);
                stratisSender.CreateRPCClient().AddNode(stratisReorg.Endpoint, true);
                // wait for the chains to catch up
                TestHelper.WaitLoop(() => TestHelper.AreNodesSynced(stratisReceiver, stratisSender));
                TestHelper.WaitLoop(() => TestHelper.AreNodesSynced(stratisReceiver, stratisReorg));
                Assert.Equal(currentBestHeight, stratisReceiver.FullNode.Chain.Tip.Height);

                // ensure wallet reorg complete
                TestHelper.WaitLoop(() => stratisReceiver.FullNode.WalletManager().WalletTipHash == stratisReorg.CreateRPCClient().GetBestBlockHash());
                // check the wallet amount was rolled back
                long newtotal = stratisReceiver.FullNode.WalletManager().GetSpendableTransactionsInWallet("mywallet").Sum(s => s.Transaction.Amount);
                Assert.Equal(receivetotal, newtotal);
                TestHelper.WaitLoop(() => maturity + 16 == stratisReceiver.FullNode.WalletManager().GetSpendableTransactionsInWallet("mywallet").First().Transaction.BlockHeight);

                // ReBuild Transaction 2
                // ====================
                // After the reorg transaction2 was returned back to mempool
                stratisSender.FullNode.NodeService<WalletController>().SendTransaction(new SendTransactionRequest(transaction2.ToHex()));

                TestHelper.WaitLoop(() => stratisReceiver.CreateRPCClient().GetRawMempool().Length > 0);
                // mine the transaction again
                stratisSender.GenerateStratisWithMiner(1);
                transaction2MinedHeight = currentBestHeight + 1;
                stratisSender.GenerateStratisWithMiner(1);
                currentBestHeight = currentBestHeight + 2;

                TestHelper.WaitLoop(() => TestHelper.IsNodeSynced(stratisSender));
                TestHelper.WaitLoop(() => TestHelper.AreNodesSynced(stratisReceiver, stratisSender));
                TestHelper.WaitLoop(() => TestHelper.AreNodesSynced(stratisReceiver, stratisReorg));
                Assert.Equal(currentBestHeight, stratisReceiver.FullNode.Chain.Tip.Height);
                long newsecondamount = stratisReceiver.FullNode.WalletManager().GetSpendableTransactionsInWallet("mywallet").Sum(s => s.Transaction.Amount);
                Assert.Equal(newamount, newsecondamount);
                TestHelper.WaitLoop(() => stratisReceiver.FullNode.WalletManager().GetSpendableTransactionsInWallet("mywallet").Any(b => b.Transaction.BlockHeight == transaction2MinedHeight));
            }
        }

        [Fact]
        public void Given_TheNodeHadAReorg_And_WalletTipIsBehindConsensusTip_When_ANewBlockArrives_Then_WalletCanRecover()
        {
            using (NodeBuilder builder = NodeBuilder.Create(this))
            {
                CoreNode stratisSender = builder.CreateStratisPowNode();
                CoreNode stratisReceiver = builder.CreateStratisPowNode();
                CoreNode stratisReorg = builder.CreateStratisPowNode();

                builder.StartAll();
                stratisSender.NotInIBD();
                stratisReceiver.NotInIBD();
                stratisReorg.NotInIBD();

                stratisSender.SetDummyMinerSecret(new BitcoinSecret(new Key(), stratisSender.FullNode.Network));
                stratisReorg.SetDummyMinerSecret(new BitcoinSecret(new Key(), stratisReorg.FullNode.Network));

                stratisSender.GenerateStratisWithMiner(10);

                // wait for block repo for block sync to work
                TestHelper.WaitLoop(() => TestHelper.IsNodeSynced(stratisSender));

                //// sync all nodes
                stratisReceiver.CreateRPCClient().AddNode(stratisSender.Endpoint, true);
                stratisReceiver.CreateRPCClient().AddNode(stratisReorg.Endpoint, true);
                stratisSender.CreateRPCClient().AddNode(stratisReorg.Endpoint, true);
                TestHelper.WaitLoop(() => TestHelper.AreNodesSynced(stratisReceiver, stratisSender));
                TestHelper.WaitLoop(() => TestHelper.AreNodesSynced(stratisReceiver, stratisReorg));

                // remove the reorg node
                stratisReceiver.CreateRPCClient().RemoveNode(stratisReorg.Endpoint);
                stratisSender.CreateRPCClient().RemoveNode(stratisReorg.Endpoint);
                TestHelper.WaitLoop(() => !TestHelper.IsNodeConnected(stratisReorg));

                // create a reorg by mining on two different chains
                // ================================================
                // advance both chains, one chin is longer
                stratisSender.GenerateStratisWithMiner(2);
                stratisReorg.GenerateStratisWithMiner(10);
                TestHelper.WaitLoop(() => TestHelper.IsNodeSynced(stratisSender));
                TestHelper.WaitLoop(() => TestHelper.IsNodeSynced(stratisReorg));

                // rewind the wallet in the stratisReceiver node
                (stratisReceiver.FullNode.NodeService<IWalletSyncManager>() as WalletSyncManager).SyncFromHeight(5);

                // connect the reorg chain
                stratisReceiver.CreateRPCClient().AddNode(stratisReorg.Endpoint, true);
                stratisSender.CreateRPCClient().AddNode(stratisReorg.Endpoint, true);
                // wait for the chains to catch up
                TestHelper.WaitLoop(() => TestHelper.AreNodesSynced(stratisReceiver, stratisSender));
                TestHelper.WaitLoop(() => TestHelper.AreNodesSynced(stratisReceiver, stratisReorg));
                Assert.Equal(20, stratisReceiver.FullNode.Chain.Tip.Height);

                stratisSender.GenerateStratisWithMiner(5);

                TestHelper.TriggerSync(stratisReceiver);
                TestHelper.TriggerSync(stratisSender);
                TestHelper.WaitLoop(() => TestHelper.AreNodesSynced(stratisReceiver, stratisSender));
                Assert.Equal(25, stratisReceiver.FullNode.Chain.Tip.Height);
            }
        }

        [Fact]
        public void Given_TheNodeHadAReorg_And_ConensusTipIsdifferentFromWalletTip_When_ANewBlockArrives_Then_WalletCanRecover()
        {
            using (NodeBuilder builder = NodeBuilder.Create(this))
            {
                CoreNode stratisSender = builder.CreateStratisPowNode();
                CoreNode stratisReceiver = builder.CreateStratisPowNode();
                CoreNode stratisReorg = builder.CreateStratisPowNode();

                builder.StartAll();
                stratisSender.NotInIBD();
                stratisReceiver.NotInIBD();
                stratisReorg.NotInIBD();

                stratisSender.SetDummyMinerSecret(new BitcoinSecret(new Key(), stratisSender.FullNode.Network));
                stratisReorg.SetDummyMinerSecret(new BitcoinSecret(new Key(), stratisReorg.FullNode.Network));

                stratisSender.GenerateStratisWithMiner(10);

                // wait for block repo for block sync to work
                TestHelper.WaitLoop(() => TestHelper.IsNodeSynced(stratisSender));

                //// sync all nodes
                stratisReceiver.CreateRPCClient().AddNode(stratisSender.Endpoint, true);
                stratisReceiver.CreateRPCClient().AddNode(stratisReorg.Endpoint, true);
                stratisSender.CreateRPCClient().AddNode(stratisReorg.Endpoint, true);
                TestHelper.WaitLoop(() => TestHelper.AreNodesSynced(stratisReceiver, stratisSender));
                TestHelper.WaitLoop(() => TestHelper.AreNodesSynced(stratisReceiver, stratisReorg));

                // remove the reorg node and wait for node to be disconnected
                stratisReceiver.CreateRPCClient().RemoveNodeAsync(stratisReorg.Endpoint).GetAwaiter().GetResult();
                stratisSender.CreateRPCClient().RemoveNodeAsync(stratisReorg.Endpoint).GetAwaiter().GetResult();
                TestHelper.WaitLoop(() => !TestHelper.IsNodeConnected(stratisReorg));

                // create a reorg by mining on two different chains
                // ================================================
                // advance both chains, one chin is longer
                stratisSender.GenerateStratisWithMiner(2);
                stratisReorg.GenerateStratisWithMiner(10);
                TestHelper.WaitLoop(() => TestHelper.IsNodeSynced(stratisSender));
                TestHelper.WaitLoop(() => TestHelper.IsNodeSynced(stratisReorg));

                // connect the reorg chain
                stratisReceiver.CreateRPCClient().AddNode(stratisReorg.Endpoint, true);
                stratisSender.CreateRPCClient().AddNode(stratisReorg.Endpoint, true);
                // wait for the chains to catch up
                TestHelper.WaitLoop(() => TestHelper.AreNodesSynced(stratisReceiver, stratisSender));
                TestHelper.WaitLoop(() => TestHelper.AreNodesSynced(stratisReceiver, stratisReorg));
                Assert.Equal(20, stratisReceiver.FullNode.Chain.Tip.Height);

                // rewind the wallet in the stratisReceiver node
                (stratisReceiver.FullNode.NodeService<IWalletSyncManager>() as WalletSyncManager).SyncFromHeight(10);

                stratisSender.GenerateStratisWithMiner(5);

                TestHelper.WaitLoop(() => TestHelper.AreNodesSynced(stratisReceiver, stratisSender));
                Assert.Equal(25, stratisReceiver.FullNode.Chain.Tip.Height);
            }
        }

        [Fact]
        public void WalletCanCatchupWithBestChain()
        {
            using (NodeBuilder builder = NodeBuilder.Create(this))
            {
                CoreNode stratisminer = builder.CreateStratisPowNode();

                builder.StartAll();
                stratisminer.NotInIBD();

                // get a key from the wallet
                Mnemonic mnemonic = stratisminer.FullNode.WalletManager().CreateWallet("123456", "mywallet");
                Assert.Equal(12, mnemonic.Words.Length);
                HdAddress addr = stratisminer.FullNode.WalletManager().GetUnusedAddress(new WalletAccountReference("mywallet", "account 0"));
                Features.Wallet.Wallet wallet = stratisminer.FullNode.WalletManager().GetWalletByName("mywallet");
                Key key = wallet.GetExtendedPrivateKeyForAddress("123456", addr).PrivateKey;

                stratisminer.SetDummyMinerSecret(key.GetBitcoinSecret(stratisminer.FullNode.Network));
                stratisminer.GenerateStratisWithMiner(10);
                // wait for block repo for block sync to work
                TestHelper.WaitLoop(() => TestHelper.IsNodeSynced(stratisminer));

                // push the wallet back
                stratisminer.FullNode.Services.ServiceProvider.GetService<IWalletSyncManager>().SyncFromHeight(5);

                stratisminer.GenerateStratisWithMiner(5);

                TestHelper.WaitLoop(() => TestHelper.IsNodeSynced(stratisminer));
            }
        }

        [Fact]
        public void WalletCanRecoverOnStartup()
        {
            using (NodeBuilder builder = NodeBuilder.Create(this))
            {
                CoreNode stratisNodeSync = builder.CreateStratisPowNode();
                builder.StartAll();
                stratisNodeSync.NotInIBD();

                // get a key from the wallet
                Mnemonic mnemonic = stratisNodeSync.FullNode.WalletManager().CreateWallet("123456", "mywallet");
                Assert.Equal(12, mnemonic.Words.Length);
                HdAddress addr = stratisNodeSync.FullNode.WalletManager().GetUnusedAddress(new WalletAccountReference("mywallet", "account 0"));
                Features.Wallet.Wallet wallet = stratisNodeSync.FullNode.WalletManager().GetWalletByName("mywallet");
                Key key = wallet.GetExtendedPrivateKeyForAddress("123456", addr).PrivateKey;

                stratisNodeSync.SetDummyMinerSecret(key.GetBitcoinSecret(stratisNodeSync.FullNode.Network));
                stratisNodeSync.GenerateStratisWithMiner(10);
                TestHelper.WaitLoop(() => TestHelper.IsNodeSynced(stratisNodeSync));

                // set the tip of best chain some blocks in the apst
                stratisNodeSync.FullNode.Chain.SetTip(stratisNodeSync.FullNode.Chain.GetBlock(stratisNodeSync.FullNode.Chain.Height - 5));

                // stop the node it will persist the chain with the reset tip
                stratisNodeSync.FullNode.Dispose();

                CoreNode newNodeInstance = builder.CloneStratisNode(stratisNodeSync);

                // load the node, this should hit the block store recover code
                newNodeInstance.Start();

                // check that store recovered to be the same as the best chain.
                Assert.Equal(newNodeInstance.FullNode.Chain.Tip.HashBlock, newNodeInstance.FullNode.WalletManager().WalletTipHash);
            }
        }

        public static TransactionBuildContext CreateContext(Network network, WalletAccountReference accountReference, string password,
            Script destinationScript, Money amount, FeeType feeType, int minConfirmations)
        {
            return new TransactionBuildContext(
                network,
                accountReference,
                new[] { new Recipient { Amount = amount, ScriptPubKey = destinationScript } }.ToList(), password)
            {
                MinConfirmations = minConfirmations,
                FeeType = feeType
            };
        }

        /// <summary>
        /// Copies the test wallet into data folder for node if it isnt' already present.
        /// </summary>
        /// <param name="path">The path of the folder to move the wallet to.</param>
        private void InitializeTestWallet(string path)
        {
            string testWalletPath = Path.Combine(path, "test.wallet.json");
            if (!File.Exists(testWalletPath))
                File.Copy("Data/test.wallet.json", testWalletPath);
        }
    }
}
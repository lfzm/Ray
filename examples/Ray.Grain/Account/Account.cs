﻿using Orleans.Concurrency;
using Ray.Core;
using Ray.Core.Event;
using Ray.DistributedTransaction;
using Ray.EventBus.RabbitMQ;
using Ray.Grain.Events;
using Ray.IGrains.Actors;
using Ray.IGrains.States;
using System.Threading.Tasks;

namespace Ray.Grain
{
    [Reentrant, Observable, Producer]
    public sealed class Account : DistributedTxGrain<long, AccountState>, IAccount
    {
        public Task Transfer(long toAccountId, decimal amount)
        {
            var evt = new AmountTransferEvent(toAccountId, amount, Snapshot.State.Balance - amount);
            return RaiseEvent(evt);
        }
        public async Task<bool> TransferDeduct(decimal amount, long transactionId)
        {
            if (Snapshot.State.Balance > amount)
            {
                await TxRaiseEvent(transactionId, new AmountDeductEvent(amount, Snapshot.State.Balance - amount));
                return true;
            }
            else
            {
                return false;
            }
        }
        public async Task TransferAddAmount(decimal amount, long transactionId)
        {
            await TxRaiseEvent(transactionId, new AmountAddEvent(amount, Snapshot.State.Balance + amount));
        }
        public Task<bool> AddAmount(decimal amount, EventUID uniqueId = null)
        {
            return ConcurrentRaiseEvent((snapshot, func) =>
           {
               var evt = new AmountAddEvent(amount, snapshot.State.Balance + amount);
               return func(evt, uniqueId);
           });
        }
        public Task<decimal> GetBalance()
        {
            return Task.FromResult(Snapshot.State.Balance);
        }
    }
}

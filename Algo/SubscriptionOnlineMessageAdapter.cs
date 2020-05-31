﻿namespace StockSharp.Algo
{
	using System;
	using System.Collections.Generic;

	using Ecng.Common;
	using Ecng.Collections;

	using StockSharp.Messages;
	using StockSharp.Localization;
	using StockSharp.Logging;

	/// <summary>
	/// Online subscription counter adapter.
	/// </summary>
	public class SubscriptionOnlineMessageAdapter : MessageAdapterWrapper
	{
		private class SubscriptionInfo
		{
			public ISubscriptionMessage Subscription { get; }

			public SubscriptionInfo(ISubscriptionMessage subscription)
			{
				Subscription = subscription ?? throw new ArgumentNullException(nameof(subscription));
			}

			public SubscriptionStates State { get; set; } = SubscriptionStates.Stopped;

			public readonly CachedSynchronizedSet<long> Subscribers = new CachedSynchronizedSet<long>();

			public override string ToString() => Subscription.ToString();
		}

		private readonly SyncObject _sync = new SyncObject();

		private readonly PairSet<Tuple<DataType, SecurityId>, SubscriptionInfo> _subscriptionsByKey = new PairSet<Tuple<DataType, SecurityId>, SubscriptionInfo>();
		private readonly Dictionary<long, SubscriptionInfo> _subscriptionsById = new Dictionary<long, SubscriptionInfo>();
		
		/// <summary>
		/// Initializes a new instance of the <see cref="SubscriptionOnlineMessageAdapter"/>.
		/// </summary>
		/// <param name="innerAdapter">Inner message adapter.</param>
		public SubscriptionOnlineMessageAdapter(IMessageAdapter innerAdapter)
			: base(innerAdapter)
		{
		}

		/// <inheritdoc />
		protected override bool OnSendInMessage(Message message)
		{
			void TryAddOrderSubscription(OrderMessage orderMsg)
			{
				lock (_sync)
				{
					if (_subscriptionsByKey.TryGetValue(Tuple.Create(DataType.Transactions, default(SecurityId)), out var info))
						TryAddOrderTransaction(info, orderMsg.TransactionId);

					//if (_subscriptionsByKey.TryGetValue(Tuple.Create(DataType.Transactions, orderMsg.SecurityId), out info))
					//	TryAddOrderTransaction(info, orderMsg.TransactionId);
				}
			}

			switch (message.Type)
			{
				case MessageTypes.Reset:
					return ProcessReset(message);

				case MessageTypes.OrderRegister:
				case MessageTypes.OrderReplace:
				case MessageTypes.OrderCancel:
				case MessageTypes.OrderGroupCancel:
				{
					var orderMsg = (OrderMessage)message;

					TryAddOrderSubscription(orderMsg);

					return base.OnSendInMessage(message);
				}

				case MessageTypes.OrderPairReplace:
				{
					var pairMsg = (OrderPairReplaceMessage)message;

					TryAddOrderSubscription(pairMsg.Message1);
					TryAddOrderSubscription(pairMsg.Message2);

					return base.OnSendInMessage(message);
				}

				case MessageTypes.OrderStatus:
				{
					var statusMsg = (OrderStatusMessage)message;

					if (statusMsg.HasOrderId())
						return base.OnSendInMessage(message);

					return ProcessInSubscriptionMessage(statusMsg);
				}

				default:
				{
					if (message is ISubscriptionMessage subscrMsg)
						return ProcessInSubscriptionMessage(subscrMsg);
					else
						return base.OnSendInMessage(message);
				}
			}
		}

		private void ChangeState(SubscriptionInfo info, SubscriptionStates state)
		{
			var id = info.Subscription.TransactionId;

			const string text = "Subscription {0} {1}->{2}.";

			if (info.State.IsOk(state))
				this.AddInfoLog(text, id, info.State, state);
			else
				this.AddWarningLog(text, id, info.State, state);

			info.State = state;

			if (!state.IsActive())
			{
				_subscriptionsByKey.RemoveByValue(info);
				this.AddInfoLog(LocalizedStrings.OnlineSubscriptionRemoved, id);
			}
		}

		/// <inheritdoc />
		protected override void OnInnerAdapterNewOutMessage(Message message)
		{
			switch (message.Type)
			{
				case MessageTypes.Disconnect:
				case ExtendedMessageTypes.ReconnectingFinished:
				{
					ClearState();
					break;
				}

				case MessageTypes.SubscriptionResponse:
				{
					var responseMsg = (SubscriptionResponseMessage)message;

					HashSet<long> subscribers = null;

					lock (_sync)
					{
						if (responseMsg.IsOk())
						{
							if (_subscriptionsById.TryGetValue(responseMsg.OriginalTransactionId, out var info))
							{
								ChangeState(info, SubscriptionStates.Active);
							}
						}
						else
						{
							if (_subscriptionsById.TryGetAndRemove(responseMsg.OriginalTransactionId, out var info))
							{
								ChangeState(info, SubscriptionStates.Error);
							}
						}
					}

					if (subscribers != null)
					{
						foreach (var subscriber in subscribers)
						{
							this.AddInfoLog(LocalizedStrings.SubscriptionNotifySubscriber, responseMsg.OriginalTransactionId, subscriber);
							base.OnInnerAdapterNewOutMessage(subscriber.CreateSubscriptionResponse(responseMsg.Error));
						}
					}

					break;
				}

				case MessageTypes.SubscriptionOnline:
				{
					lock (_sync)
					{
						if (_subscriptionsById.TryGetValue(((SubscriptionOnlineMessage)message).OriginalTransactionId, out var info))
							ChangeState(info, SubscriptionStates.Online);
					}

					break;
				}

				case MessageTypes.SubscriptionFinished:
				{
					lock (_sync)
					{
						if (_subscriptionsById.TryGetValue(((SubscriptionFinishedMessage)message).OriginalTransactionId, out var info))
							ChangeState(info, SubscriptionStates.Finished);
					}
					
					break;
				}

				default:
				{
					if (message is ISubscriptionIdMessage subscrMsg)
					{
						lock (_sync)
						{
							if (subscrMsg.OriginalTransactionId != 0 && _subscriptionsById.TryGetValue(subscrMsg.OriginalTransactionId, out var info))
							{
								if (message is ExecutionMessage execMsg &&
									execMsg.ExecutionType == ExecutionTypes.Transaction &&
									execMsg.TransactionId != 0 &&
									info.Subscription.DataType == DataType.Transactions)
								{
									TryAddOrderTransaction(info, execMsg.TransactionId);
								}
							}
							else
							{
								var dataType = subscrMsg.DataType;
								var secId = (subscrMsg as ISecurityIdMessage)?.SecurityId ?? default;

								if (!_subscriptionsByKey.TryGetValue(Tuple.Create(dataType, secId), out info) && (secId == default || !_subscriptionsByKey.TryGetValue(Tuple.Create(dataType, default(SecurityId)), out info)))
									break;
							}

							subscrMsg.SetSubscriptionIds(info.Subscribers.Cache);
						}
					}

					break;
				}
			}

			base.OnInnerAdapterNewOutMessage(message);
		}

		private void TryAddOrderTransaction(SubscriptionInfo statusInfo, long transactionId)
		{
			if (/*statusInfo.Subscribers.TryAdd(transactionId) && */!_subscriptionsById.ContainsKey(transactionId))
			{
				var orderSubscription = new SubscriptionInfo(statusInfo.Subscription.TypedClone());

				//orderSubscription.Subscribers.Add(transactionId);
				orderSubscription.Subscribers.Add(statusInfo.Subscription.TransactionId);

				_subscriptionsById.Add(transactionId, orderSubscription);
			}
			else
				this.AddWarningLog("Order's transaction {0} was handled before.", transactionId);
		}

		private void ClearState()
		{
			lock (_sync)
			{
				_subscriptionsByKey.Clear();
				_subscriptionsById.Clear();
			}
		}

		private bool ProcessReset(Message message)
		{
			ClearState();

			return base.OnSendInMessage(message);
		}

		private bool ProcessInSubscriptionMessage(ISubscriptionMessage message)
		{
			if (message == null)
				throw new ArgumentNullException(nameof(message));

			var transId = message.TransactionId;

			var isSubscribe = message.IsSubscribe;

			ISubscriptionMessage sendInMsg = null;
			Message[] sendOutMsgs = null;

			lock (_sync)
			{
				if (isSubscribe)
				{
					if (message.To == null)
					{
						var dataType = message.DataType;
						var secId = default(SecurityId);

						if (message is ISecurityIdMessage secIdMsg)
						{
							secId = secIdMsg.SecurityId;

							if (secId == default && IsSecurityRequired(dataType))
								this.AddWarningLog("Subscription {0} required security id.", dataType);
							else if (secId != default && !IsSecurityRequired(dataType))
								this.AddWarningLog("Subscription {0} doesn't required security id.", dataType);
						}

						var key = Tuple.Create(dataType, secId);

						if (!_subscriptionsByKey.TryGetValue(key, out var info))
						{
							sendInMsg = message;

							info = new SubscriptionInfo(message.TypedClone());
						
							_subscriptionsByKey.Add(key, info);
						}
						else
						{
							var resultMsg = message.CreateResult();

							if (message.Type == MessageTypes.MarketData)
							{
								sendOutMsgs = new[]
								{
									message.CreateResponse(),
									resultMsg,
								};
							}
							else
							{
								sendOutMsgs = new[] { resultMsg };
							}
						}

						_subscriptionsById.Add(transId, info);
						info.Subscribers.Add(transId);
					}
					else
						sendInMsg = message;
				}
				else
				{
					ISubscriptionMessage MakeUnsubscribe(ISubscriptionMessage m, long subscriptionId)
					{
						m.IsSubscribe = false;
						m.TransactionId = transId;
						m.OriginalTransactionId = subscriptionId;

						return m;
					}

					var originId = message.OriginalTransactionId;

					if (_subscriptionsById.TryGetValue(originId, out var info))
					{
						if (!info.Subscribers.Remove(originId))
						{
							sendOutMsgs = new[]
							{
								(Message)originId.CreateSubscriptionResponse(new InvalidOperationException(LocalizedStrings.SubscriptionNonExist.Put(originId)))
							};
						}
						else
						{
							if (info.Subscribers.Count == 0)
							{
								_subscriptionsByKey.RemoveByValue(info);
								_subscriptionsById.Remove(originId);

								if (info.State.IsActive())
								{
									// copy full subscription's details into unsubscribe request
									sendInMsg = MakeUnsubscribe(info.Subscription.TypedClone(), info.Subscription.TransactionId);
								}
								else
									this.AddWarningLog(LocalizedStrings.SubscriptionInState, originId, info.State);
							}
							else
							{
								sendOutMsgs = new[] { message.CreateResult() };
							}
						}
					}
					else
					{
						sendOutMsgs = new[]
						{
							(Message)originId.CreateSubscriptionResponse(new InvalidOperationException(LocalizedStrings.SubscriptionNonExist.Put(originId)))
						};
					}
				}
			}

			var retVal = true;

			if (sendInMsg != null)
			{
				this.AddInfoLog("In: {0}", sendInMsg);
				retVal = base.OnSendInMessage((Message)sendInMsg);
			}

			if (sendOutMsgs != null)
			{
				foreach (var sendOutMsg in sendOutMsgs)
				{
					this.AddInfoLog("Out: {0}", sendOutMsg);
					RaiseNewOutMessage(sendOutMsg);	
				}
			}

			return retVal;
		}

		/// <summary>
		/// Create a copy of <see cref="SubscriptionOnlineMessageAdapter"/>.
		/// </summary>
		/// <returns>Copy.</returns>
		public override IMessageChannel Clone()
		{
			return new SubscriptionOnlineMessageAdapter(InnerAdapter.TypedClone());
		}
	}
}
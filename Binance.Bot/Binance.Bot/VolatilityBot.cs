using System;
using Binance.Net;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using Binance.Net.Objects.Spot.MarketStream;
using CryptoExchange.Net;
using CryptoExchange.Net.Sockets;
using Microsoft.Extensions.Logging;
using ILogger = Serilog.ILogger;

namespace Binance.Bot
{
    public class VolatilityBot:IBot
    {
        private BinanceSocketClient _socketClient;
        private BinanceClient _client;
        private  ILogger<VolatilityBot> _logger;
        private BotSetting _botSetting;
        private RollingStack<IBinanceStreamKline> _stack;
        private decimal _currentPrice;
        private DateTime _dontTradeTill;
        private object _lock = new object();
        private Trades _trades;
        private Action _newTradeCallback;

        public VolatilityBot(BinanceSocketClient socketClient, BinanceClient client, 
            ILogger<VolatilityBot> logger, BotSetting botSetting, Action tradeCallback)
        {
            _socketClient = socketClient;
            _client = client;
            _logger = logger;
            _botSetting = botSetting;
            _newTradeCallback = tradeCallback;
            _stack = new RollingStack<IBinanceStreamKline>(_botSetting.TimeSpan);
            _trades = new Trades();
            _logger.LogInformation($"Starting: {this.GetType().Name} on Pair {_botSetting.Symbol} " +
                                   $"setting: Timespan: {botSetting.TimeSpan} ChangeInPrice: {botSetting.ChangeInPrice}");
        }

        public void SubscribeToData()
        {
            var subscribeResult = _socketClient.Spot.SubscribeToKlineUpdatesAsync(_botSetting.Symbol,KlineInterval.OneMinute, OnKlineUpdate);
            
            if (!subscribeResult.Result.Success)
            {
                _logger.LogError(subscribeResult.Result.Error.Message);
            }
            
            var subscribeResultPrice = _socketClient.Spot.SubscribeToTradeUpdatesAsync(_botSetting.Symbol, OnTradeUpdate);
            
            if (!subscribeResultPrice.Result.Success)
            {
                _logger.LogError(subscribeResultPrice.Result.Error.Message);
            }
        }

        private void OnKlineUpdate(DataEvent<IBinanceStreamKlineData> data)
        {
            var actualData = data.Data.Data;
            if (actualData.Final)
            {
                _stack.Push(actualData);
                _logger.LogDebug($"{_botSetting.Symbol} close{actualData.Close.ToString()} time: {actualData.CloseTime} ");
            }
        }

        private void OnTradeUpdate(DataEvent<BinanceStreamTrade> trade)
        {
            if (_dontTradeTill != null && _dontTradeTill > DateTime.Now)
            {
                return;
            }
            
            _currentPrice = trade.Data.Price;
            var action = CheckpriceDifference(_currentPrice);
            
            if(action==ActionAdvice.Buy)
            {
                ExceCuteOrder(OrderSide.Buy);    
            }
            else if (action == ActionAdvice.Sell)
            {
                ExceCuteOrder(OrderSide.Sell);
            }
        }

        private void ExceCuteOrder(OrderSide type)
        {
            lock (_lock)
            {
                if (_dontTradeTill != null && _dontTradeTill > DateTime.Now)
                {
                    return;
                }
                
                var quantity = _botSetting.QuantityInDollar/_currentPrice;

                var callResult = _client.Spot.Order.PlaceOrderAsync
                    (_botSetting.Symbol, type, OrderType.Market, quantity: quantity).GetAwaiter().GetResult();

                if(callResult.Success)
                {
                    var actualData=callResult.Data;
                    
                    if(type == OrderSide.Buy)
                        _trades.AddBuy(actualData.Price);
                    else if(type==OrderSide.Sell)
                        _trades.AddSell(actualData.Price);
                    
                    _logger.LogInformation($"{(type == OrderSide.Buy?"Bought":"Sold")}: {actualData.Quantity} of {actualData.Symbol} at {actualData.Price} price");
                    _dontTradeTill = DateTime.Now.AddMinutes(_botSetting.TimeSpan);
                    _logger.LogInformation($"Cant trade till {_dontTradeTill}");
                    _newTradeCallback();
                }
                else
                {
                    _logger.LogError(callResult.Error.ToString());
                    if (callResult.Error.Code == -2010)
                    {
                        _dontTradeTill = DateTime.Now.AddMinutes(_botSetting.TimeSpan);
                        _logger.LogInformation($"Cant trade till {_dontTradeTill}");
                    }
                }
            }
        }
        
        public void ShowAverageBuySell()
        {
            _logger.LogInformation($"Symbol: {_botSetting.Symbol} Average buy: {_trades.BuyAverage} trades: {_trades.TotalTradesBuy} " +
                                   $"average sell: {_trades.SellAverage} trades: {_trades.TotalTradesSell} ");
        }

        private ActionAdvice CheckpriceDifference(decimal price)
        {
            if (_stack.LookUp(_botSetting.TimeSpan) == null)
                return ActionAdvice.None;
            
            var prevPrice = _stack.LookUp(_botSetting.TimeSpan).Close;
            var priceChange = ((price - prevPrice) / prevPrice) * 100;
            _logger.LogDebug($"Symbol: {_botSetting.Symbol} Price Change: {prevPrice} > {price} percentage: {priceChange}");

            if (Math.Abs(priceChange) > _botSetting.ChangeInPrice)
            {
                _logger.LogInformation($"Price Change: {prevPrice} > {price} percentage: {priceChange}");

                if (priceChange > 0)
                    return ActionAdvice.Sell;
                else
                    return ActionAdvice.Buy;
            }

            return ActionAdvice.None;
        }
    }
}
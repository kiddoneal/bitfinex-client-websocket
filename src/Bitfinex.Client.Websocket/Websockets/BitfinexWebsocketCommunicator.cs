using System;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Bitfinex.Client.Websocket.Validations;
using Serilog;

namespace Bitfinex.Client.Websocket.Websockets
{
    public class BitfinexWebsocketCommunicator : IDisposable
    {
        private readonly Uri _url;
        private Timer _lastChanceTimer;
        private Timer _emitOpenedTimer;
        private readonly Func<ClientWebSocket> _clientFactory;

        private DateTime _lastReceivedMsg = DateTime.UtcNow; 

        private bool _disposing = false;
        private ClientWebSocket _client;

        private Task _sendingTask = null;
        private object _sendingTaskLock = new object();

        public WebSocketState State => _client.State;

        private CancellationTokenSource _cancelation;

        private readonly Subject<string> _messageReceivedSubject = new Subject<string>();
        private readonly Subject<byte[]> _dataReceivedSubject = new Subject<byte[]>();
        private readonly Subject<string> _openedSubject = new Subject<string>();
        private readonly Subject<string> _closedSubject = new Subject<string>();
        private readonly Subject<Exception> _errorSubject = new Subject<Exception>();


        public BitfinexWebsocketCommunicator(Uri url, Func<ClientWebSocket> clientFactory = null)
        {
            BfxValidations.ValidateInput(url, nameof(url));

            _url = url;
            _clientFactory = clientFactory ?? (() => new ClientWebSocket()
            {
                Options = {KeepAliveInterval = new TimeSpan(0, 0, 5, 0)}
            });

            _emitOpenedTimer = new Timer(EmitOpenSignal, null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>
        /// Stream with raw received message
        /// </summary>
        public IObservable<string> MessageReceived => _messageReceivedSubject.AsObservable();
        /// <summary>
        /// Stream with byte received message
        /// </summary>
        public IObservable<byte[]> DataReceived => _dataReceivedSubject.AsObservable();
        /// <summary>
        /// Stream with open
        /// </summary>
        public IObservable<string> Opened => _openedSubject.AsObservable();
        /// <summary>
        /// Stream with close
        /// </summary>
        public IObservable<string> Closed => _closedSubject.AsObservable();
        /// <summary>
        /// Stream with error
        /// </summary>
        public IObservable<Exception> Error => _errorSubject.AsObservable();

        /// <summary>
        /// Time range in ms, how long to wait before reconnecting if no message comes from server.
        /// Default 60000 ms (1 minute)
        /// </summary>
        public int ReconnectTimeoutMs { get; set; } = 30 * 1000;

        /// <summary>
        /// Time range in ms, how long to wait before reconnecting if last reconnection failed.
        /// Default 60000 ms (1 minute)
        /// </summary>
        public int ErrorReconnectTimeoutMs { get; set; } = 60 * 1000;

        public void Dispose()
        {
            _disposing = true;
            Log.Debug(L("Disposing.."));
            _lastChanceTimer?.Dispose();
            _cancelation?.Cancel();
            _client?.Abort();
            _client?.Dispose();
            _cancelation?.Dispose();
        }

        public Task Start()
        {
            Log.Debug(L("Start..."));
            _cancelation = new CancellationTokenSource();

            return StartClient(_url, _cancelation.Token);
        }

        public Task Send(string message)
        {
            BfxValidations.ValidateInput(message, nameof(message));

            Log.Debug(L($"Sending:  {message}"));
            var buffer = Encoding.UTF8.GetBytes(message);
            var messageSegment = new ArraySegment<byte>(buffer);
            lock (_sendingTaskLock)
            {
                var client = GetClient();
                if (client == null)
                {
                    return null;
                }
                try
                {
                    if (_sendingTask != null
                        && _sendingTask.Status != TaskStatus.RanToCompletion
                        && _sendingTask.Status != TaskStatus.Canceled
                        && _sendingTask.Status != TaskStatus.Faulted)
                    {
                        Log.Warning(L($"Waiting SendAsync completed."));
                        _sendingTask.Wait();
                    }
                    _sendingTask = client.SendAsync(messageSegment, WebSocketMessageType.Text, true, _cancelation.Token);
                }
                catch (Exception e)
                {
                    Log.Error(e, L($"Exception while SendAsync."));
#pragma warning disable 4014
                    Reconnect();
#pragma warning restore 4014
                }
            }

            return null;
        }

        public void EmitOpenSignal(object state)
        {
            _openedSubject.OnNext("websocket opened");
        }

        private async Task StartClient(Uri uri, CancellationToken token)
        {
            DeactiveLastChance();
            _client = _clientFactory();
            
            try
            {
                await _client.ConnectAsync(uri, token);

                if (_client.State == WebSocketState.Open)
                {
                    //_reconnecting = false;
                    Log.Information(L($"StartClient: websocket connected: state {_client.State}"));
                    _emitOpenedTimer.Change(0, Timeout.Infinite);
                }

#pragma warning disable 4014
                Listen(_client, token);
#pragma warning restore 4014
                ActivateLastChance();
            }
            catch (SocketException e)
            {
                Log.Error(e, L("Exception while connecting. " +
                               $"Waiting {ErrorReconnectTimeoutMs / 1000} sec before next reconnection try."));

                _errorSubject.OnNext(e);
                await Task.Delay(ErrorReconnectTimeoutMs, token);
                await Reconnect();
            }
            catch (Exception e)
            {
                Log.Error(e, L("Exception while connecting. " +
                               $"Waiting {ErrorReconnectTimeoutMs/1000} sec before next reconnection try."));

                _errorSubject.OnNext(e);
                await Task.Delay(ErrorReconnectTimeoutMs, token);
                await Reconnect();
            }
            
        }

        private ClientWebSocket GetClient()
        {
            if (_client == null || 
                (_client.State != WebSocketState.Open))
            {
                Log.Error(L($"GetClient error, State[{_client.State}] CloseStatus[{_client.CloseStatus} : {_client.CloseStatusDescription}]"));

                //if (_reconnecting)
                //{
                //    Log.Debug(L("It's already reconnecting, return!"));
                //}

                return null;
                //await Reconnect();
            }
            return _client;
        }

        private async Task Reconnect()
        {
            if (_disposing)
                return;

            //_reconnecting = true;

            Log.Warning(L("Reconnecting..."));
            _cancelation.Cancel();
            await Task.Delay(1000);

            _cancelation = new CancellationTokenSource();
            await StartClient(_url, _cancelation.Token);
        }

        private async Task Listen(ClientWebSocket client, CancellationToken token)
        {
            ArraySegment<Byte> buffer = new ArraySegment<byte>(new Byte[8192]);
            WebSocketReceiveResult data = null;
            while (client.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                using (var ms = new MemoryStream())
                {
                    do
                    {
                        data = await client.ReceiveAsync(buffer, CancellationToken.None);
                        ms.Write(buffer.Array, buffer.Offset, data.Count);
                    }
                    while (!data.EndOfMessage);

                    ms.Seek(0, SeekOrigin.Begin);
                    if (data.MessageType == WebSocketMessageType.Text)
                    {
                        using (var reader = new StreamReader(ms, Encoding.UTF8))
                        {
                            var received = reader.ReadToEnd();
                            Log.Verbose(L($"Received: [size: {ms.Length}] {received}"));
                            _lastReceivedMsg = DateTime.UtcNow;
                            _messageReceivedSubject.OnNext(received);
                        }
                    }
                    else if (data.MessageType == WebSocketMessageType.Binary)
                    {
                        var received = new byte[ms.Length];
                        ms.Read(received, 0, (int)ms.Length);
                        ms.Seek(0, SeekOrigin.Begin);
                        Log.Verbose(L($"Received: [size: {ms.Length}] Binary data"));
                        _lastReceivedMsg = DateTime.UtcNow;
                        _dataReceivedSubject.OnNext(received);
                    }
                    else if (data.MessageType == WebSocketMessageType.Close)
                    {
                        Log.Error(L($"Closing ... reason {client.CloseStatusDescription}"));
                        var description = client.CloseStatusDescription;
                        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                        _closedSubject.OnNext($"Websocket server closed: {description}");
                        break;
                    }
                    else
                    {
                        Log.Error(L($"Listen NotSupportedException: meesageType:{data.MessageType} state:{client.State}."));
                        throw new NotSupportedException();
                    }
                }
            }
            
            Log.Error(L($"exit listening: websocket state: {client.State}."));
        }

        private void ActivateLastChance()
        {
            var timerMs = 1000 * 5;
            try
            {
                _lastChanceTimer = new Timer(async x => await LastChance(x), null, timerMs, timerMs);
            }
            catch (TaskCanceledException e)
            {
                Log.Error(L($"ActivateLastChance error: {e}"));
            }
        }

        private void DeactiveLastChance()
        {
            _lastChanceTimer?.Dispose();
            _lastChanceTimer = null;
        }

        private async Task LastChance(object state)
        {
            var timeoutMs = Math.Abs(ReconnectTimeoutMs);
            var halfTimeoutMs = timeoutMs / 1.5;
            var diffMs = Math.Abs(DateTime.UtcNow.Subtract(_lastReceivedMsg).TotalMilliseconds);
            if(diffMs > halfTimeoutMs)
                Log.Debug(L($"Last message received {diffMs:F} ms ago"));
            if (diffMs > timeoutMs)
            {
                Log.Debug(L($"Last message received more than {timeoutMs:F} ms ago. Hard restart.."));

                _client?.Abort();
                _client?.Dispose();
                await Reconnect();
            }
        }

        private string L(string msg)
        {
            return $"[BFX WEBSOCKET COMMUNICATOR] {msg}";
        }
    }
}

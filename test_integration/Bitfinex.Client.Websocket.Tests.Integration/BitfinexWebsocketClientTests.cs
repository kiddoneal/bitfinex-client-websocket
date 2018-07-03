using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Bitfinex.Client.Websocket.Client;
using Bitfinex.Client.Websocket.Requests;
using Bitfinex.Client.Websocket.Responses;
using Bitfinex.Client.Websocket.Websockets;
using Serilog;
using Serilog.Events;
using Xunit;

namespace Bitfinex.Client.Websocket.Tests.Integration
{
    public class BitfinexWebsocketClientTests
    {
        private static readonly string API_KEY = "ABW7OS66EvtQtZ4UZ5b51SVur0KQALrROCZkWU1OiNM";
        private static readonly string API_SECRET = "oF4Y25DOmZOHJxCUEJeDnTYaUz0GnKPjV31HZGoQ3ZR";

        private static string executingDir = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
        private static string logPath = Path.Combine(executingDir, "logs", "verbose.log");


        [Fact]
        public async Task PingPong()
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
                .WriteTo.ColoredConsole(LogEventLevel.Verbose)
                .CreateLogger();

            var url = BitfinexValues.ApiWebsocketUrl;
            using (var communicator = new BitfinexWebsocketCommunicator(url))
            {
                PongResponse received = null;
                var receivedEvent = new ManualResetEvent(false);

                using (var client = new BitfinexWebsocketClient(communicator))
                {

                    client.Streams.PongStream.Subscribe(pong =>
                    {
                        received = pong;
                        receivedEvent.Set();
                    });

                    await communicator.Start();

                    await client.Send(new PingRequest() {Cid = 123456});

                    receivedEvent.WaitOne(TimeSpan.FromSeconds(30));

                    Assert.NotNull(received);
                    Assert.Equal(123456, received.Cid);
                    Assert.True(DateTime.UtcNow.Subtract(received.Ts).TotalSeconds < 15);
                }
            }
        }

        [SkippableFact]
        public async Task Authentication()
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
                .WriteTo.ColoredConsole(LogEventLevel.Verbose)
                .CreateLogger();

            Skip.If(string.IsNullOrWhiteSpace(API_SECRET));

            var url = BitfinexValues.ApiWebsocketUrl;
            using (var communicator = new BitfinexWebsocketCommunicator(url))
            {
                AuthenticationResponse received = null;
                var receivedEvent = new ManualResetEvent(false);

                using (var client = new BitfinexWebsocketClient(communicator))
                {

                    client.Streams.AuthenticationStream.Subscribe(auth =>
                    {
                        received = auth;
                        receivedEvent.Set();
                    });

                    await communicator.Start();

                    await client.Authenticate(API_KEY, API_SECRET);

                    receivedEvent.WaitOne(TimeSpan.FromSeconds(30));

                    Assert.NotNull(received);
                    Assert.True(received.IsAuthenticated);
                }
            }
        }

    }
}

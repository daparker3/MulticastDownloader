﻿// <copyright file="SessionTest.cs" company="MS">
// Copyright (c) 2016 MS.
// </copyright>

namespace MS.MulticastDownloader.Tests.IO
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Common.Logging;
    using Common.Logging.Simple;
    using Core;
    using Core.Cryptography;
    using Core.IO;
    using Core.Server;
    using Core.Server.IO;
    using Org.BouncyCastle.OpenSsl;
    using PCLStorage;
    using ProtoBuf;
    using Sockets.Plugin;
    using Sockets.Plugin.Abstractions;
    using Xunit;
    using Xunit.Abstractions;

    public class SessionTest
    {
        public SessionTest(ITestOutputHelper outputHelper)
        {
            LogManager.Adapter = new TestOutputLoggerFactoryAdapter(LogLevel.All, outputHelper);
        }

        public static IEnumerable<object[]> TestSessionReadWriteData
        {
            get
            {
                return new object[][]
                {
                    new object[]
                    {
                        new UriParameters
                        {
                            Hostname = "localhost",
                            Port = 8001,
                            Path = "foo"
                        },
                        new TestSettings(false),
                        new TestServerSettings(false),
                        null,
                        10,
                        true
                    },
                    new object[]
                    {
                        new UriParameters
                        {
                            UseTls = true,
                            Hostname = "localhost",
                            Port = 8001,
                            Path = "foo"
                        },
                        new TestSettings(true),
                        new TestServerSettings(false),
                        "foo123",
                        10,
                        true
                    }
                };
            }
        }

        [Theory]
        [MemberData("TestSessionReadWriteData")]
        public async Task TestSessionReadWrite(UriParameters uriParams, IMulticastSettings settings, IMulticastServerSettings serverSettings, string passPhrase, int numAttempts, bool withIfName)
        {
            await ((TestServerSettings)serverSettings).Init(withIfName);
            using (ServerListener listener = new ServerListener(uriParams, settings, serverSettings))
            {
                Assert.Equal(uriParams, listener.UriParameters);
                Assert.Equal(settings, listener.Settings);
                Assert.Equal(serverSettings, listener.ServerSettings);
                await listener.Listen();
                using (UdpWriter writer = await listener.CreateWriter(0, settings.Encoder))
                {
                    Assert.NotNull(writer);
                    await writer.Close();
                }

                List<ClientConnection> clientConns = new List<ClientConnection>();
                for (int i = 0; i < serverSettings.MaxConnectionsPerSession; ++i)
                {
                    ClientConnection cliConn = new ClientConnection(uriParams, settings);
                    Assert.Equal(uriParams, cliConn.UriParameters);
                    Assert.Equal(settings, cliConn.Settings);
                    await cliConn.Connect();
                    Assert.NotNull(cliConn.TcpSession);
                    clientConns.Add(cliConn);
                }

                List<ServerConnection> serverConns = new List<ServerConnection>();
                DateTime end = DateTime.Now.AddMinutes(10);
                while (DateTime.Now < end && serverConns.Count < serverSettings.MaxConnectionsPerSession)
                {
                    await Task.Delay(200);
                    foreach (ServerConnection serverConn in await listener.ReceiveConnections(CancellationToken.None))
                    {
                        Assert.Equal(uriParams, serverConn.UriParameters);
                        Assert.Equal(settings, serverConn.Settings);
                        Assert.NotNull(serverConn.TcpSession);
                        serverConns.Add(serverConn);
                    }
                }

                if (uriParams.UseTls)
                {
                    List<Task> tlsTasks = new List<Task>();
                    foreach (ClientConnection clientConn in clientConns)
                    {
                        byte[] secret = CreateSecret(passPhrase);
                        tlsTasks.Add(Task.Run(() => clientConn.ConnectTls(secret)));
                    }

                    foreach (ServerConnection serverConn in serverConns)
                    {
                        byte[] secret = CreateSecret(passPhrase);
                        tlsTasks.Add(Task.Run(() => serverConn.AcceptTls(secret)));
                    }

                    await Task.WhenAll(tlsTasks);
                }

                List<Task> writeTasks = new List<Task>();
                for (int i = 0; i < numAttempts; ++i)
                {
                    HashSet<int> remaining = new HashSet<int>();
                    for (int j = 0; j < clientConns.Count; ++j)
                    {
                        TestData td = new TestData();
                        int d = (1 + i) * (1 + j);
                        td.Data = BitConverter.GetBytes(d);
                        remaining.Add(d);
                        writeTasks.Add(clientConns[j].Send(td, CancellationToken.None));
                    }

                    for (int j = 0; j < clientConns.Count; ++j)
                    {
                        TestData td2 = await serverConns[j].Receive<TestData>(CancellationToken.None);
                        int d = BitConverter.ToInt32(td2.Data, 0);
                        Assert.True(remaining.Remove(d));
                    }

                    Assert.Equal(0, remaining.Count);
                }

                await Task.WhenAll(writeTasks);
                await CloseConnections(clientConns, serverConns);
                for (int i = 0; i < serverSettings.MaxConnectionsPerSession + 1; ++i)
                {
                    ClientConnection cliConn = new ClientConnection(uriParams, settings);
                    await cliConn.Connect();
                    clientConns.Add(cliConn);
                }

                end = DateTime.Now.AddMinutes(10);
                while (DateTime.Now < end && serverConns.Count < serverSettings.MaxConnectionsPerSession)
                {
                    await Task.Delay(200);
                    foreach (ServerConnection serverConn in await listener.ReceiveConnections(CancellationToken.None))
                    {
                        serverConns.Add(serverConn);
                    }
                }

                Assert.Equal(serverSettings.MaxConnectionsPerSession, serverConns.Count);
                await CloseConnections(clientConns, serverConns);
                await listener.Close();
            }
        }

        [Theory]
        [InlineData("mcs://localhost/foo", "foo", "bar")]
        [InlineData("mcs://localhost/foo", "foo", "bar")]
        public async Task TestTlsNegotationFailsWithMismatchedPassphrases(string uri, string passPhrase1, string passPhrase2)
        {
            Assert.NotEqual(passPhrase1, passPhrase2);
            UriParameters uriParams = new UriParameters(new Uri(uri));
            IMulticastSettings settings = new TestSettings(true);
            IMulticastServerSettings serverSettings = new TestServerSettings(false);
            await ((TestServerSettings)serverSettings).Init(false);
            using (ServerListener listener = new ServerListener(uriParams, settings, serverSettings))
            {
                await listener.Listen();
                ClientConnection clientConn = new ClientConnection(uriParams, settings);
                await clientConn.Connect();
                ServerConnection serverConn = null;
                DateTime end = DateTime.Now.AddMinutes(10);
                while (DateTime.Now < end && serverConn == null)
                {
                    await Task.Delay(200);
                    foreach (ServerConnection conn in await listener.ReceiveConnections(CancellationToken.None))
                    {
                        serverConn = conn;
                        break;
                    }
                }

                try
                {
                    List<Task> tlsTasks = new List<Task>();
                    byte[] secret1 = CreateSecret(passPhrase1);
                    tlsTasks.Add(Task.Run(() => clientConn.ConnectTls(secret1)));
                    byte[] secret2 = CreateSecret(passPhrase2);
                    tlsTasks.Add(Task.Run(() => serverConn.AcceptTls(secret2)));
                    await Task.WhenAll(tlsTasks);
                    Assert.True(false);
                }
                catch (Exception)
                {
                }

                await clientConn.Close();
                await serverConn.Close();
                await listener.Close();
            }
        }

        private static byte[] CreateSecret(string passPhrase)
        {
            byte[] secret = null;
            if (!string.IsNullOrEmpty(passPhrase))
            {
                secret = Encoding.Unicode.GetBytes(passPhrase);
            }

            return secret;
        }

        private static async Task CloseConnections(List<ClientConnection> clientConns, List<ServerConnection> serverConns)
        {
            foreach (ClientConnection cliConn in clientConns)
            {
                await cliConn.Close();
                cliConn.Dispose();
            }

            foreach (ServerConnection serverConn in serverConns)
            {
                await serverConn.Close();
                serverConn.Dispose();
            }

            clientConns.Clear();
            serverConns.Clear();
        }

        private class TestSettings : IMulticastSettings
        {
            public TestSettings(bool encoding)
            {
                if (encoding)
                {
                    this.Encoder = new PassphraseEncoderFactory("12345", Encoding.UTF8);
                }

                this.MulticastBufferSize = 1 << 20;
                this.ReadTimeout = TimeSpan.FromMinutes(10);
                this.Ttl = 1;
            }

            public IEncoderFactory Encoder
            {
                get;
                set;
            }

            public int MulticastBufferSize
            {
                get;
                set;
            }

            public TimeSpan ReadTimeout
            {
                get;
                set;
            }

            public int Ttl
            {
                get;
                set;
            }
        }

        private class TestServerSettings : IMulticastServerSettings
        {
            public TestServerSettings(bool ipv6)
            {
                this.Mtu = 1500;

                // FIXMEFIXME
                this.MaxConnectionsPerSession = 10;
                this.MaxSessions = 10;
                this.MulticastStartPort = 0xFF00;
                if (ipv6)
                {
                    this.MulticastAddress = "FF01::1";
                    this.Ipv6 = true;
                }
                else
                {
                    this.MulticastAddress = "239.0.0.1";
                }
            }

            public string InterfaceName
            {
                get;
                set;
            }

            public bool Ipv6
            {
                get;
                set;
            }

            public int MaxConnectionsPerSession
            {
                get;
                set;
            }

            public int MaxSessions
            {
                get;
                set;
            }

            public int Mtu
            {
                get;
                set;
            }

            public string MulticastAddress
            {
                get;
                set;
            }

            public int MulticastBurstLength
            {
                get;
                set;
            }

            public int MulticastStartPort
            {
                get;
                set;
            }

            public IFolder RootFolder
            {
                get;
                set;
            }

            public async Task Init(bool withIfName)
            {
                if (withIfName)
                {
                    foreach (CommsInterface ci in await CommsInterface.GetAllInterfacesAsync())
                    {
                        if (ci.IsUsable && !ci.IsLoopback && !string.IsNullOrEmpty(ci.GatewayAddress))
                        {
                            this.InterfaceName = ci.Name;
                            break;
                        }
                    }

                    Assert.NotNull(this.InterfaceName);
                }

                this.RootFolder = await FileSystem.Current.LocalStorage.CreateFolderAsync("session", CreationCollisionOption.ReplaceExisting);
            }
        }

        [ProtoContract]
        private class TestData
        {
            [ProtoMember(1)]
            public byte[] Data { get; set; }
        }
    }
}

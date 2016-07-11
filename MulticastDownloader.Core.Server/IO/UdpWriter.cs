﻿// <copyright file="UdpWriter.cs" company="MS">
// Copyright (c) 2016 MS.
// </copyright>

namespace MS.MulticastDownloader.Core.Server.IO
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics.Contracts;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Common.Logging;
    using Core.Cryptography;
    using Core.IO;
    using Core.Session;
    using Cryptography;
    using ProtoBuf;
    using Sockets.Plugin;
    using Sockets.Plugin.Abstractions;

    internal class UdpWriter : ServerConnectionBase
    {
        private ILog log = LogManager.GetLogger<ServerListener>();
        private IEncoderFactory encoder;
        private int port;
        private UdpSocketMulticastClient multicastClient = new UdpSocketMulticastClient();
        private ConcurrentBag<IEncoder> encoders = new ConcurrentBag<IEncoder>();
        private bool udpListen;
        private bool disposed;

        internal UdpWriter(UriParameters parms, IMulticastSettings settings, IMulticastServerSettings serverSettings)
            : base(parms, settings, serverSettings)
        {
        }

        internal int BlockSize
        {
            get;
            private set;
        }

        internal UdpSocketMulticastClient MulticastClient
        {
            get
            {
                return this.multicastClient;
            }
        }

        internal async Task StartMulticastServer(int sessionId, IEncoderFactory encoderFactory)
        {
            Contract.Requires(sessionId >= 0 && sessionId < this.ServerSettings.MaxSessions);
            this.port = this.ServerSettings.MulticastStartPort + sessionId;
            this.BlockSize = SessionJoinResponse.GetBlockSize(this.ServerSettings.Mtu, this.ServerSettings.Ipv6);
            if (encoderFactory != null)
            {
                this.encoder = encoderFactory;
                IEncoder encoder = encoderFactory.CreateEncoder();
                int unencodedSize = this.BlockSize;
                while (encoder.GetEncodedOutputLength(unencodedSize) > this.BlockSize)
                {
                    --unencodedSize;
                }

                Contract.Assert(unencodedSize > 0);
                this.BlockSize = unencodedSize;
            }

            this.udpListen = true;
            ICommsInterface commsInterface = await this.GetCommsInterface();
            this.log.DebugFormat("Initializing multicast session on {0}:{1}, if={2}", this.ServerSettings.MulticastAddress, this.ServerSettings.MulticastStartPort + sessionId, commsInterface != null ? commsInterface.Name : string.Empty);
            if (commsInterface != null)
            {
                this.log.DebugFormat("Interface broadcast ip={0}", commsInterface.BroadcastAddress);
                if (this.ServerSettings.MulticastAddress != commsInterface.BroadcastAddress)
                {
                    this.log.Warn("WARNING: Check your server settings. The broadcast address of your interface doesn't match the multicast address. This could result in weird behavior.");
                }
            }

            await this.multicastClient.JoinMulticastGroupAsync(this.ServerSettings.MulticastAddress, this.port, commsInterface);
        }

        internal override async Task Close()
        {
            await base.Close();
            if (this.udpListen)
            {
                await this.multicastClient.DisconnectAsync();
            }
        }

        internal async Task SendMulticast<T>(IEnumerable<T> data, CancellationToken token)
        {
            Contract.Requires(data != null);
            Task t0 = Task.Run(() =>
            {
                IEncoderFactory encoderFactory = this.encoder;
                data.AsParallel().ForAll(async (val) =>
                {
                    byte[] serialized = new byte[this.BlockSize];
                    using (MemoryStream ms = new MemoryStream(serialized, true))
                    {
                        Serializer.Serialize(ms, val);
                    }

                    if (encoderFactory != null)
                    {
                        IEncoder encoder;
                        if (!this.encoders.TryTake(out encoder))
                        {
                            encoder = encoderFactory.CreateEncoder();
                        }

                        serialized = encoder.Encode(serialized);
                        this.encoders.Add(encoder);
                    }

                    await this.multicastClient.SendMulticastAsync(serialized);
                });
            });

            await t0.WaitWithCancellation(token);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!this.disposed)
            {
                this.disposed = true;
                if (disposing)
                {
                    if (this.multicastClient != null)
                    {
                        this.multicastClient.Dispose();
                    }
                }
            }
        }
    }
}
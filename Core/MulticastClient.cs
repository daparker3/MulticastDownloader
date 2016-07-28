﻿// <copyright file="MulticastClient.cs" company="MS">
// Copyright (c) 2016 MS.
// </copyright>

namespace MS.MulticastDownloader.Core
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Common.Logging;
    using Cryptography;
    using IO;
    using Properties;
    using Session;

    /// <summary>
    /// Represent a multicast client.
    /// </summary>
    /// <see cref="ITransferReporting"/>
    /// <see cref="ISequenceReporting"/>
    /// <see cref="IReceptionReporting"/>
    public class MulticastClient : IDisposable, ITransferReporting, ISequenceReporting, IReceptionReporting
    {
        internal const int MaxIntervals = 10;
        internal const int PacketUpdateInterval = 1000;
        internal const int EncoderSize = 256;
        internal static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(30);
        internal static readonly byte[] ResponseId = Encoding.UTF8.GetBytes("client");

        private ILog log = LogManager.GetLogger<MulticastClient>();
        private IEncoderFactory fileEncoder;
        private CancellationToken token;
        private FileHeader[] files = null;
        private FileSet fileSet;
        private BitVector written;
        private ClientConnection cliConn;
        private ChunkWriter writer;
        private UdpReader udpReader;
        private BoxedLong seqNum;
        private BoxedLong waveNum;
        private BoxedDouble receptionRate = new BoxedDouble(1.0);
        private ThroughputCalculator throughputCalculator = new ThroughputCalculator(MaxIntervals);
        private BoxedLong bytesPerSecond;
        private Task writeTask = null;
        private int state;
        private bool canReconnect;
        private bool disposedValue = false;
        private bool complete = false;

        /// <summary>
        /// Initializes a new instance of the <see cref="MulticastClient"/> class.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="settings">The settings.</param>
        /// <remarks>For examples of possible multicast URIs, see the <see cref="UriParameters"/> class.</remarks>
        public MulticastClient(Uri path, IMulticastSettings settings)
            : this(path, settings, 0)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="MulticastClient"/> class.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="settings">The settings.</param>
        /// <param name="state">The application-defined state.</param>
        /// <remarks>For examples of possible multicast URIs, see the <see cref="UriParameters"/> class.</remarks>
        public MulticastClient(Uri path, IMulticastSettings settings, int state)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }

            settings.Validate();
            this.Parameters = new UriParameters(path);
            this.Settings = settings;
            if (this.Parameters.UseTls && this.Settings.Encoder == null)
            {
                throw new ArgumentException(Resources.MustSpecifyEncoder, "settings");
            }

            this.token = CancellationToken.None;
            this.state = state;
        }

        /// <summary>
        /// Gets the multicast URI parameters.
        /// </summary>
        /// <value>
        /// The URI parameters.
        /// </value>
        public UriParameters Parameters
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the multicast settings.
        /// </summary>
        /// <value>
        /// The multicast settings.
        /// </value>
        public IMulticastSettings Settings
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets a value indicating whether this <see cref="MulticastClient"/> is complete.
        /// </summary>
        /// <value>
        ///   <c>true</c> if complete; otherwise, <c>false</c>.
        /// </value>
        public bool Complete
        {
            get
            {
                return this.complete;
            }
        }

        /// <summary>
        /// Gets the total bytes in the payload.
        /// </summary>
        /// <value>
        /// The total bytes in the payload.
        /// </value>
        public long TotalBytes
        {
            get
            {
                if (this.writer != null)
                {
                    long bytes = 0;
                    foreach (FileChunk chunk in this.writer.Chunks)
                    {
                        bytes += chunk.Block.Length;
                    }

                    return bytes;
                }

                return 0;
            }
        }

        /// <summary>
        /// Gets the bytes remaining in the payload.
        /// </summary>
        /// <value>
        /// The bytes remaining.
        /// </value>
        public long BytesRemaining
        {
            get
            {
                if (this.writer != null)
                {
                    long bytes = 0;
                    foreach (FileChunk chunk in this.writer.Chunks)
                    {
                        if (!this.writer.Written[chunk.Block.SegmentId])
                        {
                            bytes += chunk.Block.Length;
                        }
                    }

                    return bytes;
                }

                return 0;
            }
        }

        /// <summary>
        /// Gets the bytes per second.
        /// </summary>
        /// <value>
        /// The bytes per second.
        /// </value>
        public long BytesPerSecond
        {
            get
            {
                BoxedLong bytesPerSecond = this.bytesPerSecond;
                if (bytesPerSecond != null)
                {
                    return bytesPerSecond.Value;
                }

                return 0;
            }
        }

        /// <summary>
        /// Gets the written bits.
        /// </summary>
        /// <value>
        /// The written bits.
        /// </value>
        public BitVector Written
        {
            get
            {
                return this.written;
            }
        }

        /// <summary>
        /// Gets the current sequence number in the download.
        /// </summary>
        /// <value>
        /// The sequence number.
        /// </value>
        public long SequenceNumber
        {
            get
            {
                BoxedLong seq = this.seqNum;
                if (seq != null)
                {
                    return seq.Value;
                }

                return 0;
            }
        }

        /// <summary>
        /// Gets the current wave number in the download.
        /// </summary>
        /// <value>
        /// The wave number.
        /// </value>
        public long WaveNumber
        {
            get
            {
                BoxedLong wave = this.waveNum;
                if (wave != null)
                {
                    return wave.Value;
                }

                return 0;
            }
        }

        /// <summary>
        /// Gets the packet reception rate reported from the last wave update.
        /// </summary>
        /// <value>
        /// The reception rate, as a coefficient in the range [0,1].
        /// </value>
        public double ReceptionRate
        {
            get
            {
                BoxedDouble receptionRate = this.receptionRate;
                if (receptionRate != null)
                {
                    return receptionRate.Value;
                }

                return 0;
            }
        }

        /// <summary>
        /// Starts the multicast transfer.
        /// </summary>
        /// <param name="token">The optional cancellation token.</param>
        /// <returns>A task object.</returns>
        /// <exception cref="SessionAbortedException">Occurs if the transfer is aborted.</exception>
        public Task StartTransfer(CancellationToken token)
        {
            this.token = token;
            return Task.Run(async () =>
            {
                int attempt = 0;
                this.complete = false;

                while (!this.complete)
                {
                    this.canReconnect = false;
                    this.writeTask = null;
                    if (attempt++ > 0)
                    {
                        this.log.Info("Reconnecting. Attempt " + attempt);
                        this.DisposeInternal();
                        await Task.Delay(ReconnectDelay, this.token);
                    }

                    try
                    {
                        this.log.Debug("Starting transfer");

                        // Connect to the server, authenticate and receive the multicast payload.
                        await this.ConnectToServer();
                        await this.RequestFilesAndBeginReading();
                        this.canReconnect = true;

                        // Now begin processing received packets and transmitting them back to the service.
                        await this.MulticastDownload();

                        await this.writer.Flush();
                        this.log.Info("Transfer complete");
                        await this.cliConn.Close();
                        await this.udpReader.Close();
                    }
                    catch (Exception ex)
                    {
                        if (!this.canReconnect || (ex is SessionAbortedException) || (ex is OperationCanceledException))
                        {
                            this.log.Error("Session aborted: " + ex.GetType() + " '" + ex.Message + "'");
                            await this.TryCleanFiles();
                            if (!(ex is SessionAbortedException) && !(ex is OperationCanceledException))
                            {
                                throw new SessionAbortedException(Resources.SessionAborted, ex);
                            }

                            throw;
                        }

                        this.log.Error(ex);
                    }
                }
            });
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!this.disposedValue)
            {
                this.disposedValue = true;
                if (disposing)
                {
                    this.DisposeInternal();
                }

                this.written = null;
                this.writer = null;
            }
        }

        private void DisposeInternal()
        {
            if (this.cliConn != null)
            {
                this.cliConn.Dispose();
            }

            if (this.udpReader != null)
            {
                this.udpReader.Dispose();
            }

            if (this.fileSet != null)
            {
                this.fileSet.Dispose();
            }
        }

        private async Task ConnectToServer()
        {
            try
            {
                this.log.Info("Connecting to server: " + this.Parameters);
                this.cliConn = new ClientConnection(this.Parameters, this.Settings);
                await this.cliConn.Connect();

                Challenge challenge = await this.cliConn.Receive<Challenge>(this.token);
                byte[] psk = null;
                if (this.Settings.Encoder != null)
                {
                    IDecoder decoder = this.Settings.Encoder.CreateDecoder();
                    psk = decoder.Decode(challenge.ChallengeKey);
                }

                this.log.Debug("Challenge: " + Convert.ToBase64String(challenge.ChallengeKey));
                if (this.Parameters.UseTls)
                {
                    // We use TLS to secure the connection before sending back the decoded response.
                    this.cliConn.ConnectTls(psk);
                }

                // We'll use the file encoder on the PSK to decode file data.
                ChallengeResponse response = new ChallengeResponse();
                if (psk != null)
                {
                    this.fileEncoder = new HashEncoderFactory(psk, EncoderSize);
                    IEncoder encoder = this.fileEncoder.CreateEncoder();
                    response.ChallengeKey = encoder.Encode(ResponseId);
                }
                else
                {
                    response.ChallengeKey = ResponseId;
                }

                await this.cliConn.Send(response, this.token);

                Response authResponse = await this.CheckResponse<Response>();
                this.log.Debug("Auth response: " + authResponse);
            }
            catch (Exception ex)
            {
                throw new SessionAbortedException(Resources.ConnectionFailed, ex);
            }
        }

        private async Task RequestFilesAndBeginReading()
        {
            try
            {
                SessionJoinRequest request = new SessionJoinRequest();
                request.Path = this.Parameters.Path;
                request.State = this.state;
                this.log.InfoFormat("Requesting payload '{0}' with state: {1}", request.Path, request.State);
                await this.cliConn.Send(request, this.token);

                SessionJoinResponse response = await this.CheckResponse<SessionJoinResponse>();
                if (this.files != null && !this.files.SequenceEqual(response.Files))
                {
                    this.log.Error("Payload differs.");
                    throw new SessionAbortedException(Resources.ServerPayloadMismatch);
                }

                this.fileSet = new FileSet(this.Settings.RootFolder, response.Files);
                await this.fileSet.InitWrite();
                if (this.written == null)
                {
                    long countChunks = this.fileSet.EnumerateChunks().LongCount();
                    this.written = new BitVector(countChunks);
                }

                this.writer = new ChunkWriter(this.fileSet.EnumerateChunks(), this.written);
                this.log.Debug("Bytes remaining: " + this.BytesRemaining);

                this.log.DebugFormat("Listening on {0}:{1}", response.MulticastAddress, response.MulticastPort);
                this.waveNum = new BoxedLong(response.WaveNumber);
                this.log.Debug("Starting wave: " + response.WaveNumber);
                this.udpReader = await this.cliConn.JoinMulticastServer(response, this.fileEncoder);
            }
            catch (Exception ex)
            {
                throw new SessionAbortedException(Resources.RequestFailed, ex);
            }
        }

        private async Task MulticastDownload()
        {
            try
            {
                this.throughputCalculator.Start(this.BytesRemaining, DateTime.Now);
                this.log.Info("Waiting for multicast payload");
                Task statusTask = this.UpdateStatus();
                while (!this.complete)
                {
                    List<FileSegment> received = new List<FileSegment>(await this.udpReader.ReceiveMulticast<FileSegment>(this.token));
                    FileSegment last = received.LastOrDefault();
                    if (last != null)
                    {
                        this.seqNum = new BoxedLong(last.SegmentId);
                    }

                    if (this.writeTask != null)
                    {
                        await this.writeTask;
                    }

                    this.writeTask = this.writer.WriteSegments(received);
                }

                if (this.writeTask != null)
                {
                    await this.writeTask;
                }

                if (statusTask != null)
                {
                    await statusTask;
                }
            }
            catch (Exception ex)
            {
                throw new SessionAbortedException(Resources.MulticastDownloadFailed, ex);
            }
        }

        // Returns true if the wave completed
        private Task UpdateStatus()
        {
            return Task.Run(async () =>
            {
                try
                {
                    while (this.complete = !this.written.Contains(false))
                    {
                        PacketStatusUpdate statusUpdate = new PacketStatusUpdate();
                        long bytesLeft = this.BytesRemaining;
                        long average = this.throughputCalculator.UpdateThroughput(bytesLeft, DateTime.Now);
                        this.bytesPerSecond = new BoxedLong(average);
                        statusUpdate.BytesLeft = bytesLeft;
                        statusUpdate.LeavingSession = this.complete;
                        this.log.Debug("Sequence: " + this.SequenceNumber + "  Bytes left: " + bytesLeft + "  Leaving session: " + this.complete);
                        await this.cliConn.Send(statusUpdate, this.token);
                        PacketStatusUpdateResponse resp = await this.CheckResponse<PacketStatusUpdateResponse>();
                        this.receptionRate = new BoxedDouble(resp.ReceptionRate);
                        if (resp.ResponseType == Session.ResponseId.WaveComplete)
                        {
                            this.log.Debug("Wave complete");
                            if (this.writeTask != null)
                            {
                                await this.writeTask;
                            }

                            WaveStatusUpdate waveUpdate = new WaveStatusUpdate();
                            waveUpdate.BytesLeft = bytesLeft;
                            waveUpdate.LeavingSession = this.complete;
                            waveUpdate.FileBitVector = this.written.RawBits;
                            await this.cliConn.Send(waveUpdate, this.token);
                            WaveCompleteResponse waveResp = await this.CheckResponse<WaveCompleteResponse>();
                            this.waveNum = new BoxedLong(waveResp.WaveNumber);
                            this.log.Debug("New wave: " + waveResp.WaveNumber);
                        }

                        await Task.Delay(PacketUpdateInterval, this.token);
                    }
                }
                catch (Exception ex)
                {
                    throw new SessionAbortedException(Resources.StatusUpdateFailed, ex);
                }
            });
        }

        private async Task<bool> TryCleanFiles()
        {
            try
            {
                if (this.cliConn != null)
                {
                    await this.cliConn.Close();
                }

                if (this.udpReader != null)
                {
                    await this.udpReader.Close();
                }

                if (this.fileSet != null)
                {
                    await this.fileSet.Clean();
                }

                return true;
            }
            catch (Exception ex)
            {
                this.log.Warn("Failed to clean temporary files from disk.");
                this.log.Debug(ex);
            }

            return false;
        }

        private async Task<T> CheckResponse<T>()
            where T : Response
        {
            T authResponse = await this.cliConn.Receive<T>(this.token);
            authResponse.ThrowIfFailed();
            return authResponse;
        }
    }
}
﻿//
// Copyright (c) 2018 Pelco. All rights reserved.
//
// This file contains trade secrets of Pelco.  No part may be reproduced or
// transmitted in any form by any means or for any purpose without the express
// written permission of Pelco.
//
using NLog;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Pelco.Media.RTSP.Server
{
    public sealed class RtspServer : IDisposable
    {
        private static readonly Logger LOG = LogManager.GetCurrentClassLogger();

        private readonly object ListenerLock = new object();

        private int _port;
        private TcpListener _listener;
        private ManualResetEvent _stop;
        private IRequestDispatcher _dispatcher;
        private BlockingCollection<RtspMessage> _messages;
        private ConcurrentDictionary<string, RtspListener> _listeners;

        public RtspServer(int port, IRequestDispatcher dispatcher)
        {
            _port = port;
            _dispatcher = dispatcher;
            _stop = new ManualResetEvent(false);
            _listeners = new ConcurrentDictionary<string, RtspListener>();
        }

        ~RtspServer()
        {
            Dispose();
        }

        public void Start()
        {
            lock (ListenerLock)
            {
                if (_listener == null)
                {
                    _stop.Reset();
                    _listener = new TcpListener(IPAddress.Any, _port);
                    _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

                    _messages = new BlockingCollection<RtspMessage>();

                    _listener.Start();
                    _dispatcher.Init();
                    LOG.Info($"Started RTSP server on '{_port}'");

                    ThreadPool.QueueUserWorkItem(Accept);
                    ThreadPool.QueueUserWorkItem(ProcessMessages);
                }
            }
        }

        public void Stop()
        {
            lock (ListenerLock)
            {
                if (_listener != null)
                {
                    _stop.Set();
                    _listener.Stop();
                    _dispatcher.Close();
                    _messages.Dispose();
                    _listener = null;

                    foreach (var listener in _listeners)
                    {
                        try
                        {
                            listener.Value.Stop();
                        }
                        catch (Exception e)
                        {
                            LOG.Error($"Received exception while stopping RTSP listener for {listener.Key}, message={e.Message}");
                        }
                    }
                    _listeners.Clear();

                    LOG.Info($"RTSP server on '{_port}' successfully shutdown");
                }
            }
        }

        private void Accept(object state)
        {
            while (!_stop.WaitOne(0))
            {
                try
                {
                    var client = _listener.AcceptTcpClient();
                    var conn = new RtspConnection(client);
                    conn.ConnectionClosed += ConnConnectionClosed;

                    var listener = new RtspListener(conn, OnRtspRequest);

                    LOG.Debug($"Accepted client connection from '{conn.RemoteAddress}'");

                    listener.Start();

                    _listeners.TryAdd(conn.RemoteAddress, listener);
                }
                catch (Exception e)
                {
                    LOG.Error(e, $"Caught exception while accepting client connection, message={e.Message}");
                }
            }
        }

        private void ConnConnectionClosed(object sender, EventArgs e)
        {
            var conn = sender as RtspConnection;
            conn.ConnectionClosed -= ConnConnectionClosed;
            _listeners.TryRemove(conn.RemoteAddress, out _);
        }

        private void OnRtspRequest(RtspChunk chunk)
        {
            if (chunk is RtspMessage)
            {
                _messages.Add(chunk as RtspMessage);
            }
        }

        private void ProcessMessages(object state)
        {
            try
            {
                while (true)
                {
                    var msg = _messages.Take();

                    if ((msg != null) && msg is RtspRequest request)
                    {
                        HandleRequest(request);
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                LOG.Info($"Message processing queue shutdown for RTSP Server at '{_port}'");
            }
            catch (Exception e)
            {
                LOG.Error(e, "Caught exception while processing RTSP message");
            }
        }

        private async void HandleRequest(RtspRequest request)
        {
            RtspListener listener = null;
            if (_listeners.TryGetValue(request.RemoteEndpoint.ToString(), out listener))
            {
                await Task.Run(() =>
                {
                    try
                    {
                        int receivedCseq = request.CSeq;
                        request.Context = new RequestContext(listener);
                        var response = _dispatcher.Dispatch(request);

                        if (response != null)
                        {
                            if (response.HasBody)
                            {
                                response.Headers[RtspHeaders.Names.CONTENT_LENGTH] = response.Body.Length.ToString();
                            }

                            // Always send headers
                            response.Headers[RtspHeaders.Names.CSEQ] = receivedCseq.ToString();
                            response.Headers[RtspHeaders.Names.DATE] = DateTime.UtcNow.ToString("ddd, dd MMM yyy HH':'mm':'ss 'GMT'");

                            listener.SendResponse(response);

                            // Remove listener on teardown.
                            // VLC will terminate the connection and the listener will stop itself properly.
                            // Some clients will send Teardown but keep the connection open, in this type scenario we'll close it.
                            if (request.Method == RtspRequest.RtspMethod.TEARDOWN)
                            {
                                listener.Stop();
                                _listeners.TryRemove(listener.Endpoint.ToString(), out _);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        LOG.Error(e, $"Caught exception while procesing RTSP request from {request.URI}");

                        listener.SendResponse(RtspResponse.CreateBuilder()
                                                          .Status(RtspResponse.Status.InternalServerError)
                                                          .Build());
                    }

                });
            }
            else
            {
                LOG.Error($"Unable to process request because no active connection was found for {request.URI}");
            }
        }

        public void Dispose()
        {
            Stop();
            GC.SuppressFinalize(this);
        }
    }
}

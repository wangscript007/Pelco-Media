///
// Copyright (c) 2018 Pelco. All rights reserved.
//
// This file contains trade secrets of Pelco.  No part may be reproduced or
// transmitted in any form by any means or for any purpose without the express
// written permission of Pelco.
//
using NLog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Pelco.Media.Pipeline.Sinks
{
    /// <summary>
    /// A TeeSink is a sink that takes in a single source buffer and replicates
    /// it out to all output sources.
    /// </summary>
    public class TeeSink : SinkBase
    {
        private const int DEFAULT_QUEUE_SIZE = 100;

        private int _queueSize;
        private SynchronizedCollection<ISink> _clients;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="queueSize">Size of the input source queue</param>
        public TeeSink(int queueSize = DEFAULT_QUEUE_SIZE)
        {
            _queueSize = queueSize;
            _clients = new SynchronizedCollection<ISink>();
        }

        /// <summary>
        /// <see cref="ISink.WriteBuffer(ByteBuffer)"/>
        /// </summary>
        /// <param name="buffer"></param>
        /// <returns></returns>
        public override bool WriteBuffer(ByteBuffer buffer)
        {
            int count = _clients.Count;
            for (int i = count - 1; i >= 0; i--)
            {
                ISink client = _clients[i];
                bool success = client.WriteBuffer(buffer);
                if (!success)
                    RemoveSource(client);
            }
            return count > 0;
        }

        /// <summary>
        /// <see cref="ISink.Stop"/>
        /// </summary>
        public override void Stop()
        {
            for (int i = _clients.Count - 1; i >= 0; i--)
            {
                ISink client = _clients[i];
                RemoveSource(client);
            }
        }

        /// <summary>
        /// Creates an new output source instance.
        /// </summary>
        /// <returns></returns>
        public ISource CreateSource()
        {
            var source = new TeeOutflowSource(_queueSize);
            _clients.Add(source);
            return source;
        }

        private void RemoveSource(ISink sink)
        {
            sink.Stop();
            _clients.Remove(sink);
        }

        private class TeeOutflowSource : TransformBase
        {
            private static readonly Logger LOG = LogManager.GetCurrentClassLogger();

            private bool _started;
            private int _queueSize;
            private ConcurrentQueue<ByteBuffer> _queue;
            private Task _processTask;
            private CancellationTokenSource _processCts;

            public TeeOutflowSource(int queueSize)
            {
                _queueSize = queueSize;
                _queue = new ConcurrentQueue<ByteBuffer>();
                Flushing = true;
            }

            public override void Start()
            {
                if (_started)
                {
                    LOG.Warn("Attempted to Start, already started");
                    return;
                }

                _processCts = new CancellationTokenSource();
                _processTask = Task.Run(ProcessBuffers);

                // We need to set the flushing flag to false so that the buffers
                // will be processed.
                Flushing = false;
                _started = true;
            }

            public override void Stop()
            {
                if (!_started)
                {
                    LOG.Warn("Attempted to Stop, already stopped");
                    return;
                }

                _processCts.Cancel();
                Task.Run(async () => await _processTask).Wait();

                _queue = new ConcurrentQueue<ByteBuffer>();

                // We need to set the flushing flag to true so that the buffers
                // will not be processed.
                Flushing = true;
                _started = false;
            }

            public override bool WriteBuffer(ByteBuffer buffer)
            {
                bool success = false;
                if (_started)
                {
                    try
                    {
                        if (!_processCts.IsCancellationRequested)
                        {
                            if (_queue.Count > _queueSize)
                            {
                                LOG.Warn("Dropping oldest buffer, queue is full");
                                _queue.TryDequeue(out ByteBuffer _);
                            }
                            _queue.Enqueue(buffer);
                            success = true;
                        }
                    }
                    catch (Exception e)
                    {
                        LOG.Error(e, "Threw while WriteBuffer");
                    }
                }
                return success;
            }

            private async Task ProcessBuffers()
            {
                try
                {
                    while (!_processCts.IsCancellationRequested)
                    {
                        while (!_processCts.IsCancellationRequested && !_queue.Any())
                            await Task.Delay(20);

                        if (_queue.TryDequeue(out ByteBuffer buffer))
                            PushBuffer(buffer);
                    }
                }
                catch (Exception e)
                {
                    LOG.Error(e, "Threw while ProcessBuffers");
                }
            }
        }
    }
}

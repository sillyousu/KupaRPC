using System;
using System.Linq;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using NonBlocking;
using Pipelines.Sockets.Unofficial;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;

namespace KupaRPC
{

    public class Client
    {
        private readonly ConcurrentDictionary<long, IPendingRequest> _pendingRequests = new ConcurrentDictionary<long, IPendingRequest>();
        private long _nextRequestID = 1;
        private readonly SocketConnection _conn;
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private readonly Codec _codec;
        private bool _stop = false;
        private readonly SemaphoreSlim _sendMutex = new SemaphoreSlim(1);
        private readonly ILogger _logger;
        private static readonly OperationCanceledException s_clientStoppedException = new OperationCanceledException("Client is stopped");
        private readonly ClientFactory _clientFactory;

        internal Client(ClientFactory clientFactory, ILoggerFactory loggerFactory, SocketConnection conn, Codec codec)
        {
            _conn = conn;
            _codec = codec;
            _logger = loggerFactory.CreateLogger("KupaRPC");
            _clientFactory = clientFactory;
            _ = ReceiveLoop();
        }

        public async Task<TReply> Send<TArg, TReply>(Request<TArg, TReply> request, IContext icontext)
        {
            // TODO IContext is a placeholder now.
            ClientContext context = icontext as ClientContext;
            if (context == null)
            {
                context = ClientContext.Default;
            }

            await _sendMutex.WaitAsync();
            try
            {
                if (_stop)
                {
                    throw s_clientStoppedException;
                }
                request.ID = _nextRequestID;
                _nextRequestID++;

                _codec.WriteRequest(request.Arg, request.ID, request.ServiceID, request.MethodID, out ReadOnlyMemory<byte> tmpBuffer);
                await _conn.Output.WriteAsync(tmpBuffer);
                _pendingRequests.Add(request.ID, request);
            }
            finally
            {
                _sendMutex.Release();
            }

            await request.Task;
            return request.Task.Result;
        }

        public TService Get<TService>()
        {
            return _clientFactory.GetServiceClient<TService>(this);
        }

        public async Task StopAsync()
        {
            IPendingRequest[] pendingRequests = null;
            await _sendMutex.WaitAsync();
            try
            {
                if (_stop)
                {
                    return;
                }
                _stop = true;
                _cancellation.Cancel();
                pendingRequests = _pendingRequests.Values.ToArray();
            }
            finally
            {
                _sendMutex.Release();
            }

            foreach (IPendingRequest request in pendingRequests)
            {
                request.OnError(s_clientStoppedException);
            }
        }

        private async Task ReceiveLoop()
        {
            PipeReader input = _conn.Input;
            ReponseHead head = new ReponseHead();

            try
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    ReadResult result = await input.ReadAsync(_cancellation.Token);
                    if (result.IsCompleted || result.IsCanceled)
                    {
                        return;
                    }
                    ReadOnlySequence<byte> buffer = result.Buffer;

                    if (!_codec.TryReadReponseHead(in buffer, ref head))
                    {
                        input.AdvanceTo(buffer.Start, buffer.End);
                        continue;
                    }

                    ReadOnlySequence<byte> body = buffer.Slice(Protocol.ReponseHeadSize, head.PayloadSize);

                    if (!_pendingRequests.TryRemove(head.RequestID, out IPendingRequest request))
                    {
                        input.AdvanceTo(body.End);
                        continue;
                    }

                    if (head.ErrorCode ==  ErrorCode.OK)
                    {
                        request.OnResult(_codec, in body);
                    }
                    else
                    {
                        request.OnError(new ServerException(head.ErrorCode));
                    }

                    input.AdvanceTo(body.End);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Client ReceiveLoop exception");
            }
            finally
            {
                _logger.LogInformation("Client ReceiveLoop exit");
                _ = StopAsync();
            }
        }
    }
}

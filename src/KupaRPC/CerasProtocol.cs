using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Ceras;

namespace KupaRPC
{
    public class CerasProtocol : Protocol
    {
        private readonly IEnumerable<ServiceDefine> _serviceDefines;

        internal CerasProtocol(IEnumerable<ServiceDefine> serviceDefines)
        {
            _serviceDefines = serviceDefines;
        }

        public override Codec NewCodec()
        {
            SerializerConfig config = new SerializerConfig();
            foreach (ServiceDefine service in _serviceDefines)
            {
                foreach (MethodDefine method in service.Methods)
                {
                    if (!config.KnownTypes.Contains(method.RpcParamType))
                    {
                        config.KnownTypes.Add(method.RpcParamType);
                    }
                    if (!config.KnownTypes.Contains(method.RpcReturnType))
                    {
                        config.KnownTypes.Add(method.RpcReturnType);
                    }
                }
            }

            CerasSerializer serializer = new CerasSerializer(config);
            Codec codec = new CerasCodec(serializer);
            return codec;
        }
    }

    public class CerasCodec : Codec
    {
        private readonly CerasSerializer _serializer;

        public CerasCodec(CerasSerializer serializer)
        {
            _serializer = serializer;
        }


        private byte[] _readBuffer = new byte[Protocol.RequestHeadSize];
        private byte[] _writeBuffer = new byte[Protocol.RequestHeadSize + 128];

        public override bool TryReadRequestHead(in ReadOnlySequence<byte> buffer, ref RequestHead head)
        {
            if (buffer.Length < Protocol.RequestHeadSize)
            {
                return false;
            }

            buffer.Slice(0, Protocol.RequestHeadSize).CopyTo(_readBuffer);

            ReadOnlySpan<byte> span = new ReadOnlySpan<byte>(_readBuffer);
            head.PayloadSize = BinaryPrimitives.ReadInt32LittleEndian(span);
            if (head.PayloadSize < 0 || head.PayloadSize > Protocol.MaxPayloadSize)
            {
                ThrowHelper.ThrowInvalidBodySizeException();
            }
            span = span.Slice(sizeof(int));

            head.RequestID = BinaryPrimitives.ReadInt64LittleEndian(span);
            span = span.Slice(sizeof(long));

            head.ServiceID = BinaryPrimitives.ReadUInt16LittleEndian(span);
            span = span.Slice(sizeof(ushort));

            head.MethodID = BinaryPrimitives.ReadUInt16LittleEndian(span);
            return true;
        }

        public override bool TryReadReponseHead(in ReadOnlySequence<byte> buffer, ref ReponseHead head)
        {
            if (buffer.Length < Protocol.ReponseHeadSize)
            {
                return false;
            }


            buffer.Slice(0, Protocol.ReponseHeadSize).CopyTo(_readBuffer);

            ReadOnlySpan<byte> span = new ReadOnlySpan<byte>(_readBuffer);
            head.PayloadSize = BinaryPrimitives.ReadInt32LittleEndian(span);
            if (head.PayloadSize < 0 || head.PayloadSize > Protocol.MaxPayloadSize)
            {
                ThrowHelper.ThrowInvalidBodySizeException();
            }
            span = span.Slice(sizeof(int));

            head.RequestID = BinaryPrimitives.ReadInt64LittleEndian(span);
            span = span.Slice(sizeof(long));

            head.ErrorCode = (ErrorCode)BinaryPrimitives.ReadInt32LittleEndian(span);

            return true;
        }

        public override T ReadBody<T>(in ReadOnlySequence<byte> body)
        {
            if (body.IsSingleSegment && MemoryMarshal.TryGetArray(body.First, out ArraySegment<byte> segment))
            {
            }
            else if (body.Length <= _readBuffer.Length)
            {
                body.CopyTo(_readBuffer);
                segment = new ArraySegment<byte>(_readBuffer, 0, (int)body.Length);
            }
            else
            {
                _readBuffer = body.ToArray();
                segment = new ArraySegment<byte>(_readBuffer, 0, (int)body.Length);
            }

            int offset = segment.Offset;
            T val = default;
            _serializer.Deserialize(ref val, segment.Array, ref offset);
            return val;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteReponseHead(int payloadSize, long requestID, ErrorCode errorCode)
        {
            Span<byte> span = _writeBuffer;
            BinaryPrimitives.WriteInt32LittleEndian(span, payloadSize);
            span = span.Slice(sizeof(int));

            BinaryPrimitives.WriteInt64LittleEndian(span, requestID);
            span = span.Slice(sizeof(long));

            BinaryPrimitives.WriteInt32LittleEndian(span, (int)errorCode);
        }


        public override void WriteRequest<T>(T arg, long requestID, ushort serviceID, ushort methodID, out ReadOnlyMemory<byte> tmpBuffer)
        {
            // write body
            int size = _serializer.Serialize(arg, ref _writeBuffer, Protocol.RequestHeadSize);
            if (size < 0 || size > Protocol.MaxPayloadSize)
            {
                ThrowHelper.ThrowInvalidBodySizeException();
            }

            Span<byte> span = _writeBuffer;
            BinaryPrimitives.WriteInt32LittleEndian(span, size);
            span = span.Slice(sizeof(int));

            BinaryPrimitives.WriteInt64LittleEndian(span, requestID);
            span = span.Slice(sizeof(long));

            BinaryPrimitives.WriteUInt16LittleEndian(span, serviceID);
            span = span.Slice(sizeof(ushort));

            BinaryPrimitives.WriteUInt16LittleEndian(span, methodID);

            tmpBuffer = new ReadOnlyMemory<byte>(_writeBuffer, 0, Protocol.RequestHeadSize + size);
        }

        public override void WriteReponse<T>(T body, long requestID, out ReadOnlyMemory<byte> tmpBuffer)
        {
            int size = _serializer.Serialize(body, ref _writeBuffer, Protocol.ReponseHeadSize);

            if (size < 0 || size > Protocol.MaxPayloadSize)
            {
                ThrowHelper.ThrowInvalidBodySizeException();
            }

            WriteReponseHead(size, requestID, ErrorCode.OK);

            tmpBuffer = new ReadOnlyMemory<byte>(_writeBuffer, 0, size + Protocol.ReponseHeadSize);
        }

        public override void WriteErrorReponse(ErrorCode errorCode, long requestID, out ReadOnlyMemory<byte> tmpBuffer)
        {
            WriteReponseHead(0, requestID, errorCode);
            tmpBuffer = new ReadOnlyMemory<byte>(_writeBuffer, 0, Protocol.ReponseHeadSize);
        }
    }
}

﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Npgsql.Messages;
using NpgsqlTypes;
using System.Data;

namespace Npgsql.TypeHandlers
{
    /// <remarks>
    /// http://www.postgresql.org/docs/9.3/static/datatype-binary.html
    /// </remarks>
    internal class ByteaHandler : TypeHandler<byte[]>
    {
        static readonly string[] _pgNames = { "bytea" };
        internal override string[] PgNames { get { return _pgNames; } }
        public override bool SupportsBinaryRead { get { return true; } }
        public override bool CanReadFromSocket { get { return true; } }

        static readonly NpgsqlDbType?[] _npgsqlDbTypes = { NpgsqlDbType.Bytea };
        internal override NpgsqlDbType?[] NpgsqlDbTypes { get { return _npgsqlDbTypes; } }
        static readonly DbType?[] _dbTypes = { DbType.Binary };
        internal override DbType?[] DbTypes { get { return _dbTypes; } }

        public override byte[] Read(NpgsqlBuffer buf, FieldDescription fieldDescription, int len)
        {
            byte[] result;
            switch (fieldDescription.FormatCode)
            {
                case FormatCode.Binary:
                    result = new byte[len];
                    buf.ReadBytes(result, 0, len, true);
                    break;

                case FormatCode.Text:
                    switch (ParseTextualByteaHeader(buf))
                    {
                        case ByteaTextFormat.Hex:
                            var decodedLen = (len - 2) / 2;
                            result = new byte[decodedLen];
                            buf.ReadBytesHex(result, 0, decodedLen, true);
                            break;
                        case ByteaTextFormat.Escape:
                            throw new NotImplementedException("Traditional bytea text escape encoding not (yet) implemented");
                        default:
                            throw PGUtil.ThrowIfReached();
                    }
                    break;

                default:
                    throw PGUtil.ThrowIfReached("Unknown format code: " + fieldDescription.FormatCode);
            }
            return result;
        }

        protected override int BinarySize(object value)
        {
            return 4 + ((byte[])value).Length;
        }

        protected override void WriteBinary(object value, NpgsqlBuffer buf)
        {
            var arr = (byte[])value;
            buf.EnsuredWriteInt32(arr.Length);
            buf.WriteBytes(arr);
        }

        static readonly byte[] HexDigits = new byte[16] { (byte)'0', (byte)'1', (byte)'2', (byte)'3', (byte)'4', (byte)'5', (byte)'6', (byte)'7',
                                                          (byte)'8', (byte)'9', (byte)'a', (byte)'b', (byte)'c', (byte)'d', (byte)'e', (byte)'f' };

        public override void WriteText(object value, NpgsqlTextWriter writer)
        {
            byte[] arr = (byte[])value;

            writer.WriteSingleChar('\\');
            writer.WriteSingleChar('x');

            if (arr.Length < 256)
            {
                foreach (var b in arr)
                {
                    writer.WriteSingleChar((char)HexDigits[b >> 4]);
                    writer.WriteSingleChar((char)HexDigits[b & 0xf]);
                }
            }
            else
            {
                byte[] buf = new byte[256];
                for (int i = 0, pos = 0; i < (arr.Length + 127) / 128; i++)
                {
                    var end = Math.Min(pos + 128, arr.Length);
                    var len = end - pos;
                    for (var j = 0; pos < end; pos++)
                    {
                        var b = arr[pos];
                        buf[j++] = HexDigits[b >> 4];
                        buf[j++] = HexDigits[b & 0xf];
                    }
                    writer.WriteRawByteArray(buf, 0, len * 2);
                }
            }
        }

        public long GetBytes(DataRowMessage row, int dataOffset, byte[] output, int bufferOffset, int len, FieldDescription field)
        {
            if (row.PosInColumn == 0) {
                PreparePartialFieldAccess(row, field);
            }

            switch (field.FormatCode)
            {
                case FormatCode.Binary:
                    return GetBytesBinary(row, dataOffset, output, bufferOffset, len);

                case FormatCode.Text:
                    switch (row.CurrentByteaTextFormat)
                    {
                        case ByteaTextFormat.Hex:
                            return GetBytesHex(row, dataOffset, output, bufferOffset, len);
                        case ByteaTextFormat.Escape:
                            throw new NotImplementedException("Traditional bytea text escape encoding not (yet) implemented");
                        default:
                            throw new ArgumentOutOfRangeException("Unknown bytea text encoding: " + row.CurrentByteaTextFormat);
                    }

                default:
                    throw PGUtil.ThrowIfReached();
            }
        }

        long GetBytesBinary(DataRowMessage row, int offset, byte[] output, int outputOffset, int len)
        {
            if (output == null) {
                return row.ColumnLen;
            }

            row.SeekInColumn(offset);

            // Attempt to read beyond the end of the column
            if (offset + len > row.ColumnLen) {
                len = row.ColumnLen - offset;
            }

            row.Buffer.ReadBytes(output, outputOffset, len, true);
            row.PosInColumn += len;
            return len;
        }

        long GetBytesHex(DataRowMessage row, int decodedOffset, byte[] output, int outputOffset, int decodedLen)
        {
            if (output == null) {
                return row.DecodedColumnLen;
            }

            // Translate content position to the byte position: 2-byte header, 2 bytes per encoded byte
            var posInColumn = 2 + decodedOffset * 2;
            row.SeekInColumn(posInColumn);
            row.DecodedPosInColumn = decodedOffset;

            // Attempt to read beyond the end of the column
            if (decodedOffset + decodedLen > row.DecodedColumnLen) {
                decodedLen = row.DecodedColumnLen - decodedOffset;
            }

            var read = row.Buffer.ReadBytesHex(output, outputOffset, decodedLen, true);
            Debug.Assert(read == decodedLen);
            row.DecodedPosInColumn += decodedLen;
            row.PosInColumn += decodedLen * 2;

            return decodedLen;
        }

        internal Stream GetStream(DataRowMessage row, FieldDescription field)
        {
            Contract.Requires(row.PosInColumn == 0);

            PreparePartialFieldAccess(row, field);
            switch (field.FormatCode)
            {
                case FormatCode.Text:
                    return new ByteaHexStream(row);
                case FormatCode.Binary:
                    return new ByteaBinaryStream(row);
                default:
                    throw PGUtil.ThrowIfReached();
            }
        }

        static ByteaTextFormat ParseTextualByteaHeader(NpgsqlBuffer buf)
        {
            buf.Ensure(2);

            // Must start with a backslash
            if (buf.ReadByte() != (byte)'\\') {
                throw new Exception("Unrecognized bytea text encoding");
            }

            return buf.ReadByte() == (byte)'x' ? ByteaTextFormat.Hex : ByteaTextFormat.Escape;
        }

        void PreparePartialFieldAccess(DataRowMessage row, FieldDescription field)
        {
            Contract.Requires(row.PosInColumn == 0);

            switch (field.FormatCode)
            {
                case FormatCode.Binary:
                    row.DecodedColumnLen = row.ColumnLen;
                    break;

                case FormatCode.Text:
                    row.CurrentByteaTextFormat = ParseTextualByteaHeader(row.Buffer);
                    switch (row.CurrentByteaTextFormat)
                    {
                        case ByteaTextFormat.Hex:
                            row.PosInColumn = 2;
                            row.DecodedColumnLen = (row.ColumnLen - 2) / 2;
                            break;
                        case ByteaTextFormat.Escape:
                            throw new NotImplementedException("Traditional bytea text escape encoding not (yet) implemented");
                        default:
                            throw PGUtil.ThrowIfReached();
                    }
                    break;

                default:
                    throw PGUtil.ThrowIfReached();
            }

            row.DecodedPosInColumn = 0;
        }

#if PREVIOUS_IMPLEMENTATION
#if NET45
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
        internal override void ReadText(NpgsqlBufferedStream buf, int len, FieldDescription field, NpgsqlValue output)
        {
            throw new NotImplementedException();

            Int32 byteAPosition = 0;
#if GO
            if (len >= 2 && BackendData[0] == (byte)ASCIIBytes.BackSlash && BackendData[1] == (byte)ASCIIBytes.x)
            {
                // PostgreSQL 8.5+'s bytea_output=hex format
                byte[] result = new byte[(len - 2) / 2];
#if UNSAFE
                unsafe
                {
                    fixed (byte* pBackendData = &BackendData[2])
                    {
                        fixed (byte* pResult = &result[0])
                        {
                            byte* pBackendData2 = pBackendData;
                            byte* pResult2 = pResult;
                            
                            for (byteAPosition = 2 ; byteAPosition < byteALength ; byteAPosition += 2)
                            {
                                *pResult2 = FastConverter.ToByteHexFormat(pBackendData2);
                                pBackendData2 += 2;
                                pResult2++;
                            }
                        }
                    }
                }
#else
                Int32 k = 0;

                for (byteAPosition = 2; byteAPosition < len; byteAPosition += 2)
                {
                    result[k] = FastConverter.ToByteHexFormat(BackendData, byteAPosition);
                    k++;
                }
#endif

                return result;
            }
            else
            {
                byte octalValue = 0;
                MemoryStream ms = new MemoryStream();

                while (byteAPosition < len)
                {
                    // The IsDigit is necessary in case we receive a \ as the octal value and not
                    // as the indicator of a following octal value in decimal format.
                    // i.e.: \201\301P\A
                    if (BackendData[byteAPosition] == (byte)ASCIIBytes.BackSlash)
                    {
                        if (byteAPosition + 1 == len)
                        {
                            octalValue = (byte)ASCIIBytes.BackSlash;
                            byteAPosition++;
                        }
                        else if (BackendData[byteAPosition + 1] >= (byte)ASCIIBytes.b0 && BackendData[byteAPosition + 1] <= (byte)ASCIIBytes.b7)
                        {
                            octalValue = FastConverter.ToByteEscapeFormat(BackendData, byteAPosition + 1);
                            byteAPosition += 4;
                        }
                        else
                        {
                            octalValue = (byte)ASCIIBytes.BackSlash;
                            byteAPosition += 2;
                        }
                    }
                    else
                    {
                        octalValue = BackendData[byteAPosition];
                        byteAPosition++;
                    }

                    ms.WriteByte((Byte)octalValue);
                }

                return ms.ToArray();
            }
#endif
        }
#endif
    }

    #region Streams

    abstract class ByteaStream : Stream
    {
        protected readonly DataRowMessage Row;

        protected ByteaStream(DataRowMessage row)
        {
            Row = row;
        }

        public override void Close()
        {
            Row.IsStreaming = false;
        }

        public override long Length
        {
            get { return Row.DecodedColumnLen; }
        }

        public override long Position
        {
            get { return Row.DecodedPosInColumn; }
            set { throw new NotSupportedException(); }
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }

        #region Not Supported

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        #endregion
    }

    class ByteaBinaryStream : ByteaStream
    {
        public ByteaBinaryStream(DataRowMessage row) : base(row) { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            count = Math.Min(count, Row.ColumnLen - Row.PosInColumn);
            var read = Row.Buffer.ReadBytes(buffer, offset, count, false);
            Row.PosInColumn += read;
            Row.DecodedPosInColumn += read;
            return read;
        }
    }

    class ByteaHexStream : ByteaStream
    {
        public ByteaHexStream(DataRowMessage row) : base(row) { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            count = Math.Min(count, Row.ColumnLen - Row.PosInColumn);
            var decodedRead = Row.Buffer.ReadBytesHex(buffer, offset, count, false);
            Row.DecodedPosInColumn += decodedRead;
            Row.PosInColumn += decodedRead * 2;
            return decodedRead;
        }
    }

    #endregion

    /// <summary>
    /// Indicates whether bytea text encoding uses the traditional escape format or the newer hex format.
    /// http://www.postgresql.org/docs/current/static/datatype-binary.html
    /// </summary>
    enum ByteaTextFormat
    {
        /// <summary>
        /// The newer hex format (the default since Postgresql 9.0)
        /// </summary>
        Hex,
        /// <summary>
        /// The traditional escape format
        /// </summary>
        Escape
    }
}

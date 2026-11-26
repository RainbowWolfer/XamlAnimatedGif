using System;
using System.IO;
using Buffer = System.Buffer;
using System.Runtime.CompilerServices;
using System.Diagnostics;

namespace XamlAnimatedGif.Decompression
{
    class LzwDecompressStream : Stream
    {
        private const int MaxCodeLength = 12;
        private const int TableSize = 1 << MaxCodeLength;

        private readonly BitReader _reader;
        private readonly int[] _start;
        private readonly int[] _length;
        private byte[] _arena;
        private int _arenaUsed;
        private int _minimumCodeLength;
        private int _clearCode;
        private int _stopCode;
        private int _count;
        private int _codeLength;
        private int _prevCode = -1;
        private byte[] _remainingBytes;
        private bool _endOfStream;

        public LzwDecompressStream()
            : this(Array.Empty<byte>(), 0, 2)
        {
        }

        public LzwDecompressStream(byte[] compressedBuffer, int minimumCodeLength)
            : this(compressedBuffer, compressedBuffer.Length, minimumCodeLength)
        {
        }

        public LzwDecompressStream(byte[] compressedBuffer, int length, int minimumCodeLength)
        {
            _reader = new BitReader(compressedBuffer, length);
            _start = new int[TableSize];
            _length = new int[TableSize];
            _arena = new byte[256 * 1024];
            Reset(compressedBuffer, length, minimumCodeLength);
        }

        public void Reset(byte[] compressedBuffer, int length, int minimumCodeLength)
        {
            _reader.Reset(compressedBuffer, length);
            _minimumCodeLength = minimumCodeLength;
            _clearCode = 1 << minimumCodeLength;
            _stopCode = _clearCode + 1;
            _endOfStream = false;
            _remainingBytes = null;
            ResetTable();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateReadArgs(buffer, offset, count);

            if (_endOfStream)
                return 0;

            int read = 0;

            FlushRemainingBytes(buffer, offset, count, ref read);

            while (read < count)
            {
                int code = _reader.ReadBits(_codeLength);

                if (!ProcessCode(code, buffer, offset, count, ref read))
                {
                    _endOfStream = true;
                    break;
                }
            }
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length
        {
            get { throw new NotSupportedException(); }
        }

        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        private void ResetTable()
        {
            if (_arena.Length < _clearCode)
                Ensure(_clearCode);

            for (int i = 0; i < _clearCode; i++)
            {
                _start[i] = i;
                _length[i] = 1;
                _arena[i] = (byte)i;
            }

            // Clear and stop occupy slots but are not sequences. Roots are not added
            // through Add, so the length stays at minimum + 1, matching the old table.
            _count = _clearCode + 2;
            _codeLength = _minimumCodeLength + 1;
            _prevCode = -1;
            _arenaUsed = _clearCode;
        }

        private void Ensure(int size)
        {
            if (size <= _arena.Length)
                return;

            int newSize = _arena.Length;
            while (newSize < size)
            {
                if (newSize > int.MaxValue / 2)
                {
                    newSize = size;
                    break;
                }
                newSize *= 2;
            }

            byte[] bigger = new byte[newSize];
            Buffer.BlockCopy(_arena, 0, bigger, 0, _arenaUsed);
            _arena = bigger;
        }

        private static byte[] CopySequenceToBuffer(byte[] sequence, byte[] buffer, int offset, int count, ref int read)
        {
            int bytesToRead = Math.Min(sequence.Length, count - read);
            Buffer.BlockCopy(sequence, 0, buffer, offset + read, bytesToRead);
            read += bytesToRead;
            byte[] remainingBytes = null;
            if (bytesToRead < sequence.Length)
            {
                int remainingBytesCount = sequence.Length - bytesToRead;
                remainingBytes = new byte[remainingBytesCount];
                Buffer.BlockCopy(sequence, bytesToRead, remainingBytes, 0, remainingBytesCount);
            }
            return remainingBytes;
        }

        private void FlushRemainingBytes(byte[] buffer, int offset, int count, ref int read)
        {
            if (_remainingBytes != null)
                _remainingBytes = CopySequenceToBuffer(_remainingBytes, buffer, offset, count, ref read);
        }

        [Conditional("DISABLED")]
        private static void ValidateReadArgs(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), "Offset can't be negative");
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), "Count can't be negative");
            if (offset + count > buffer.Length)
                throw new ArgumentException("Buffer is to small to receive the requested data");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool ProcessCode(int code, byte[] buffer, int offset, int count, ref int read)
        {
            if ((uint)code > (uint)_count || code >= TableSize)
                return false;

            if (code == _clearCode)
            {
                ResetTable();
                return true;
            }

            if (code == _stopCode)
                return false;

            if (code == _count)
            {
                if (_prevCode < 0 || _prevCode >= _count)
                    return false;

                int slot = _count;
                Add(_prevCode, _arena[_start[_prevCode]]);
                if (!Emit(slot, buffer, offset, count, ref read))
                    return false;
                _prevCode = code;
                return true;
            }

            if (!Emit(code, buffer, offset, count, ref read))
                return false;

            if (_prevCode >= 0)
                Add(_prevCode, _arena[_start[code]]);
            _prevCode = code;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Add(int prefix, byte suffix)
        {
            if (_count >= TableSize)
                return;

            int prefixLength = _length[prefix];
            int dest = _arenaUsed;
            Ensure(dest + prefixLength + 1);
            Buffer.BlockCopy(_arena, _start[prefix], _arena, dest, prefixLength);
            _arena[dest + prefixLength] = suffix;
            _start[_count] = dest;
            _length[_count] = prefixLength + 1;
            _arenaUsed = dest + prefixLength + 1;
            _count++;
            if ((_count & (_count - 1)) == 0 && _codeLength < MaxCodeLength)
                _codeLength++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool Emit(int code, byte[] buffer, int offset, int count, ref int read)
        {
            int length = _length[code];
            int source = _start[code];
            if (length == 1)
            {
                if (read >= count)
                {
                    _remainingBytes = [_arena[source]];
                    return true;
                }

                buffer[offset + read] = _arena[source];
                read++;
                _remainingBytes = null;
                return true;
            }

            int space = count - read;
            int take = length <= space ? length : space;
            Buffer.BlockCopy(_arena, source, buffer, offset + read, take);
            read += take;
            if (take == length)
            {
                _remainingBytes = null;
                return true;
            }

            int remain = length - take;
            _remainingBytes = new byte[remain];
            Buffer.BlockCopy(_arena, source + take, _remainingBytes, 0, remain);
            return true;
        }
    }
}

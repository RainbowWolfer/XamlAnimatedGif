using System;

namespace XamlAnimatedGif.Decompression
{
    class BitReader
    {
        private byte[] _buffer;
        private int _length;

        public BitReader(byte[] buffer, int length)
        {
            Reset(buffer, length);
        }

        public void Reset(byte[] buffer, int length)
        {
            _buffer = buffer ?? Array.Empty<byte>();
            _length = length < 0 ? 0 : Math.Min(length, _buffer.Length);
            _bytePosition = -1;
            _bitPosition = 0;
            _currentValue = 0;
            _bitsRead = 0;
        }

        private int _bytePosition = -1;
        private int _bitPosition;
        private int _currentValue;
        private int _bitsRead;
        public int ReadBits(int bitCount)
        {
            // Past the bytes actually copied for this frame. The shared buffer is larger
            // than this frame, and those extra bytes belong to whatever was decoded last.
            if (bitCount <= 0 || _bitsRead + bitCount > _length * 8)
                return -1;

            // The following code assumes it's running on a little-endian architecture.
            // It's probably safe to assume it will always be the case, because:
            // - Windows only supports little-endian architectures: x86/x64 and ARM (which supports
            //   both endiannesses, but Windows on ARM is always in little-endian mode)
            // - No platforms other than Windows support XAML applications
            // If the situation changes, this code will have to be updated.

            if (_bytePosition == -1)
            {
                _bytePosition = 0;
                _bitPosition = 0;
                _currentValue = ReadInt32(); //BitConverter.ToInt32(_buffer, _bytePosition);
            }
            else if (bitCount > 32 - _bitPosition)
            {
                int n = _bitPosition >> 3;
                _bytePosition += n;
                _bitPosition &= 0x07;
                _currentValue = ReadInt32() >> _bitPosition;
            }
            int mask = (1 << bitCount) - 1;
            int value = _currentValue & mask;
            _currentValue >>= bitCount;
            _bitPosition += bitCount;
            _bitsRead += bitCount;
            return value;
        }

        private int ReadInt32()
        {
            int value = 0;
            for (int i = 0; i < 4; i++)
            {
                if (_bytePosition + i >= _length)
                    break;
                value |= _buffer[_bytePosition + i] << (i << 3);
            }
            return value;
        }
    }
}

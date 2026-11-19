using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.Utils.Native;

namespace PCL.Core.Utils.Hash;

public class MurmurHash2Provider : IHashProvider
{
    private const int NativeSliceThreshold = 4096;
    private const int StreamChunkSize = 64 * 1024;
    private const uint MurmurMul = 0x5BD1E995u;

    public static MurmurHash2Provider Instance { get; } = new();
    public int Length => 8;

    public byte[] ComputeHash(ReadOnlySpan<byte> data)
    {
        if (data.Length >= NativeSliceThreshold && PclNativeInterop.TryMurmurHash2Slice(data, out var nativeHash))
        {
            return BitConverter.GetBytes(nativeHash);
        }

        return _ComputeHashManaged(data);
    }

    public byte[] ComputeHash(string input, Encoding? encoding = null)
    {
        encoding ??= Encoding.UTF8;
        var byteCount = encoding.GetByteCount(input);
        if (byteCount <= 1024)
        {
            Span<byte> buf = stackalloc byte[byteCount];
            encoding.GetBytes(input, buf);
            return ComputeHash(buf);
        }

        var rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = encoding.GetBytes(input, rented);
            return ComputeHash(rented.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public byte[] ComputeHash(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input is FileStream fs &&
            !fs.CanWrite &&
            fs.CanSeek &&
            fs.Position == 0 &&
            !string.IsNullOrEmpty(fs.Name) &&
            PclNativeInterop.TryMurmurHash2File(fs.Name, out var nativeFileHash))
        {
            fs.Seek(0, SeekOrigin.End);
            return BitConverter.GetBytes(nativeFileHash);
        }

        if (input is MemoryStream memStream && memStream.TryGetBuffer(out var seg))
        {
            var pos = (int)memStream.Position;
            var len = (int)memStream.Length;
            var span = seg.AsSpan(pos, Math.Max(0, len - pos));
            memStream.Position = memStream.Length;
            return ComputeHash(span);
        }

        if (input.CanSeek)
        {
            return _ComputeSeekableStreamManaged(input);
        }

        using var ms = new MemoryStream();
        input.CopyTo(ms);
        return ComputeHash(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    }

    public async ValueTask<byte[]> ComputeHashAsync(Stream input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        if (input is FileStream fs &&
            !fs.CanWrite &&
            fs.CanSeek &&
            fs.Position == 0 &&
            !string.IsNullOrEmpty(fs.Name) &&
            PclNativeInterop.TryMurmurHash2File(fs.Name, out var nativeFileHash))
        {
            fs.Seek(0, SeekOrigin.End);
            return BitConverter.GetBytes(nativeFileHash);
        }

        if (input is MemoryStream memStream && memStream.TryGetBuffer(out var seg))
        {
            var pos = (int)memStream.Position;
            var len = (int)memStream.Length;
            var span = seg.AsSpan(pos, Math.Max(0, len - pos));
            memStream.Position = memStream.Length;
            return ComputeHash(span);
        }

        if (input.CanSeek)
        {
            return await _ComputeSeekableStreamManagedAsync(input, cancellationToken).ConfigureAwait(false);
        }

        using var ms = new MemoryStream();
        await input.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return ComputeHash(ms.GetBuffer().AsSpan(0, (int)ms.Length));
    }

    private static byte[] _ComputeHashManaged(ReadOnlySpan<byte> data)
    {
        if (data.Length <= 4096)
        {
            Span<byte> filtered = stackalloc byte[data.Length];
            var n = 0;
            foreach (var b in data)
            {
                if (!_IsWhitespace(b))
                {
                    filtered[n++] = b;
                }
            }
            return _ComputeHashFiltered(filtered[..n]);
        }

        var filteredLen = _CountFilteredBytes(data);
        var hasher = new ManagedMurmur2Hasher(filteredLen);
        hasher.UpdateUnfiltered(data);
        return BitConverter.GetBytes(hasher.Finish());
    }

    private static byte[] _ComputeSeekableStreamManaged(Stream input)
    {
        var startPos = input.Position;
        var buf = ArrayPool<byte>.Shared.Rent(StreamChunkSize);
        try
        {
            long filteredLen = 0;
            int read;
            while ((read = input.Read(buf, 0, StreamChunkSize)) > 0)
            {
                filteredLen += _CountFilteredBytes(buf.AsSpan(0, read));
            }

            input.Position = startPos;
            var hasher = new ManagedMurmur2Hasher((uint)filteredLen);
            while ((read = input.Read(buf, 0, StreamChunkSize)) > 0)
            {
                hasher.UpdateUnfiltered(buf.AsSpan(0, read));
            }

            return BitConverter.GetBytes(hasher.Finish());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static async ValueTask<byte[]> _ComputeSeekableStreamManagedAsync(Stream input, CancellationToken cancellationToken)
    {
        var startPos = input.Position;
        var buf = ArrayPool<byte>.Shared.Rent(StreamChunkSize);
        try
        {
            long filteredLen = 0;
            int read;
            while ((read = await input.ReadAsync(buf.AsMemory(0, StreamChunkSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                filteredLen += _CountFilteredBytes(buf.AsSpan(0, read));
            }

            input.Position = startPos;
            var hasher = new ManagedMurmur2Hasher((uint)filteredLen);
            while ((read = await input.ReadAsync(buf.AsMemory(0, StreamChunkSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                hasher.UpdateUnfiltered(buf.AsSpan(0, read));
            }

            return BitConverter.GetBytes(hasher.Finish());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsWhitespace(byte b) => b is 9 or 10 or 13 or 32;

    private static uint _CountFilteredBytes(ReadOnlySpan<byte> data)
    {
        uint count = 0;
        foreach (var b in data)
        {
            if (!_IsWhitespace(b)) count++;
        }
        return count;
    }

    private static byte[] _ComputeHashFiltered(ReadOnlySpan<byte> data)
    {
        var h = (uint)(1 ^ data.Length);
        var i = 0;
        var loopTo = data.Length - 4;

        for (; i <= loopTo; i += 4)
        {
            var k = data[i]
                 | ((uint)data[i + 1] << 8)
                 | ((uint)data[i + 2] << 16)
                 | ((uint)data[i + 3] << 24);
            k *= MurmurMul;
            k ^= k >> 24;
            k *= MurmurMul;
            h *= MurmurMul;
            h ^= k;
        }

        switch (data.Length - i)
        {
            case 3:
                h ^= (uint)(data[i] | ((uint)data[i + 1] << 8));
                h ^= (uint)data[i + 2] << 16;
                h *= MurmurMul;
                break;
            case 2:
                h ^= (uint)(data[i] | ((uint)data[i + 1] << 8));
                h *= MurmurMul;
                break;
            case 1:
                h ^= data[i];
                h *= MurmurMul;
                break;
        }

        h ^= h >> 13;
        h *= MurmurMul;
        h ^= h >> 15;

        return BitConverter.GetBytes(h);
    }

    private struct ManagedMurmur2Hasher(uint filteredLen)
    {
        private uint _h = 1u ^ filteredLen;
        private uint _tail;
        private int _tailLen;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void _MixWord(uint k)
        {
            k *= MurmurMul;
            k ^= k >> 24;
            k *= MurmurMul;
            _h *= MurmurMul;
            _h ^= k;
        }

        public void UpdateUnfiltered(ReadOnlySpan<byte> data)
        {
            foreach (var b in data)
            {
                if (_IsWhitespace(b)) continue;
                _tail |= (uint)b << (_tailLen * 8);
                _tailLen++;
                if (_tailLen == 4)
                {
                    _MixWord(_tail);
                    _tail = 0;
                    _tailLen = 0;
                }
            }
        }

        public uint Finish()
        {
            if (_tailLen > 0)
            {
                _h ^= _tail;
                _h *= MurmurMul;
            }
            _h ^= _h >> 13;
            _h *= MurmurMul;
            _h ^= _h >> 15;
            return _h;
        }
    }
}

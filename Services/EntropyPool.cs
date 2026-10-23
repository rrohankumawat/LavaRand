using System.Security.Cryptography;

namespace LavaRand.Services;

/// <summary>
/// A tiny "LavaRand-style" entropy pool.
///
/// Idea (same as Cloudflare's LavaRand): collect messy, unpredictable data from the
/// physical world, hash it into a pool, and MIX the pool with the operating system's
/// own CSPRNG. Extra sources can only help; they can never make the output weaker,
/// because every output also depends on fresh OS randomness.
/// </summary>
public sealed class EntropyPool
{
    private readonly object _lock = new();
    private byte[] _state = RandomNumberGenerator.GetBytes(64);
    private long _counter;
    private long _samples;
    private long _bytesMixed;
    private readonly Dictionary<string, long> _bySource = new();
    private DateTimeOffset? _lastAdded;

    /// <summary>Mix a sample into the pool: state = SHA-512(state || counter || sample).</summary>
    public void AddEntropy(string source, ReadOnlySpan<byte> sample)
    {
        if (sample.IsEmpty) return;

        lock (_lock)
        {
            _counter++;

            var buffer = new byte[_state.Length + sizeof(long) + sample.Length];
            _state.CopyTo(buffer, 0);
            BitConverter.TryWriteBytes(buffer.AsSpan(_state.Length), _counter);
            sample.CopyTo(buffer.AsSpan(_state.Length + sizeof(long)));

            _state = SHA512.HashData(buffer);

            _samples++;
            _bytesMixed += sample.Length;
            _lastAdded = DateTimeOffset.UtcNow;
            _bySource[source] = _bySource.GetValueOrDefault(source) + 1;
        }
    }

    /// <summary>Convenience for small numeric samples (timestamps, counters...).</summary>
    public void AddEntropy(string source, long value) =>
        AddEntropy(source, BitConverter.GetBytes(value));

    /// <summary>
    /// Produce random bytes. Output = HKDF(pool state, salt = fresh OS randomness).
    /// Afterwards the pool is "ratcheted" so past outputs can't be rebuilt from a later leak.
    /// </summary>
    public byte[] GetBytes(int length)
    {
        if (length is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(length), "Length must be 1..4096.");

        var salt = RandomNumberGenerator.GetBytes(32);   // the OS CSPRNG always participates
        byte[] ikm;

        lock (_lock)
        {
            ikm = (byte[])_state.Clone();

            // Ratchet forward: new state depends on old state + the salt.
            var ratchet = new byte[_state.Length + salt.Length + 7];
            _state.CopyTo(ratchet, 0);
            salt.CopyTo(ratchet, _state.Length);
            "ratchet"u8.CopyTo(ratchet.AsSpan(_state.Length + salt.Length));
            _state = SHA512.HashData(ratchet);
        }

        try
        {
            // HKDF-Expand limit is 255 * hashLen, so build long outputs in chunks.
            if (length <= 255 * 32)
                return Derive(ikm, salt, length, 0);

            var result = new byte[length];
            var offset = 0;
            var block = 0;
            while (offset < length)
            {
                var take = Math.Min(255 * 32, length - offset);
                Derive(ikm, salt, take, block++).CopyTo(result, offset);
                offset += take;
            }
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ikm);
        }
    }

    /// <summary>Unbiased integer in [minInclusive, maxExclusive) using rejection sampling.</summary>
    public int GetInt32(int minInclusive, int maxExclusive)
    {
        if (minInclusive >= maxExclusive)
            throw new ArgumentException("min must be less than max.");

        var range = (uint)(maxExclusive - minInclusive);
        if (range == 1) return minInclusive;

        // Largest multiple of range that fits in uint -> reject values above it (avoids modulo bias).
        var limit = uint.MaxValue - (uint.MaxValue % range) - 1;

        while (true)
        {
            var value = BitConverter.ToUInt32(GetBytes(4));
            if (value <= limit)
                return (int)(minInclusive + (value % range));
        }
    }

    public EntropyStats GetStats()
    {
        lock (_lock)
        {
            return new EntropyStats(
                _samples,
                _bytesMixed,
                _lastAdded,
                new Dictionary<string, long>(_bySource));
        }
    }

    private static byte[] Derive(byte[] ikm, byte[] salt, int length, int block) =>
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm,
            length,
            salt,
            System.Text.Encoding.UTF8.GetBytes($"lavarand-v1/{block}"));
}

public sealed record EntropyStats(
    long Samples,
    long BytesMixed,
    DateTimeOffset? LastAdded,
    Dictionary<string, long> BySource);

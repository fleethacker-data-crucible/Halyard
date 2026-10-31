using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Halyard.Core.Serialization;

namespace Halyard.Core.Host
{
    public sealed class OperatorLeaseException : InvalidOperationException
    {
        public OperatorLeaseException(string message) : base(message) { }
    }

    /// <summary>
    /// One writer at a time. Proposals, apply journals and the sandbox itself are shared by everyone who points a Halyard at the same
    /// <c>output/</c> folder (for example on a network share), so a job that writes (apply, rollback, promote, seed, teardown) holds a
    /// lease file while it runs. A second operator gets a clear message naming the holder instead of interleaved writes. A lease is
    /// released when the job ends, and is ignored once it has expired or when its owner is a process that is no longer running on
    /// this machine. Re-entrant inside one engine (several write jobs of one process share one lease file).
    /// </summary>
    public sealed class OperatorLease
    {
        private readonly string _path;
        private readonly string _holder;
        private readonly TimeSpan _ttl;
        private readonly object _lock = new object();
        private int _count;

        public OperatorLease(string path, string holder, TimeSpan ttl)
        {
            _path = path;
            _holder = holder;
            _ttl = ttl;
        }

        public static string DefaultHolder()
        {
            return Environment.UserName + "@" + Environment.MachineName + ":" + Process.GetCurrentProcess().Id;
        }

        public string Path { get { return _path; } }

        /// <summary>Takes (or joins) the lease for <paramref name="reason"/>; dispose the result to release it.</summary>
        public IDisposable Acquire(string reason)
        {
            lock (_lock)
            {
                if (_count == 0) Take(reason);
                _count++;
                return new Token(this);
            }
        }

        /// <summary>Who holds the lease now, or null.</summary>
        public JObject Current()
        {
            JObject info = Read();
            return info == null || IsStale(info) ? null : info;
        }

        private void Take(string reason)
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path));
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using (var stream = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    {
                        var info = new JObject
                        {
                            { "holder", _holder }, { "reason", reason }, { "host", Environment.MachineName }, { "pid", Process.GetCurrentProcess().Id },
                            { "acquiredAt", DateTimeOffset.UtcNow.ToString("o") }, { "expiresAt", DateTimeOffset.UtcNow.Add(_ttl).ToString("o") },
                        };
                        var bytes = new UTF8Encoding(false).GetBytes(info.ToString(Formatting.Indented));
                        stream.Write(bytes, 0, bytes.Length);
                    }
                    return;
                }
                catch (IOException)
                {
                    var existing = Read();
                    if (existing != null && !IsStale(existing))
                        throw new OperatorLeaseException("Another operator is writing to this sandbox: " + (string)existing["holder"] + " (" + (string)existing["reason"] + ", since " + (string)existing["acquiredAt"] + "). Wait for it to finish, then try again.");
                    try { File.Delete(_path); } catch (IOException) { }
                }
            }
            throw new OperatorLeaseException("Could not take the write lease at " + _path + ".");
        }

        private JObject Read()
        {
            try { return File.Exists(_path) ? JObject.Parse(File.ReadAllText(_path)) : null; }
            catch (IOException) { return null; }
            catch (JsonException) { return new JObject { { "holder", "unknown" }, { "expiresAt", DateTimeOffset.MinValue.ToString("o") } }; }
        }

        private static bool IsStale(JObject info)
        {
            DateTimeOffset expires;
            if (!DateTimeOffset.TryParse((string)info["expiresAt"], out expires) || expires <= DateTimeOffset.UtcNow) return true;
            var host = (string)info["host"];
            var pid = (int?)info["pid"];
            if (pid != null && string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            {
                try { Process.GetProcessById(pid.Value); }
                catch (ArgumentException) { return true; }
            }
            return false;
        }

        private void Release()
        {
            lock (_lock)
            {
                if (_count == 0) return;
                _count--;
                if (_count > 0) return;
                var info = Read();
                if (info != null && (string)info["holder"] == _holder)
                {
                    try { File.Delete(_path); } catch (IOException) { }
                }
            }
        }

        private sealed class Token : IDisposable
        {
            private OperatorLease _lease;
            public Token(OperatorLease lease) { _lease = lease; }
            public void Dispose()
            {
                var lease = _lease;
                _lease = null;
                if (lease != null) lease.Release();
            }
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Halyard.Core.Host
{
    public enum JobState
    {
        Queued = 0,
        Running = 1,
        Succeeded = 2,
        Failed = 3,
        Cancelled = 4,
    }

    public sealed class JobInfo
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public JobState State { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public int ProgressDone { get; set; }
        public int ProgressTotal { get; set; }
        public string Message { get; set; }
        public string Error { get; set; }
        public string ErrorCode { get; set; }
        public object Result { get; set; }
        public List<string> Log { get; set; }

        public JobInfo()
        {
            Id = string.Empty;
            Kind = string.Empty;
            Log = new List<string>();
        }
    }

    /// <summary>Progress sink handed to job bodies.</summary>
    public sealed class JobContext
    {
        private readonly Job _job;

        internal JobContext(Job job, CancellationToken token)
        {
            _job = job;
            Cancellation = token;
        }

        public string JobId { get { return _job.Id; } }
        public CancellationToken Cancellation { get; private set; }

        public void Report(int done, int total, string message)
        {
            _job.SetProgress(done, total, message);
        }

        public void Log(string message)
        {
            _job.AppendLog(message);
        }
    }

    internal sealed class Job
    {
        private readonly object _lock = new object();
        private readonly JobInfo _info;
        public readonly CancellationTokenSource Cts = new CancellationTokenSource();

        public Job(string kind)
        {
            _info = new JobInfo { Id = DateTimeOffset.UtcNow.ToString("HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6), Kind = kind, State = JobState.Queued, CreatedAt = DateTimeOffset.UtcNow };
        }

        public string Id { get { return _info.Id; } }
        public string Kind { get { return _info.Kind; } }

        public JobInfo Snapshot()
        {
            lock (_lock)
            {
                return new JobInfo
                {
                    Id = _info.Id, Kind = _info.Kind, State = _info.State, CreatedAt = _info.CreatedAt, StartedAt = _info.StartedAt, FinishedAt = _info.FinishedAt,
                    ProgressDone = _info.ProgressDone, ProgressTotal = _info.ProgressTotal, Message = _info.Message, Error = _info.Error, ErrorCode = _info.ErrorCode,
                    Result = _info.Result, Log = _info.Log.ToList(),
                };
            }
        }

        public void SetProgress(int done, int total, string message)
        {
            lock (_lock)
            {
                _info.ProgressDone = done;
                _info.ProgressTotal = total;
                if (message != null) { _info.Message = message; Append(message); }
            }
        }

        public void AppendLog(string message)
        {
            lock (_lock) Append(message);
        }

        private void Append(string message)
        {
            _info.Log.Add(DateTimeOffset.UtcNow.ToString("HH:mm:ss") + " " + message);
            if (_info.Log.Count > 500) _info.Log.RemoveRange(0, _info.Log.Count - 500);
        }

        public void Start() { lock (_lock) { _info.State = JobState.Running; _info.StartedAt = DateTimeOffset.UtcNow; } }
        public void Succeed(object result) { lock (_lock) { _info.State = JobState.Succeeded; _info.Result = result; _info.FinishedAt = DateTimeOffset.UtcNow; } }
        public void Fail(string error, string code) { lock (_lock) { _info.State = JobState.Failed; _info.Error = error; _info.ErrorCode = code; _info.FinishedAt = DateTimeOffset.UtcNow; } }
        public void Cancel() { lock (_lock) { _info.State = JobState.Cancelled; _info.FinishedAt = DateTimeOffset.UtcNow; } }
        public bool IsFinished { get { lock (_lock) return _info.State == JobState.Succeeded || _info.State == JobState.Failed || _info.State == JobState.Cancelled; } }
    }

    /// <summary>
    /// Runs long operations (sync, seed, teardown, model calls) off the request thread so the UI can poll
    /// progress and the host stays responsive. One job of a given kind runs at a time; the rest are refused
    /// with a clear message rather than queued silently.
    /// </summary>
    public sealed class JobManager
    {
        private readonly ConcurrentDictionary<string, Job> _jobs = new ConcurrentDictionary<string, Job>();
        private readonly Diagnostics _log;

        public JobManager(Diagnostics log)
        {
            _log = log;
        }

        /// <summary>Job kinds that write to the sandbox (or production) and so need the operator lease.</summary>
        public static readonly string[] WriteKinds = { "apply", "rollback", "promote", "seed", "teardown", "intake" };

        /// <summary>When set, a write job takes this lease before it starts and releases it when it ends.</summary>
        public OperatorLease Lease { get; set; }

        public JobInfo Start(string kind, Func<JobContext, Task<object>> body, bool exclusive = true)
        {
            IDisposable lease = null;
            if (Lease != null && Array.IndexOf(WriteKinds, kind) >= 0) lease = Lease.Acquire(kind);
            try { return StartCore(kind, body, exclusive, lease); }
            catch { if (lease != null) lease.Dispose(); throw; }
        }

        private JobInfo StartCore(string kind, Func<JobContext, Task<object>> body, bool exclusive, IDisposable lease)
        {
            if (exclusive)
            {
                var running = _jobs.Values.FirstOrDefault(j => j.Kind == kind && !j.IsFinished);
                if (running != null) throw new InvalidOperationException("A '" + kind + "' job is already running (" + running.Id + "). Wait for it or cancel it first.");
            }
            var job = new Job(kind);
            _jobs[job.Id] = job;
            var ctx = new JobContext(job, job.Cts.Token);
            Task.Run(async () =>
            {
                job.Start();
                _log.Info("JOB_START", kind + " " + job.Id);
                try
                {
                    var result = await body(ctx).ConfigureAwait(false);
                    job.Succeed(result);
                    _log.Info("JOB_DONE", kind + " " + job.Id);
                }
                catch (OperationCanceledException)
                {
                    job.Cancel();
                    _log.Warn("JOB_CANCELLED", kind + " " + job.Id);
                }
                catch (Providers.ProviderHttpException ex)
                {
                    job.Fail(ex.Message, ex.Code);
                    _log.Error("JOB_FAILED", kind + " " + job.Id + ": " + ex.Code + " " + ex.Message);
                }
                catch (Safety.WriteBlockedException ex)
                {
                    job.Fail(ex.Message, "WRITE_BLOCKED");
                    _log.Error("JOB_FAILED", kind + " " + job.Id + ": " + ex.Message);
                }
                catch (Exception ex)
                {
                    job.Fail(ex.Message, ex.GetType().Name);
                    _log.Error("JOB_FAILED", kind + " " + job.Id + ": " + ex.GetType().Name + " " + ex.Message);
                }
                finally
                {
                    if (lease != null) lease.Dispose();
                }
            });
            Prune();
            return job.Snapshot();
        }

        public JobInfo Get(string id)
        {
            Job job;
            return _jobs.TryGetValue(id, out job) && job != null ? job.Snapshot() : null;
        }

        public IReadOnlyList<JobInfo> List(int max = 50)
        {
            return _jobs.Values.Select(j => j.Snapshot()).OrderByDescending(j => j.CreatedAt).Take(max).ToList();
        }

        public bool Cancel(string id)
        {
            Job job;
            if (!_jobs.TryGetValue(id, out job) || job == null || job.IsFinished) return false;
            job.Cts.Cancel();
            return true;
        }

        public bool AnyRunning { get { return _jobs.Values.Any(j => !j.IsFinished); } }

        private void Prune()
        {
            var finished = _jobs.Values.Where(j => j.IsFinished).OrderByDescending(j => j.Snapshot().CreatedAt).Skip(100).ToList();
            foreach (var j in finished)
            {
                Job removed;
                _jobs.TryRemove(j.Id, out removed);
            }
        }
    }
}

// Copyright (c) 2012, Event Store LLP
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are
// met:
// 
// Redistributions of source code must retain the above copyright notice,
// this list of conditions and the following disclaimer.
// Redistributions in binary form must reproduce the above copyright
// notice, this list of conditions and the following disclaimer in the
// documentation and/or other materials provided with the distribution.
// Neither the name of the Event Store LLP nor the names of its
// contributors may be used to endorse or promote products derived from
// this software without specific prior written permission
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
// HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
// 
// Copyright (c) 2012, Event Store LLP
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are
// met:
// 
// Redistributions of source code must retain the above copyright notice,
// this list of conditions and the following disclaimer.
// Redistributions in binary form must reproduce the above copyright
// notice, this list of conditions and the following disclaimer in the
// documentation and/or other materials provided with the distribution.
// Neither the name of the Event Store LLP nor the names of its
// contributors may be used to endorse or promote products derived from
// this software without specific prior written permission
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
// HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
// 
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using EventStore.Common.Log;
using EventStore.Common.Utils;
using EventStore.Core.Data;
using EventStore.Core.DataStructures;
using EventStore.Core.Settings;
using EventStore.Core.TransactionLog;
using EventStore.Core.TransactionLog.LogRecords;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EventStore.Core.Services.Storage.ReaderIndex
{
    public interface IIndexWriter
    {
        long CachedTransInfo { get; }
        long NotCachedTransInfo { get; }

        void Reset();
        CommitCheckResult CheckCommitStartingAt(long transactionPosition, long commitPosition);
        CommitCheckResult CheckCommit(string streamId, int expectedVersion, IEnumerable<Guid> eventIds);
        void PreCommit(CommitLogRecord commit);
        void PreCommit(IList<PrepareLogRecord> commitedPrepares);
        void UpdateTransactionInfo(long transactionId, long logPosition, TransactionInfo transactionInfo);
        TransactionInfo GetTransactionInfo(long writerCheckpoint, long transactionId);
        void PurgeNotProcessedCommitsTill(long checkpoint);
        void PurgeNotProcessedTransactions(long checkpoint);

        Tuple<int, byte[]> GetSoftUndeletedStreamMeta(string streamId, int recreateFromEventNumber);
    }

    public class IndexWriter : IIndexWriter
    {
        private static readonly ILogger Log = LogManager.GetLoggerFor<IndexWriter>();

        public long CachedTransInfo { get { return Interlocked.Read(ref _cachedTransInfo); } }
        public long NotCachedTransInfo { get { return Interlocked.Read(ref _notCachedTransInfo); } }

        private readonly IIndexBackend _indexBackend;
        private readonly IIndexReader _indexReader;

        private readonly IStickyLRUCache<long, TransactionInfo> _transactionInfoCache = new StickyLRUCache<long, TransactionInfo>(ESConsts.TransactionMetadataCacheCapacity);
        private readonly Queue<TransInfo> _notProcessedTrans = new Queue<TransInfo>();
        private readonly BoundedCache<Guid, Tuple<string, int>> _committedEvents = new BoundedCache<Guid, Tuple<string, int>>(int.MaxValue, ESConsts.CommitedEventsMemCacheLimit, x => 16 + 4 + IntPtr.Size + 2*x.Item1.Length);
        private readonly IStickyLRUCache<string, int> _streamVersions = new StickyLRUCache<string, int>(ESConsts.StreamInfoCacheCapacity);
        private readonly IStickyLRUCache<string, byte[]> _streamRawMetas = new StickyLRUCache<string, byte[]>(0); // store nothing flushed, only sticky non-flushed stuff
        private readonly Queue<CommitInfo> _notProcessedCommits = new Queue<CommitInfo>();

        private long _cachedTransInfo;
        private long _notCachedTransInfo;

        public IndexWriter(IIndexBackend indexBackend, IIndexReader indexReader)
        {
            Ensure.NotNull(indexBackend, "indexBackend");
            Ensure.NotNull(indexReader, "indexReader");

            _indexBackend = indexBackend;
            _indexReader = indexReader;
        }

        public void Reset()
        {
            _notProcessedCommits.Clear();
            _streamVersions.Clear();
            _streamRawMetas.Clear();
            _notProcessedTrans.Clear();
            _transactionInfoCache.Clear();
        }

        public CommitCheckResult CheckCommitStartingAt(long transactionPosition, long commitPosition)
        {
            string streamId;
            int expectedVersion;
            using (var reader = _indexBackend.BorrowReader())
            {
                try
                {
                    PrepareLogRecord prepare = GetPrepare(reader, transactionPosition);
                    if (prepare == null)
                    {
                        var message = string.Format("Couldn't read first prepare of to-be-committed transaction. "
                                                    + "Transaction pos: {0}, commit pos: {1}.",
                                                    transactionPosition, commitPosition);
                        Log.Error(message);
                        throw new InvalidOperationException(message);
                    }
                    streamId = prepare.EventStreamId;
                    expectedVersion = prepare.ExpectedVersion;
                }
                catch (InvalidOperationException)
                {
                    return new CommitCheckResult(CommitDecision.InvalidTransaction, string.Empty, -1, -1, -1, false);
                }
            }

            // we should skip prepares without data, as they don't mean anything for idempotency
            // though we have to check deletes, otherwise they always will be considered idempotent :)
            var eventIds = from prepare in GetTransactionPrepares(transactionPosition, commitPosition)
                           where prepare.Flags.HasAnyOf(PrepareFlags.Data | PrepareFlags.StreamDelete)
                           select prepare.EventId;
            return CheckCommit(streamId, expectedVersion, eventIds);
        }
        
        private static PrepareLogRecord GetPrepare(TFReaderLease reader, long logPosition)
        {
            RecordReadResult result = reader.TryReadAt(logPosition);
            if (!result.Success)
                return null;
            if (result.LogRecord.RecordType != LogRecordType.Prepare)
                throw new Exception(string.Format("Incorrect type of log record {0}, expected Prepare record.",
                                                  result.LogRecord.RecordType));
            return (PrepareLogRecord)result.LogRecord;
        }

        public CommitCheckResult CheckCommit(string streamId, int expectedVersion, IEnumerable<Guid> eventIds)
        {
            var curVersion = GetStreamLastEventNumber(streamId);
            if (curVersion == EventNumber.DeletedStream)
                return new CommitCheckResult(CommitDecision.Deleted, streamId, curVersion, -1, -1, false);

            bool isSoftDeleted = GetStreamMetadata(streamId).TruncateBefore == EventNumber.DeletedStream;

            // idempotency checks
            if (expectedVersion == ExpectedVersion.Any)
            {
                var first = true;
                int startEventNumber = -1;
                int endEventNumber = -1;
                foreach (var eventId in eventIds)
                {
                    Tuple<string, int> prepInfo;
                    if (!_committedEvents.TryGetRecord(eventId, out prepInfo) || prepInfo.Item1 != streamId)
                        return new CommitCheckResult(first ? CommitDecision.Ok : CommitDecision.CorruptedIdempotency,
                                                     streamId, curVersion, -1, -1, isSoftDeleted);
                    if (first)
                        startEventNumber = prepInfo.Item2;
                    endEventNumber = prepInfo.Item2;
                    first = false;
                }
                return first /* no data in transaction */
                    ? new CommitCheckResult(CommitDecision.Ok, streamId, curVersion, -1, -1, isSoftDeleted)
                    : new CommitCheckResult(CommitDecision.Idempotent, streamId, curVersion, startEventNumber, endEventNumber, isSoftDeleted);
            }

            if (expectedVersion < curVersion)
            {
                var eventNumber = expectedVersion;
                foreach (var eventId in eventIds)
                {
                    eventNumber += 1;

                    Tuple<string, int> prepInfo;
                    if (_committedEvents.TryGetRecord(eventId, out prepInfo) && prepInfo.Item1 == streamId && prepInfo.Item2 == eventNumber)
                        continue;

                    var res = _indexReader.ReadPrepare(streamId, eventNumber);
                    if (res != null && res.EventId == eventId)
                        continue;

                    var first = eventNumber == expectedVersion + 1;
                    if (!first)
                        return new CommitCheckResult(CommitDecision.CorruptedIdempotency, streamId, curVersion, -1, -1, isSoftDeleted);

                    var decision = isSoftDeleted && expectedVersion == ExpectedVersion.NoStream
                                           ? CommitDecision.Ok
                                           : CommitDecision.WrongExpectedVersion;
                    return new CommitCheckResult(decision, streamId, curVersion, -1, -1, isSoftDeleted);
                }
                return eventNumber == expectedVersion /* no data in transaction */
                    ? new CommitCheckResult(CommitDecision.WrongExpectedVersion, streamId, curVersion, -1, -1, isSoftDeleted)
                    : new CommitCheckResult(CommitDecision.Idempotent, streamId, curVersion, expectedVersion + 1, eventNumber, isSoftDeleted);
            }

            if (expectedVersion > curVersion)
                return new CommitCheckResult(CommitDecision.WrongExpectedVersion, streamId, curVersion, -1, -1, isSoftDeleted);

            // expectedVersion == currentVersion
            return new CommitCheckResult(CommitDecision.Ok, streamId, curVersion, -1, -1, isSoftDeleted);
        }

        public void PreCommit(CommitLogRecord commit)
        {
            string streamId = null;
            int eventNumber = int.MinValue;
            PrepareLogRecord lastPrepare = null;

            foreach (var prepare in GetTransactionPrepares(commit.TransactionPosition, commit.LogPosition))
            {
                if (prepare.Flags.HasNoneOf(PrepareFlags.StreamDelete | PrepareFlags.Data))
                    continue;

                if (streamId == null) 
                    streamId = prepare.EventStreamId;

                if (prepare.EventStreamId != streamId)
                    throw new Exception(string.Format("Expected stream: {0}, actual: {1}.", streamId, prepare.EventStreamId));

                eventNumber = prepare.Flags.HasAnyOf(PrepareFlags.StreamDelete)
                                      ? EventNumber.DeletedStream
                                      : commit.FirstEventNumber + prepare.TransactionOffset;
                lastPrepare = prepare;
                _committedEvents.PutRecord(prepare.EventId, Tuple.Create(streamId, eventNumber), throwOnDuplicate: false);
            }

            if (eventNumber != int.MinValue)
                _streamVersions.Put(streamId, eventNumber, +1);

            if (lastPrepare != null && SystemStreams.IsMetastream(streamId))
            {
                var rawMeta = lastPrepare.Data;
                _streamRawMetas.Put(SystemStreams.OriginalStreamOf(streamId), rawMeta, +1);
            }
        }

        public void PreCommit(IList<PrepareLogRecord> commitedPrepares)
        {
            if (commitedPrepares.Count == 0)
                return;

            var lastPrepare = commitedPrepares[commitedPrepares.Count - 1];
            string streamId = lastPrepare.EventStreamId;
            int eventNumber = int.MinValue;
            foreach (var prepare in commitedPrepares)
            {
                if (prepare.Flags.HasNoneOf(PrepareFlags.StreamDelete | PrepareFlags.Data))
                    continue;

                if (prepare.EventStreamId != streamId)
                    throw new Exception(string.Format("Expected stream: {0}, actual: {1}.", streamId, prepare.EventStreamId));

                eventNumber = prepare.ExpectedVersion + 1; /* for committed prepare expected version is always explicit */
                _committedEvents.PutRecord(prepare.EventId, Tuple.Create(streamId, eventNumber), throwOnDuplicate: false);
            }
            _notProcessedCommits.Enqueue(new CommitInfo(streamId, lastPrepare.LogPosition));
            _streamVersions.Put(streamId, eventNumber, 1);
            if (SystemStreams.IsMetastream(streamId))
            {
                var rawMeta = lastPrepare.Data;
                _streamRawMetas.Put(SystemStreams.OriginalStreamOf(streamId), rawMeta, +1);
            }
        }

        private struct TransInfo
        {
            public readonly long TransactionId;
            public readonly long LogPosition;

            public TransInfo(long transactionId, long logPosition)
            {
                TransactionId = transactionId;
                LogPosition = logPosition;
            }
        }

        private struct CommitInfo
        {
            public readonly string StreamId;
            public readonly long LogPosition;

            public CommitInfo(string streamId, long logPosition)
            {
                StreamId = streamId;
                LogPosition = logPosition;
            }
        }

        private int GetStreamLastEventNumber(string streamId)
        {
            int lastEventNumber;
            if (_streamVersions.TryGet(streamId, out lastEventNumber))
                return lastEventNumber;
            return _indexReader.GetStreamLastEventNumber(streamId);
        }

        private StreamMetadata GetStreamMetadata(string streamId)
        {
            byte[] rawMeta;
            if (_streamRawMetas.TryGet(streamId, out rawMeta))
                return Helper.EatException(() => StreamMetadata.FromJsonBytes(rawMeta), StreamMetadata.Empty);
            return _indexReader.GetStreamMetadata(streamId);
        }

        public void UpdateTransactionInfo(long transactionId, long logPosition, TransactionInfo transactionInfo)
        {
            _notProcessedTrans.Enqueue(new TransInfo(transactionId, logPosition));
            _transactionInfoCache.Put(transactionId, transactionInfo, +1);
        }

        public TransactionInfo GetTransactionInfo(long writerCheckpoint, long transactionId)
        {
            TransactionInfo transactionInfo;
            if (!_transactionInfoCache.TryGet(transactionId, out transactionInfo))
            {
                if (GetTransactionInfoUncached(writerCheckpoint, transactionId, out transactionInfo))
                    _transactionInfoCache.Put(transactionId, transactionInfo, 0);
                else
                    transactionInfo = new TransactionInfo(int.MinValue, null);
                Interlocked.Increment(ref _notCachedTransInfo);
            }
            else
            {
                Interlocked.Increment(ref _cachedTransInfo);
            }
            return transactionInfo;
        }

        private bool GetTransactionInfoUncached(long writerCheckpoint, long transactionId, out TransactionInfo transactionInfo)
        {
            using (var reader = _indexBackend.BorrowReader())
            {
                reader.Reposition(writerCheckpoint);
                SeqReadResult result;
                while ((result = reader.TryReadPrev()).Success)
                {
                    if (result.LogRecord.LogPosition < transactionId)
                        break;
                    if (result.LogRecord.RecordType != LogRecordType.Prepare)
                        continue;
                    var prepare = (PrepareLogRecord)result.LogRecord;
                    if (prepare.TransactionPosition == transactionId)
                    {
                        transactionInfo = new TransactionInfo(prepare.TransactionOffset, prepare.EventStreamId);
                        return true;
                    }
                }
            }
            transactionInfo = new TransactionInfo(int.MinValue, null);
            return false;
        }

        public void PurgeNotProcessedCommitsTill(long checkpoint)
        {
            while (_notProcessedCommits.Count > 0 && _notProcessedCommits.Peek().LogPosition < checkpoint)
            {
                var commitInfo = _notProcessedCommits.Dequeue();
                // decrease stickiness
                _streamVersions.Put(
                    commitInfo.StreamId,
                    x =>
                    {
                        if (!Debugger.IsAttached) Debugger.Launch(); else Debugger.Break();
                        throw new Exception(string.Format("CommitInfo for stream '{0}' is not present!", x));
                    },
                    (streamId, oldVersion) => oldVersion,
                    stickiness: -1);
                if (SystemStreams.IsMetastream(commitInfo.StreamId))
                {
                    _streamRawMetas.Put(
                        SystemStreams.OriginalStreamOf(commitInfo.StreamId),
                        x =>
                        {
                            if (!Debugger.IsAttached) Debugger.Launch(); else Debugger.Break();
                            throw new Exception(string.Format("Original stream CommitInfo for meta-stream '{0}' is not present!",
                                                              SystemStreams.MetastreamOf(x)));
                        },
                        (streamId, oldVersion) => oldVersion,
                        stickiness: -1);
                }
            }
        }

        public void PurgeNotProcessedTransactions(long checkpoint)
        {
            while (_notProcessedTrans.Count > 0 && _notProcessedTrans.Peek().LogPosition < checkpoint)
            {
                var transInfo = _notProcessedTrans.Dequeue();
                // decrease stickiness
                _transactionInfoCache.Put(
                    transInfo.TransactionId,
                    x => { throw new Exception(string.Format("TransInfo for transaction ID {0} is not present!", x)); },
                    (streamId, oldTransInfo) => oldTransInfo,
                    stickiness: -1);
            }
        }

        private IEnumerable<PrepareLogRecord> GetTransactionPrepares(long transactionPos, long commitPos)
        {
            using (var reader = _indexBackend.BorrowReader())
            {
                reader.Reposition(transactionPos);

                // in case all prepares were scavenged, we should not read past Commit LogPosition
                SeqReadResult result;
                while ((result = reader.TryReadNext()).Success && result.RecordPrePosition <= commitPos)
                {
                    if (result.LogRecord.RecordType != LogRecordType.Prepare)
                        continue;

                    var prepare = (PrepareLogRecord)result.LogRecord;
                    if (prepare.TransactionPosition == transactionPos)
                    {
                        yield return prepare;
                        if (prepare.Flags.HasAnyOf(PrepareFlags.TransactionEnd))
                            yield break;
                    }
                }
            }
        }

        public Tuple<int, byte[]> GetSoftUndeletedStreamMeta(string streamId, int recreateFromEventNumber)
        {
            var metastreamId = SystemStreams.MetastreamOf(streamId);
            var metaLastEventNumber = GetStreamLastEventNumber(metastreamId);

            byte[] metaRaw;
            if (!_streamRawMetas.TryGet(streamId, out metaRaw))
                metaRaw = _indexReader.ReadPrepare(metastreamId, metaLastEventNumber).Data;

            try
            {
                var jobj = JObject.Parse(Encoding.UTF8.GetString(metaRaw));
                jobj[SystemMetadata.TruncateBefore] = recreateFromEventNumber;
                using (var memoryStream = new MemoryStream())
                {
                    using (var jsonWriter = new JsonTextWriter(new StreamWriter(memoryStream)))
                    {
                        jobj.WriteTo(jsonWriter);
                    }
                    return Tuple.Create(metaLastEventNumber, memoryStream.ToArray());
                }
            }
            catch (Exception exc)
            {
                var msg = string.Format("Error deserializing to-be-soft-undeleted stream '{0}' metadata. That's wrong!", streamId);
                Log.ErrorException(exc, msg);
                throw new Exception(msg, exc);
            }
        }
    }
}
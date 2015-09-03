﻿using System;
using System.Threading;
using ECommon.Utilities;

namespace EQueue.Broker.Storage
{
    public class TFChunkReader
    {
        internal static long CachedReads;
        internal static long NotCachedReads;

        public const int MaxRetries = 20;

        public long CurrentPosition { get { return _currentDataPosition; } }

        private readonly TFChunkDb _chunkDb;
        private readonly ICheckpoint _writerCheckpoint;
        private long _currentDataPosition;

        public TFChunkReader(TFChunkDb chunkDb, ICheckpoint writerCheckpoint, long initialPosition = 0)
        {
            Ensure.NotNull(chunkDb, "chunkDb");
            Ensure.NotNull(writerCheckpoint, "writerCheckpoint");
            Ensure.Nonnegative(initialPosition, "initialPosition");

            _chunkDb = chunkDb;
            _writerCheckpoint = writerCheckpoint;
            _currentDataPosition = initialPosition;
        }

        public void Reposition(long position)
        {
            _currentDataPosition = position;
        }

        public SeqReadResult TryReadNext()
        {
            return TryReadNextInternal(0);
        }

        private SeqReadResult TryReadNextInternal(int retries)
        {
            while (true)
            {
                var currentDataPosition = _currentDataPosition;
                var writerChk = _writerCheckpoint.Read();
                if (currentDataPosition >= writerChk)
                    return SeqReadResult.Failure;

                var chunk = _chunkDb.Manager.GetChunkFor(currentDataPosition);
                RecordReadResult result;
                try
                {
                    result = chunk.TryReadClosestForward(chunk.ChunkHeader.GetLocalDataPosition(currentDataPosition));
                    CountRead(chunk.IsCached);
                }
                catch (FileBeingDeletedException)
                {
                    if (retries > MaxRetries)
                        throw new Exception(
                            string.Format(
                                "Got a file that was being deleted {0} times from TFChunkDb, likely a bug there.",
                                MaxRetries));
                    return TryReadNextInternal(retries + 1);
                }

                if (result.Success)
                {
                    _currentDataPosition = chunk.ChunkHeader.ChunkDataStartPosition + result.NextPosition;
                    var postPos = result.LogRecord.LogPosition + result.RecordLength + 2 * sizeof(int);
                    var eof = postPos == writerChk;
                    return new SeqReadResult(
                        true, eof, result.LogRecord, result.RecordLength, result.LogRecord.LogPosition, postPos);
                }

                // we are the end of chunk
                _currentDataPosition = chunk.ChunkHeader.ChunkDataEndPosition; // the start of next physical chunk
            }
        }

        public SeqReadResult TryReadPrev()
        {
            return TryReadPrevInternal(0);
        }

        private SeqReadResult TryReadPrevInternal(int retries)
        {
            while (true)
            {
                var currentDataPosition = _currentDataPosition;
                var writerChk = _writerCheckpoint.Read();
                // we allow == writerChk, that means read the very last record
                if (currentDataPosition > writerChk)
                    throw new Exception(string.Format("Requested position {0} is greater than writer checkpoint {1} when requesting to read previous record from TF.", currentDataPosition, writerChk));
                if (currentDataPosition <= 0)
                    return SeqReadResult.Failure;

                var chunk = _chunkDb.Manager.GetChunkFor(currentDataPosition);
                bool readLast = false;
                if (currentDataPosition == chunk.ChunkHeader.ChunkDataStartPosition)
                {
                    // we are exactly at the boundary of physical chunks
                    // so we switch to previous chunk and request TryReadLast
                    readLast = true;
                    chunk = _chunkDb.Manager.GetChunkFor(currentDataPosition - 1);
                }

                RecordReadResult result;
                try
                {
                    result = readLast ? chunk.TryReadLast() : chunk.TryReadClosestBackward(chunk.ChunkHeader.GetLocalDataPosition(currentDataPosition));
                    CountRead(chunk.IsCached);
                }
                catch (FileBeingDeletedException)
                {
                    if (retries > MaxRetries)
                        throw new Exception(string.Format("Got a file that was being deleted {0} times from TFChunkDb, likely a bug there.", MaxRetries));
                    return TryReadPrevInternal(retries + 1);
                }

                if (result.Success)
                {
                    _currentDataPosition = chunk.ChunkHeader.ChunkDataStartPosition + result.NextPosition;
                    var postPos = result.LogRecord.LogPosition + result.RecordLength + 2 * sizeof(int);
                    var eof = postPos == writerChk;
                    return new SeqReadResult(true, eof, result.LogRecord, result.RecordLength, result.LogRecord.LogPosition, postPos);
                }

                // we are the beginning of chunk, so need to switch to previous one
                // to do that we set cur position to the exact boundary position between current and previous chunk, 
                // this will be handled correctly on next iteration
                _currentDataPosition = chunk.ChunkHeader.ChunkDataStartPosition;
            }
        }

        public RecordReadResult TryReadAt(long position)
        {
            return TryReadAtInternal(position, 0);
        }

        private RecordReadResult TryReadAtInternal(long position, int retries)
        {
            var writerChk = _writerCheckpoint.Read();
            if (position >= writerChk)
                return RecordReadResult.Failure;

            var chunk = _chunkDb.Manager.GetChunkFor(position);
            try
            {
                CountRead(chunk.IsCached);
                return chunk.TryReadAt(chunk.ChunkHeader.GetLocalDataPosition(position));
            }
            catch (FileBeingDeletedException)
            {
                if (retries > MaxRetries)
                    throw new FileBeingDeletedException("Been told the file was deleted > MaxRetries times. Probably a problem in db.");
                return TryReadAtInternal(position, retries + 1);
            }
        }

        public bool ExistsAt(long position)
        {
            return ExistsAtInternal(position, 0);
        }

        private bool ExistsAtInternal(long position, int retries)
        {
            var writerChk = _writerCheckpoint.Read();
            if (position >= writerChk)
                return false;

            var chunk = _chunkDb.Manager.GetChunkFor(position);
            try
            {
                CountRead(chunk.IsCached);
                return chunk.ExistsAt(chunk.ChunkHeader.GetLocalDataPosition(position));
            }
            catch (FileBeingDeletedException)
            {
                if (retries > MaxRetries)
                    throw new FileBeingDeletedException("Been told the file was deleted > MaxRetries times. Probably a problem in db.");
                return ExistsAtInternal(position, retries + 1);
            }
        }

        private static void CountRead(bool isCached)
        {
            if (isCached)
                Interlocked.Increment(ref CachedReads);
            else
                Interlocked.Increment(ref NotCachedReads);
        }
    }
}

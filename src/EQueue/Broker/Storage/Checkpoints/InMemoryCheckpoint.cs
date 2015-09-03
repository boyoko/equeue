﻿using System;
using System.Threading;

namespace EQueue.Broker.Storage
{
    public class InMemoryCheckpoint : ICheckpoint
    {
        public string Name { get { return _name; } }

        private long _last;
        private long _lastFlushed;
        private readonly string _name;

        private readonly object _flushLocker = new object();

        public InMemoryCheckpoint(long initialValue) : this(Guid.NewGuid().ToString(), initialValue) { }

        public InMemoryCheckpoint() : this(Guid.NewGuid().ToString(), 0) { }

        public InMemoryCheckpoint(string name, long initValue = 0)
        {
            _last = initValue;
            _lastFlushed = initValue;
            _name = name;
        }

        public void Write(long checkpoint)
        {
            Interlocked.Exchange(ref _last, checkpoint);
        }

        public long Read()
        {
            return Interlocked.Read(ref _lastFlushed);
        }

        public long ReadNonFlushed()
        {
            return Interlocked.Read(ref _last);
        }

        public void Flush()
        {
            var last = Interlocked.Read(ref _last);
            if (last == _lastFlushed)
                return;

            Interlocked.Exchange(ref _lastFlushed, last);

            lock (_flushLocker)
            {
                Monitor.PulseAll(_flushLocker);
            }
        }

        public bool WaitForFlush(TimeSpan timeout)
        {
            lock (_flushLocker)
            {
                return Monitor.Wait(_flushLocker, timeout);
            }
        }

        public void Close()
        {
            //NOOP
        }

        public void Dispose()
        {
            //NOOP
        }
    }
}

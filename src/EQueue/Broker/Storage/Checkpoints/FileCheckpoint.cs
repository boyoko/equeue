﻿using System;
using System.IO;
using System.Threading;

namespace EQueue.Broker.Storage.Checkpoints
{
    public class FileCheckpoint : ICheckpoint
    {
        private readonly string _name;
        private readonly string _filename;
        private readonly FileStream _fileStream;

        private long _last;
        private long _lastFlushed;
        private readonly bool _cached;

        private readonly BinaryWriter _writer;
        private readonly BinaryReader _reader;

        private readonly object _flushLocker = new object();

        public FileCheckpoint(string filename)
            : this(filename, Guid.NewGuid().ToString())
        {
        }

        public FileCheckpoint(string filename, string name, bool cached = false, bool mustExist = false, long initValue = 0)
        {
            _filename = filename;
            _name = name;
            _cached = cached;
            var old = File.Exists(filename);
            _fileStream = new FileStream(_filename,
                                         mustExist ? FileMode.Open : FileMode.OpenOrCreate,
                                         FileAccess.ReadWrite,
                                         FileShare.ReadWrite);
            if (_fileStream.Length != 8)
                _fileStream.SetLength(8);
            _reader = new BinaryReader(_fileStream);
            _writer = new BinaryWriter(_fileStream);
            if (old)
                _last = _lastFlushed = ReadCurrent();
            else
            {
                _last = initValue;
                Flush();
            }
        }

        private long ReadCurrent()
        {
            _fileStream.Seek(0, SeekOrigin.Begin);
            return _reader.ReadInt64();
        }

        public void Close()
        {
            Flush();
            _reader.Close();
            _writer.Close();
            _fileStream.Close();
        }

        public string Name
        {
            get { return _name; }
        }

        public void Write(long checkpoint)
        {
            Interlocked.Exchange(ref _last, checkpoint);
        }

        public void Flush()
        {
            var last = Interlocked.Read(ref _last);
            if (last == _lastFlushed)
                return;

            _fileStream.Seek(0, SeekOrigin.Begin);
            _writer.Write(last);

            _fileStream.FlushToDisk();
            Interlocked.Exchange(ref _lastFlushed, last);

            lock (_flushLocker)
            {
                Monitor.PulseAll(_flushLocker);
            }
        }

        public long Read()
        {
            return _cached ? Interlocked.Read(ref _lastFlushed) : ReadCurrent();
        }

        public long
            ReadNonFlushed()
        {
            return Interlocked.Read(ref _last);
        }

        public bool WaitForFlush(TimeSpan timeout)
        {
            lock (_flushLocker)
            {
                return Monitor.Wait(_flushLocker, timeout);
            }
        }

        public void Dispose()
        {
            Close();
        }
    }
}

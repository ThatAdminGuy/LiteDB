﻿using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;

namespace LiteDB.Engine
{
    /// <summary>
    /// A public class that take care of all engine data structure access - it´s basic implementation of a NoSql database
    /// Its isolated from complete solution - works on low level only (no linq, no poco... just Bson objects)
    /// </summary>
    public partial class LiteEngine : IDisposable
    {
        #region Services instances

        private readonly Logger _log;

        private readonly LockService _locker;

        private readonly DataFileService _dataFile;

        private readonly WalService _wal;

        private HeaderPage _header;

        private readonly BsonReader _bsonReader;

        private readonly BsonWriter _bsonWriter;

        private readonly EngineSettings _settings;

        private bool _shutdown = false;

        #region TempDB

        private LiteEngine _tempdb = null;
        private bool _disposeTempdb = false;

        /// <summary>
        /// Get/Set temporary engine database used to sort data
        /// </summary>
        public LiteEngine TempDB
        {
            get
            {
                if (_tempdb == null)
                {
                    _tempdb = new LiteEngine(new EngineSettings { FileName = ":temp:" });
                }

                return _tempdb;
            }
            set
            {
                if (_tempdb != null) throw LiteException.TempEngineAlreadyDefined();

                _tempdb = value;
                _disposeTempdb = false;
            }
        }

        #endregion

        /// <summary>
        /// Get log instance for debug operations
        /// </summary>
        public Logger Log => _log;

        /// <summary>
        /// Get database settings - settings 
        /// </summary>
        public EngineSettings Settings => _settings;

        #endregion

        #region Ctor

        /// <summary>
        /// Initialize LiteEngine using connection memory database
        /// </summary>
        public LiteEngine()
            : this(new EngineSettings())
        {
        }

        /// <summary>
        /// Initialize LiteEngine using connection string using key=value; parser
        /// </summary>
        public LiteEngine(string filename)
            : this (new EngineSettings { FileName = filename })
        {
        }

        /// <summary>
        /// Initialize LiteEngine using initial engine settings
        /// </summary>
        public LiteEngine(EngineSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            try
            {
                // create factory based on connection string if there is no factory
                _log = settings.Log ?? new Logger(settings.LogLevel);

                _log.Info($"initializing database '{settings.FileName}'");

                // get engine setting
                _settings = settings;

                _bsonReader = new BsonReader(settings.UtcDate);
                _bsonWriter = new BsonWriter();

                _locker = new LockService(settings.Timeout, _log);

                // get disk factory from engine settings and open/create datafile/walfile
                var factory = settings.GetDiskFactory();

                _dataFile = new DataFileService(factory, settings.Timeout, settings.InitialSize, settings.UtcDate, _log);

                // initialize wal service
                _wal = new WalService(_locker, _dataFile, factory, settings.Timeout, settings.LimitSize, settings.UtcDate, _log);

                // if WAL file have content, must run checkpoint
                _wal.Checkpoint(false, null, false);

                // load header page
                _header = _dataFile.ReadPage(0) as HeaderPage;

                // register system collections
                this.InitializeSystemCollections();

                // if fileVersion are less than current version, must upgrade datafile
                if (_header.FileVersion < HeaderPage.FILE_VERSION)
                {
                    this.Upgrade();
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex);

                // explicit dispose
                this.Dispose();
                throw;
            }
        }

        #endregion

        /// <summary>
        /// Request a WAL checkpoint
        /// </summary>
        public void Checkpoint(bool delete) => _wal.Checkpoint(delete, _header, true);

        /// <summary>
        /// Shutdown database
        /// - After dispose engine, no more new transaction
        /// - All transation will throw shutdown exception and do rollback
        /// - Wait for async write with full flush to disk
        /// - Do checkpoint (sync)
        /// - Dispose disks
        /// </summary>
        public void Dispose()
        {
            // this method can be called from Ctor, so many 
            // of this members can be null yet. 
            if (_shutdown) return;

            // start shutdown operation
            _shutdown = true;

            _log.Info("shutting down the database");

            // mark all transaction as shotdown status
            foreach (var trans in _transactions?.Values)
            {
                trans.Shutdown();
            }

            if (_settings.CheckpointOnShutdown)
            {
                // do checkpoint (with no-lock check) and delete wal file (will dispose wal file too)
                _wal?.Checkpoint(true, null, false);
            }

            // dispose lockers
            _locker?.Dispose();

            // close all Dispose services
            _dataFile?.Dispose();
            _wal?.WalFile?.Dispose();

            if (_disposeTempdb)
            {
                _tempdb?.Dispose();
            }
        }
    }
}
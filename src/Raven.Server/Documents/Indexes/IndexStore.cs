﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Raven.Abstractions.Data;
using Raven.Abstractions.Indexing;
using Raven.Client.Data.Indexes;
using Raven.Server.Documents.Indexes.Auto;
using Raven.Server.Utils;

namespace Raven.Server.Documents.Indexes
{
    public class IndexStore : IDisposable
    {
        private readonly DocumentDatabase _documentDatabase;

        private readonly CollectionOfIndexes _indexes = new CollectionOfIndexes();

        private readonly object _locker = new object();

        private bool _initialized;

        private string _path;

        public IndexStore(DocumentDatabase documentDatabase)
        {
            _documentDatabase = documentDatabase;
        }

        public void Initialize()
        {
            if (_initialized)
                throw new InvalidOperationException();

            lock (_locker)
            {
                if (_initialized)
                    throw new InvalidOperationException();

                if (_documentDatabase.Configuration.Indexing.RunInMemory == false)
                {
                    _path = _documentDatabase.Configuration.Indexing.IndexStoragePath;
                    if (Directory.Exists(_path) == false && _documentDatabase.Configuration.Indexing.RunInMemory == false)
                        Directory.CreateDirectory(_path);
                }

                Task.Factory.StartNew(OpenIndexes, TaskCreationOptions.LongRunning);

                _initialized = true;
            }
        }

        public Index GetIndex(int id)
        {
            Index index;
            if (_indexes.TryGetById(id, out index) == false)
                return null;

            return index;
        }

        public Index GetIndex(string name)
        {
            Index index;
            if (_indexes.TryGetByName(name, out index) == false)
                return null;

            return index;
        }

        public int CreateIndex(AutoMapIndexDefinition definition)
        {
            lock (_locker)
            {
                Index existingIndex;
                ValidateIndexDefinition(definition, out existingIndex);

                switch (GetIndexCreationOptions(definition, existingIndex))
                {
                    case IndexCreationOptions.Noop:
                        return existingIndex.IndexId;
                    case IndexCreationOptions.UpdateWithoutUpdatingCompiledIndex:
                        throw new NotImplementedException(); // TODO [ppekrol]
                    case IndexCreationOptions.Update:
                        DeleteIndex(existingIndex.IndexId);
                        break;
                }

                var indexId = _indexes.GetNextIndexId();

                var index = AutoMapIndex.CreateNew(indexId, definition, _documentDatabase);
                index.Start();

                _indexes.Add(index);

                _documentDatabase.Notifications.RaiseNotifications(new IndexChangeNotification
                {
                    Name = index.Name,
                    Type = IndexChangeTypes.IndexAdded
                });

                return indexId;
            }
        }

        private static IndexCreationOptions GetIndexCreationOptions(IndexDefinitionBase indexDefinition, Index existingIndex)
        {
            if (existingIndex == null)
                return IndexCreationOptions.Create;

            //if (existingIndex.Definition.IsTestIndex) // TODO [ppekrol]
            //    return IndexCreationOptions.Update;

            var equals = existingIndex.Definition.Equals(indexDefinition, ignoreFormatting: true, ignoreMaxIndexOutputs: true);
            if (equals)
                return IndexCreationOptions.Noop;

            return existingIndex.Definition.Equals(indexDefinition, ignoreFormatting: true, ignoreMaxIndexOutputs: true)
                       ? IndexCreationOptions.UpdateWithoutUpdatingCompiledIndex
                       : IndexCreationOptions.Update;
        }

        private void ValidateIndexDefinition(IndexDefinitionBase indexDefinition, out Index existingIndex)
        {
            ValidateIndexName(indexDefinition.Name);

            if (_indexes.TryGetByName(indexDefinition.Name, out existingIndex))
            {
                switch (existingIndex.Definition.LockMode)
                {
                    case IndexLockMode.SideBySide:
                        throw new NotImplementedException(); // TODO [ppekrol]
                    case IndexLockMode.LockedIgnore:
                        return;
                    case IndexLockMode.LockedError:
                        throw new InvalidOperationException("Can not overwrite locked index: " + indexDefinition.Name);
                }
            }
        }

        private void ValidateIndexName(string name)
        {
            if (name.StartsWith("dynamic/", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Index name '{name.Replace("//", "__")}' not permitted. Index names starting with dynamic_ or dynamic/ are reserved!", nameof(name));
            }

            if (name.Equals("dynamic", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Index name '{name.Replace("//", "__")}' not permitted. Index name dynamic is reserved!", nameof(name));
            }

            if (name.Contains("//"))
            {
                throw new ArgumentException($"Index name '{name.Replace("//", "__")}' not permitted. Index name cannot contain // (double slashes)", nameof(name));
            }
        }

        public int ResetIndex(string name)
        {
            var index = GetIndex(name);
            if (index == null)
                throw new InvalidOperationException("There is no index with name: " + name);

            return ResetIndexInternal(index);
        }

        public int ResetIndex(int id)
        {
            var index = GetIndex(id);
            if (index == null)
                throw new InvalidOperationException("There is no index with id: " + id);

            return ResetIndexInternal(index);
        }

        public void DeleteIndex(string name)
        {
            var index = GetIndex(name);
            if (index == null)
                throw new InvalidOperationException("There is no index with name: " + name);

            DeleteIndexInternal(index.IndexId);
        }

        public void DeleteIndex(int id)
        {
            DeleteIndexInternal(id);
        }

        private void DeleteIndexInternal(int id)
        {
            lock (_locker)
            {
                Index index;
                if (_indexes.TryRemoveById(id, out index) == false)
                    throw new InvalidOperationException("There is no index with id: " + id);

                try
                {
                    index.Dispose();
                }
                catch (Exception)
                {
                    //TODO [ppekrol] log
                }

                _documentDatabase.Notifications.RaiseNotifications(new IndexChangeNotification
                {
                    Name = index.Name,
                    Type = IndexChangeTypes.IndexRemoved
                });

                if (_documentDatabase.Configuration.Indexing.RunInMemory)
                    return;

                var path = Path.Combine(_documentDatabase.Configuration.Indexing.IndexStoragePath, id.ToString());
                IOExtensions.DeleteDirectory(path);
            }
        }

        public void StartIndexing()
        {
            StartIndexing(_indexes);
        }

        public void StartMapIndexes()
        {
            StartIndexing(_indexes.Where(x => x.Type == IndexType.AutoMap || x.Type == IndexType.Map));
        }

        public void StartMapReduceIndexes()
        {
            StartIndexing(_indexes.Where(x => x.Type == IndexType.MapReduce));
        }

        private void StartIndexing(IEnumerable<Index> indexes)
        {
            if (_documentDatabase.Configuration.Indexing.Disabled)
                return;

            Parallel.ForEach(indexes, index => index.Start());
        }

        public void StartIndex(string name)
        {
            var index = GetIndex(name);
            if (index == null)
                throw new InvalidOperationException("There is no index with name: " + name);

            index.Start();
        }

        public void StopIndex(string name)
        {
            var index = GetIndex(name);
            if (index == null)
                throw new InvalidOperationException("There is no index with name: " + name);

            index.Stop();
        }

        public void StopIndexing()
        {
            StopIndexing(_indexes);
        }

        public void StopMapIndexes()
        {
            StopIndexing(_indexes.Where(x => x.Type == IndexType.AutoMap || x.Type == IndexType.Map));
        }

        public void StopMapReduceIndexes()
        {
            StopIndexing(_indexes.Where(x => x.Type == IndexType.MapReduce));
        }

        private void StopIndexing(IEnumerable<Index> indexes)
        {
            if (_documentDatabase.Configuration.Indexing.Disabled)
                return;

            Parallel.ForEach(indexes, index => index.Stop());
        }

        public void Dispose()
        {
            //FlushMapIndexes();
            //FlushReduceIndexes();

            foreach (var index in _indexes)
            {
                index.Dispose();
            }
        }

        private int ResetIndexInternal(Index index)
        {
            lock (_locker)
            {
                DeleteIndex(index.IndexId);

                switch (index.Type)
                {
                    case IndexType.AutoMap:
                        var autoMapIndex = (AutoMapIndex)index;
                        var autoMapIndexDefinition = autoMapIndex.Definition;
                        return CreateIndex(autoMapIndexDefinition);
                    default:
                        throw new NotSupportedException(index.Type.ToString());
                }
            }
        }

        private void OpenIndexes()
        {
            if (_documentDatabase.Configuration.Indexing.RunInMemory)
                return;

            foreach (var indexDirectory in new DirectoryInfo(_path).GetDirectories())
            {
                int indexId;
                if (int.TryParse(indexDirectory.Name, out indexId) == false)
                    continue;

                var index = Index.Open(indexId, _documentDatabase);
                _indexes.Add(index);
            }
        }

        public List<AutoMapIndexDefinition> GetAutoIndexDefinitionsForCollection(string collection)
        {
            return _indexes.GetDefinitionsOfTypeForCollection<AutoMapIndexDefinition>(collection);
        }

        public IEnumerable<Index> GetIndexesForCollection(string collection)
        {
            return _indexes.GetForCollection(collection);
        }

        public IEnumerable<Index> GetIndexes()
        {
            return _indexes;
        }
    }
}
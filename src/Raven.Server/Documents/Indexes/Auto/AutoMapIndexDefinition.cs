﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Raven.Abstractions.Indexing;
using Raven.Client.Indexing;
using Raven.Server.Json;
using Raven.Server.ServerWide.Context;
using Voron;

namespace Raven.Server.Documents.Indexes.Auto
{
    public class AutoMapIndexDefinition : IndexDefinitionBase
    {
        public AutoMapIndexDefinition(string collection, IndexField[] fields)
            : base(FindIndexName(collection, fields), new[] { collection }, IndexLockMode.Unlock, fields)
        {
            if (string.IsNullOrEmpty(collection))
                throw new ArgumentNullException(nameof(collection));

            if (fields.Length == 0)
                throw new ArgumentException("You must specify at least one field.", nameof(fields));
        }

        public int CountOfMapFields => MapFields.Length;

        private static string FindIndexName(string collection, IReadOnlyCollection<IndexField> fields)
        {
            var combinedFields = string.Join("And", fields.Select(x => IndexField.ReplaceInvalidCharactersInFieldName(x.Name)).OrderBy(x => x));

            var sortOptions = fields.Where(x => x.SortOption != null).Select(x => x.Name).ToArray();
            if (sortOptions.Length > 0)
            {
                combinedFields = $"{combinedFields}SortBy{string.Join(string.Empty, sortOptions.OrderBy(x => x))}";
            }

            var highlighted = fields.Where(x => x.Highlighted).Select(x => x.Name).ToArray();
            if (highlighted.Length > 0)
            {
                combinedFields = $"{combinedFields}Highlight{string.Join("", highlighted.OrderBy(x => x))}";
            }

            return fields.Count == 0 ? $"Auto/{collection}" : $"Auto/{collection}/By{combinedFields}";
        }

        public override void Persist(TransactionOperationContext context)
        {
            var tree = context.Transaction.InnerTransaction.CreateTree("Definition");
            using (var stream = new MemoryStream())
            using (var writer = new BlittableJsonTextWriter(context, stream))
            {
                writer.WriteStartObject();

                var collection = Collections.First();

                writer.WritePropertyName(context.GetLazyString(nameof(Collections)));
                writer.WriteStartArray();
                writer.WriteString(context.GetLazyString(collection));
                writer.WriteEndArray();
                writer.WriteComma();
                writer.WritePropertyName(context.GetLazyString(nameof(LockMode)));
                writer.WriteInteger((int)LockMode);
                writer.WriteComma();

                writer.WritePropertyName(context.GetLazyString(nameof(MapFields)));
                writer.WriteStartArray();
                var first = true;
                foreach (var field in MapFields)
                {
                    if (first == false)
                        writer.WriteComma();

                    writer.WriteStartObject();

                    writer.WritePropertyName(context.GetLazyString(nameof(field.Name)));
                    writer.WriteString(context.GetLazyString(field.Name));
                    writer.WriteComma();

                    writer.WritePropertyName(context.GetLazyString(nameof(field.Highlighted)));
                    writer.WriteBool(field.Highlighted);
                    writer.WriteComma();

                    writer.WritePropertyName(context.GetLazyString(nameof(field.SortOption)));
                    writer.WriteInteger((int)(field.SortOption ?? SortOptions.None));

                    writer.WriteEndObject();

                    first = false;
                }
                writer.WriteEndArray();

                writer.WriteEndObject();

                writer.Flush();

                stream.Position = 0;
                tree.Add(DefinitionSlice, stream.ToArray());
            }
        }

        protected override void FillIndexDefinition(IndexDefinition indexDefinition)
        {
            var map = $"{Collections.First()}:[{string.Join(";", MapFields.Select(x => $"<Name:{x.Name},Sort:{x.SortOption},Highlight:{x.Highlighted}>"))}]";
            indexDefinition.Maps.Add(map);
        }

        public override bool Equals(IndexDefinitionBase other, bool ignoreFormatting, bool ignoreMaxIndexOutputs)
        {
            var otherDefinition = other as AutoMapIndexDefinition;
            if (otherDefinition == null)
                return false;

            if (ReferenceEquals(this, other))
                return true;

            if (string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase) == false)
                return false;

            if (CountOfMapFields != otherDefinition.CountOfMapFields)
                return false;

            if (Collections.SequenceEqual(otherDefinition.Collections) == false)
                return false;

            if (MapFields.SequenceEqual(otherDefinition.MapFields) == false)
                return false;

            return true;
        }

        public static AutoMapIndexDefinition Load(StorageEnvironment environment)
        {
            using (var pool = new UnmanagedBuffersPool(nameof(AutoMapIndexDefinition)))
            using (var context = new MemoryOperationContext(pool))
            using (var tx = environment.ReadTransaction())
            {
                var tree = tx.CreateTree("Definition");
                var result = tree.Read(DefinitionSlice);
                if (result == null)
                    return null;

                using (var reader = context.ReadForDisk(result.Reader.AsStream(), string.Empty))
                {
                    int lockModeAsInt;
                    reader.TryGet(nameof(LockMode), out lockModeAsInt);

                    BlittableJsonReaderArray jsonArray;
                    reader.TryGet(nameof(Collections), out jsonArray);

                    var collection = jsonArray.GetStringByIndex(0);

                    reader.TryGet(nameof(MapFields), out jsonArray);

                    var fields = new IndexField[jsonArray.Length];
                    for (var i = 0; i < jsonArray.Length; i++)
                    {
                        var json = jsonArray.GetByIndex<BlittableJsonReaderObject>(i);

                        string name;
                        json.TryGet(nameof(IndexField.Name), out name);

                        bool highlighted;
                        json.TryGet(nameof(IndexField.Highlighted), out highlighted);

                        int sortOptionAsInt;
                        json.TryGet(nameof(IndexField.SortOption), out sortOptionAsInt);

                        var field = new IndexField
                        {
                            Name = name,
                            Highlighted = highlighted,
                            Storage = FieldStorage.No,
                            SortOption = (SortOptions?)sortOptionAsInt,
                            Indexing = FieldIndexing.Default
                        };

                        fields[i] = field;
                    }

                    return new AutoMapIndexDefinition(collection, fields)
                    {
                        LockMode = (IndexLockMode)lockModeAsInt
                    };
                }
            }
        }
    }
}
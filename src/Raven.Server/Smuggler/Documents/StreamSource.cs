﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Raven.Client;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Smuggler;
using Raven.Client.Util;
using Raven.Server.Documents;
using Raven.Server.Documents.Handlers;
using Raven.Server.ServerWide.Context;
using Raven.Server.Smuggler.Documents.Data;
using Raven.Server.Smuggler.Documents.Processors;
using Sparrow;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Sparrow.Logging;
using Sparrow.Utils;
using Voron;
using Size = Sparrow.Size;

namespace Raven.Server.Smuggler.Documents
{
    public class StreamSource : ISmugglerSource, IDisposable
    {
        private readonly PeepingTomStream _peepingTomStream;
        private readonly DocumentsOperationContext _context;
        private readonly Logger _log;

        private JsonOperationContext.ManagedPinnedBuffer _buffer;
        private JsonOperationContext.ReturnBuffer _returnBuffer;
        private JsonOperationContext.ManagedPinnedBuffer _writeBuffer;
        private JsonOperationContext.ReturnBuffer _returnWriteBuffer;
        private JsonParserState _state;
        private UnmanagedJsonParser _parser;
        private DatabaseItemType? _currentType;

        private SmugglerResult _result;

        private BuildVersionType _buildVersionType;
        private bool _readLegacyEtag;

        private Size _totalObjectsRead = new Size(0, SizeUnit.Bytes);

        public StreamSource(Stream stream, DocumentsOperationContext context, DocumentDatabase database)
        {
            _peepingTomStream = new PeepingTomStream(stream, context);
            _context = context;
            _log = LoggingSource.Instance.GetLogger<StreamSource>(database.Name);
        }

        public IDisposable Initialize(DatabaseSmugglerOptions options, SmugglerResult result, out long buildVersion)
        {
            _result = result;
            _returnBuffer = _context.GetManagedBuffer(out _buffer);
            _state = new JsonParserState();
            _parser = new UnmanagedJsonParser(_context, _state, "file");

            if (UnmanagedJsonParserHelper.Read(_peepingTomStream, _parser, _state, _buffer) == false)
                UnmanagedJsonParserHelper.ThrowInvalidJson("Unexpected end of json.", _peepingTomStream, _parser);

            if (_state.CurrentTokenType != JsonParserToken.StartObject)
                UnmanagedJsonParserHelper.ThrowInvalidJson("Expected start object, but got " + _state.CurrentTokenType, _peepingTomStream, _parser);

            buildVersion = ReadBuildVersion();
            _buildVersionType = BuildVersion.Type(buildVersion);
            _readLegacyEtag = options.ReadLegacyEtag;

            return new DisposableAction(() =>
            {
                _parser.Dispose();
                _returnBuffer.Dispose();
                _returnWriteBuffer.Dispose();
            });
        }

        public DatabaseItemType GetNextType()
        {
            if (_currentType != null)
            {
                var currentType = _currentType.Value;
                _currentType = null;

                return currentType;
            }

            var type = ReadType();
            if (type == null)
                return DatabaseItemType.None;

            while (type.Equals("Transformers", StringComparison.OrdinalIgnoreCase))
            {
                SkipArray();
                type = ReadType();
                if (type == null)
                    break;
            }

            return GetType(type);
        }

        public IDisposable GetCmpXchg(out IEnumerable<(string key, long index, BlittableJsonReaderObject value)> cmpXchg)
        {
            cmpXchg = InternalGetCmpXchg();
            return null;
        }

        private unsafe void SetBuffer(UnmanagedJsonParser parser, LazyStringValue value)
        {
            parser.SetBuffer(value.Buffer, value.Size);
        }

        private IEnumerable<(string key, long index, BlittableJsonReaderObject value)> InternalGetCmpXchg()
        {
            var state = new JsonParserState();
            using (var parser = new UnmanagedJsonParser(_context, state, "Import/CompareExchagne"))
            using (var builder = new BlittableJsonDocumentBuilder(_context,
                BlittableJsonDocumentBuilder.UsageMode.ToDisk, "Import/CompareExchange", parser, state))
            {
                foreach (var reader in ReadArray())
                {
                    using (reader)
                    {

                        if (reader.TryGet("Key", out string key) == false ||
                            reader.TryGet("Value", out LazyStringValue value) == false)
                        {
                            _result.CmpXchg.ErroredCount++;
                            _result.AddWarning("Could not read compare exchange entry.");

                            continue;
                        }

                        builder.ReadNestedObject();
                        SetBuffer(parser, value);
                        parser.Read();
                        builder.Read();
                        builder.FinalizeDocument();
                        yield return (key, 0, builder.CreateReader());

                        builder.Renew("import/cmpxchg", BlittableJsonDocumentBuilder.UsageMode.ToDisk);
                    }
                }
            }
        }

        public long SkipType(DatabaseItemType type, Action<long> onSkipped)
        {
            switch (type)
            {
                case DatabaseItemType.None:
                    return 0;
                case DatabaseItemType.Documents:
                case DatabaseItemType.RevisionDocuments:
                case DatabaseItemType.Tombstones:
                case DatabaseItemType.Conflicts:
                case DatabaseItemType.Indexes:
                case DatabaseItemType.Identities:
                case DatabaseItemType.CompareExchange:
                case DatabaseItemType.LegacyDocumentDeletions:
                case DatabaseItemType.LegacyAttachmentDeletions:
                    return SkipArray(onSkipped);
                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
        }

        public IEnumerable<DocumentItem> GetDocuments(List<string> collectionsToExport, INewDocumentActions actions)
        {
            return ReadDocuments(actions);
        }

        public IEnumerable<DocumentItem> GetRevisionDocuments(List<string> collectionsToExport, INewDocumentActions actions)
        {
            return ReadDocuments(actions);
        }

        public IEnumerable<DocumentItem> GetLegacyAttachments(INewDocumentActions actions)
        {
            return ReadLegacyAttachments(actions);
        }

        public IEnumerable<string> GetLegacyAttachmentDeletions()
        {
            foreach (var id in ReadLegacyDeletions())
                yield return $"{DummyDocumentPrefix}{id}";
        }

        public IEnumerable<string> GetLegacyDocumentDeletions()
        {
            return ReadLegacyDeletions();
        }

        public IEnumerable<DocumentTombstone> GetTombstones(List<string> collectionsToExport, INewDocumentActions actions)
        {
            return ReadTombstones(actions);
        }

        public IEnumerable<DocumentConflict> GetConflicts(List<string> collectionsToExport, INewDocumentActions actions)
        {
            return ReadConflicts(actions);
        }

        public IEnumerable<IndexDefinitionAndType> GetIndexes()
        {
            foreach (var reader in ReadArray())
            {
                using (reader)
                {
                    IndexType type;
                    object indexDefinition;

                    try
                    {
                        indexDefinition = IndexProcessor.ReadIndexDefinition(reader, _buildVersionType, out type);
                    }
                    catch (Exception e)
                    {
                        _result.Indexes.ErroredCount++;
                        _result.AddWarning($"Could not read index definition. Message: {e.Message}");

                        continue;
                    }

                    yield return new IndexDefinitionAndType
                    {
                        Type = type,
                        IndexDefinition = indexDefinition
                    };
                }
            }
        }

        public IDisposable GetIdentities(out IEnumerable<(string Prefix, long Value)> identities)
        {
            identities = InternalGetIdentities();
            return null;
        }

        private IEnumerable<(string Prefix, long Value)> InternalGetIdentities()
        {
            foreach (var reader in ReadArray())
            {
                using (reader)
                {
                    if (reader.TryGet("Key", out string identityKey) == false ||
                        reader.TryGet("Value", out string identityValueString) == false ||
                        long.TryParse(identityValueString, out long identityValue) == false)
                    {
                        _result.Identities.ErroredCount++;
                        _result.AddWarning("Could not read identity.");

                        continue;
                    }

                    yield return (identityKey, identityValue);
                }
            }
        }

        private unsafe string ReadType()
        {
            if (UnmanagedJsonParserHelper.Read(_peepingTomStream, _parser, _state, _buffer) == false)
                UnmanagedJsonParserHelper.ThrowInvalidJson("Unexpected end of object when reading type", _peepingTomStream, _parser);

            if (_state.CurrentTokenType == JsonParserToken.EndObject)
                return null;

            if (_state.CurrentTokenType != JsonParserToken.String)
                UnmanagedJsonParserHelper.ThrowInvalidJson("Expected property type to be string, but was " + _state.CurrentTokenType, _peepingTomStream, _parser);

            return _context.AllocateStringValue(null, _state.StringBuffer, _state.StringSize).ToString();
        }

        private void ReadObject(BlittableJsonDocumentBuilder builder)
        {
            UnmanagedJsonParserHelper.ReadObject(builder, _peepingTomStream, _parser, _buffer);

            _totalObjectsRead.Add(builder.SizeInBytes, SizeUnit.Bytes);
        }

        private long ReadBuildVersion()
        {
            var type = ReadType();
            if (type == null)
                return 0;

            if (type.Equals("BuildVersion", StringComparison.OrdinalIgnoreCase) == false)
            {
                _currentType = GetType(type);
                return 0;
            }

            if (UnmanagedJsonParserHelper.Read(_peepingTomStream, _parser, _state, _buffer) == false)
                UnmanagedJsonParserHelper.ThrowInvalidJson("Unexpected end of json.", _peepingTomStream, _parser);

            if (_state.CurrentTokenType != JsonParserToken.Integer)
                UnmanagedJsonParserHelper.ThrowInvalidJson("Expected integer BuildVersion, but got " + _state.CurrentTokenType, _peepingTomStream, _parser);

            return _state.Long;
        }

        private long SkipArray(Action<long> onSkipped = null)
        {
            var count = 0L;
            foreach (var _ in ReadArray())
            {
                using (_)
                {
                    count++; //skipping
                    onSkipped?.Invoke(count);
                }
            }

            return count;
        }

        private IEnumerable<BlittableJsonReaderObject> ReadArray(INewDocumentActions actions = null)
        {
            if (UnmanagedJsonParserHelper.Read(_peepingTomStream, _parser, _state, _buffer) == false)
                UnmanagedJsonParserHelper.ThrowInvalidJson("Unexpected end of json", _peepingTomStream, _parser);

            if (_state.CurrentTokenType != JsonParserToken.StartArray)
                UnmanagedJsonParserHelper.ThrowInvalidJson("Expected start array, got " + _state.CurrentTokenType, _peepingTomStream, _parser);

            var builder = CreateBuilder(_context, null);
            try
            {
                while (true)
                {
                    if (UnmanagedJsonParserHelper.Read(_peepingTomStream, _parser, _state, _buffer) == false)
                        UnmanagedJsonParserHelper.ThrowInvalidJson("Unexpected end of json while reading array", _peepingTomStream, _parser);

                    if (_state.CurrentTokenType == JsonParserToken.EndArray)
                        break;
                    if (actions != null)
                    {
                        var oldContext = _context;
                        var context = actions.GetContextForNewDocument();
                        if (_context != oldContext)
                        {
                            builder.Dispose();
                            builder = CreateBuilder(context, null);
                        }
                    }
                    builder.Renew("import/object", BlittableJsonDocumentBuilder.UsageMode.ToDisk);

                    _context.CachedProperties.NewDocument();

                    ReadObject(builder);

                    var reader = builder.CreateReader();
                    builder.Reset();
                    yield return reader;
                }
            }
            finally
            {
                builder.Dispose();
            }
        }

        private IEnumerable<string> ReadLegacyDeletions()
        {
            foreach (var item in ReadArray())
            {
                if (item.TryGet("Key", out string key) == false)
                    continue;

                yield return key;
            }
        }

        private IEnumerable<DocumentItem> ReadLegacyAttachments(INewDocumentActions actions)
        {
            if (UnmanagedJsonParserHelper.Read(_peepingTomStream, _parser, _state, _buffer) == false)
                UnmanagedJsonParserHelper.ThrowInvalidJson("Unexpected end of json", _peepingTomStream, _parser);

            if (_state.CurrentTokenType != JsonParserToken.StartArray)
                UnmanagedJsonParserHelper.ThrowInvalidJson("Expected start array, but got " + _state.CurrentTokenType, _peepingTomStream, _parser);

            var context = _context;
            var modifier = new BlittableMetadataModifier(context);
            var builder = CreateBuilder(context, modifier);
            try
            {
                while (true)
                {
                    if (UnmanagedJsonParserHelper.Read(_peepingTomStream, _parser, _state, _buffer) == false)
                        UnmanagedJsonParserHelper.ThrowInvalidJson("Unexpected end of json while reading legacy attachments", _peepingTomStream, _parser);

                    if (_state.CurrentTokenType == JsonParserToken.EndArray)
                        break;

                    if (actions != null)
                    {
                        var oldContext = context;
                        context = actions.GetContextForNewDocument();
                        if (oldContext != context)
                        {
                            builder.Dispose();
                            builder = CreateBuilder(context, modifier);
                        }
                    }
                    builder.Renew("import/object", BlittableJsonDocumentBuilder.UsageMode.ToDisk);

                    _context.CachedProperties.NewDocument();

                    ReadObject(builder);

                    var data = builder.CreateReader();
                    builder.Reset();

                    var attachment = new DocumentItem.AttachmentStream
                    {
                        Stream = actions.GetTempStream()
                    };

                    var attachmentInfo = ProcessLegacyAttachment(context, data, ref attachment);
                    if (ShouldSkip(attachmentInfo))
                        continue;

                    var dummyDoc = new DocumentItem
                    {
                        Document = new Document
                        {
                            Data = WriteDummyDocumentForAttachment(context, attachmentInfo),
                            Id = attachmentInfo.Id,
                            ChangeVector = string.Empty,
                            Flags = DocumentFlags.HasAttachments,
                            NonPersistentFlags = NonPersistentDocumentFlags.FromSmuggler
                        },
                        Attachments = new List<DocumentItem.AttachmentStream>
                        {
                            attachment
                        }
                    };

                    yield return dummyDoc;
                }
            }
            finally
            {
                builder.Dispose();
            }

        }

        private static bool ShouldSkip(LegacyAttachmentDetails attachmentInfo)
        {
            if (attachmentInfo.Metadata.TryGet("Raven-Delete-Marker", out bool deleted) && deleted)
                return true;

            return attachmentInfo.Key.EndsWith(".deleting") || attachmentInfo.Key.EndsWith(".downloading");
        }

        public static BlittableJsonReaderObject WriteDummyDocumentForAttachment(DocumentsOperationContext context, LegacyAttachmentDetails details)
        {
            var attachment = new DynamicJsonValue
            {
                ["Name"] = details.Key,
                ["Hash"] = details.Hash,
                ["ContentType"] = string.Empty,
                ["Size"] = details.Size,
            };
            var attachmets = new DynamicJsonArray();
            attachmets.Add(attachment);
            var metadata = new DynamicJsonValue
            {
                [Constants.Documents.Metadata.Collection] = "@files",
                [Constants.Documents.Metadata.Attachments] = attachmets,
                [Constants.Documents.Metadata.LegacyAttachmentsMetadata] = details.Metadata
            };
            var djv = new DynamicJsonValue
            {
                [Constants.Documents.Metadata.Key] = metadata,

            };

            return context.ReadObject(djv, details.Id);
        }

        private IEnumerable<DocumentItem> ReadDocuments(INewDocumentActions actions = null)
        {
            if (UnmanagedJsonParserHelper.Read(_peepingTomStream, _parser, _state, _buffer) == false)
                UnmanagedJsonParserHelper.ThrowInvalidJson("Unexpected end of json", _peepingTomStream, _parser);

            if (_state.CurrentTokenType != JsonParserToken.StartArray)
                UnmanagedJsonParserHelper.ThrowInvalidJson("Expected start array, but got " + _state.CurrentTokenType, _peepingTomStream, _parser);

            var context = _context;
            var legacyImport = _buildVersionType == BuildVersionType.V3;
            var modifier = new BlittableMetadataModifier(context)
            {
                ReadFirstEtagOfLegacyRevision = legacyImport,
                ReadLegacyEtag = _readLegacyEtag
            };
            var builder = CreateBuilder(context, modifier);
            try
            {
                List<DocumentItem.AttachmentStream> attachments = null;
                while (true)
                {
                    if (UnmanagedJsonParserHelper.Read(_peepingTomStream, _parser, _state, _buffer) == false)
                        UnmanagedJsonParserHelper.ThrowInvalidJson("Unexpected end of json while reading docs", _peepingTomStream, _parser);

                    if (_state.CurrentTokenType == JsonParserToken.EndArray)
                        break;

                    if (actions != null)
                    {
                        var oldContext = context;
                        context = actions.GetContextForNewDocument();
                        if (oldContext != context)
                        {
                            builder.Dispose();
                            builder = CreateBuilder(context, modifier);
                        }
                    }
                    builder.Renew("import/object", BlittableJsonDocumentBuilder.UsageMode.ToDisk);

                    _context.CachedProperties.NewDocument();

                    ReadObject(builder);

                    var data = builder.CreateReader();
                    builder.Reset();

                    if (data.TryGet(Constants.Documents.Metadata.Key, out BlittableJsonReaderObject metadata) &&
                        metadata.TryGet(DocumentItem.ExportDocumentType.Key, out string type) &&
                        type == DocumentItem.ExportDocumentType.Attachment)
                    {
                        if (attachments == null)
                            attachments = new List<DocumentItem.AttachmentStream>();

                        var attachment = new DocumentItem.AttachmentStream
                        {
                            Stream = actions.GetTempStream()
                        };
                        ProcessAttachmentStream(context, data, ref attachment);
                        attachments.Add(attachment);
                        continue;
                    }

                    if (legacyImport)
                    {
                        if (modifier.Id.Contains(HiLoHandler.RavenHiloIdPrefix))
                        {
                            data.Modifications = new DynamicJsonValue
                            {
                                [Constants.Documents.Metadata.Key] = new DynamicJsonValue
                                {
                                    [Constants.Documents.Metadata.Collection] = CollectionName.HiLoCollection
                                }
                            };
                            data = context.ReadObject(data, modifier.Id, BlittableJsonDocumentBuilder.UsageMode.ToDisk);
                        }
                    }

                    _result.LegacyLastDocumentEtag = modifier.LegacyEtag;

                    yield return new DocumentItem
                    {
                        Document = new Document
                        {
                            Data = data,
                            Id = modifier.Id,
                            ChangeVector = modifier.ChangeVector,
                            Flags = modifier.Flags,
                            NonPersistentFlags = modifier.NonPersistentFlags
                        },
                        Attachments = attachments
                    };
                    attachments = null;
                }
            }
            finally
            {
                builder.Dispose();
            }
        }

        private IEnumerable<DocumentTombstone> ReadTombstones(INewDocumentActions actions = null)
        {
            if (UnmanagedJsonParserHelper.Read(_peepingTomStream, _parser, _state, _buffer) == false)
                UnmanagedJsonParserHelper.ThrowInvalidJson("Unexpected end of json", _peepingTomStream, _parser);

            if (_state.CurrentTokenType != JsonParserToken.StartArray)
                UnmanagedJsonParserHelper.ThrowInvalidJson("Expected start array, but got " + _state.CurrentTokenType, _peepingTomStream, _parser);

            var context = _context;
            var builder = CreateBuilder(context, null);
            try
            {
                while (true)
                {
                    if (UnmanagedJsonParserHelper.Read(_peepingTomStream, _parser, _state, _buffer) == false)
                        UnmanagedJsonParserHelper.ThrowInvalidJson("Unexpected end of json while reading docs", _peepingTomStream, _parser);

                    if (_state.CurrentTokenType == JsonParserToken.EndArray)
                        break;

                    if (actions != null)
                    {
                        var oldContext = context;
                        context = actions.GetContextForNewDocument();
                        if (oldContext != context)
                        {
                            builder.Dispose();
                            builder = CreateBuilder(context, null);
                        }
                    }
                    builder.Renew("import/object", BlittableJsonDocumentBuilder.UsageMode.ToDisk);

                    _context.CachedProperties.NewDocument();

                    ReadObject(builder);

                    var data = builder.CreateReader();
                    builder.Reset();

                    var tombstone = new DocumentTombstone();
                    if (data.TryGet("Key", out tombstone.LowerId) &&
                        data.TryGet(nameof(DocumentTombstone.Type), out string type) &&
                        data.TryGet(nameof(DocumentTombstone.Collection), out tombstone.Collection))
                    {
                        tombstone.Type = Enum.Parse<DocumentTombstone.TombstoneType>(type);
                        yield return tombstone;
                    }
                    else
                    {
                        var msg = "Ignoring an invalied tombstone which you try to import. " + data;
                        if (_log.IsOperationsEnabled)
                            _log.Operations(msg);

                        _result.Tombstones.ErroredCount++;
                        _result.AddWarning(msg);
                    }
                }
            }
            finally
            {
                builder.Dispose();
            }
        }

        private IEnumerable<DocumentConflict> ReadConflicts(INewDocumentActions actions = null)
        {
            if (UnmanagedJsonParserHelper.Read(_peepingTomStream, _parser, _state, _buffer) == false)
                UnmanagedJsonParserHelper.ThrowInvalidJson("Unexpected end of json", _peepingTomStream, _parser);

            if (_state.CurrentTokenType != JsonParserToken.StartArray)
                UnmanagedJsonParserHelper.ThrowInvalidJson("Expected start array, but got " + _state.CurrentTokenType, _peepingTomStream, _parser);

            var context = _context;
            var builder = CreateBuilder(context, null);
            try
            {
                while (true)
                {
                    if (UnmanagedJsonParserHelper.Read(_peepingTomStream, _parser, _state, _buffer) == false)
                        UnmanagedJsonParserHelper.ThrowInvalidJson("Unexpected end of json while reading docs", _peepingTomStream, _parser);

                    if (_state.CurrentTokenType == JsonParserToken.EndArray)
                        break;

                    if (actions != null)
                    {
                        var oldContext = context;
                        context = actions.GetContextForNewDocument();
                        if (oldContext != context)
                        {
                            builder.Dispose();
                            builder = CreateBuilder(context, null);
                        }
                    }
                    builder.Renew("import/object", BlittableJsonDocumentBuilder.UsageMode.ToDisk);

                    _context.CachedProperties.NewDocument();

                    ReadObject(builder);

                    var data = builder.CreateReader();
                    builder.Reset();

                    var conflict = new DocumentConflict();
                    if (data.TryGet(nameof(DocumentConflict.Id), out conflict.Id) &&
                        data.TryGet(nameof(DocumentConflict.Collection), out conflict.Collection) &&
                        data.TryGet(nameof(DocumentConflict.Flags), out string flags) &&
                        data.TryGet(nameof(DocumentConflict.ChangeVector), out conflict.ChangeVector) &&
                        data.TryGet(nameof(DocumentConflict.Etag), out conflict.Etag) &&
                        data.TryGet(nameof(DocumentConflict.LastModified), out conflict.LastModified) &&
                        data.TryGet(nameof(DocumentConflict.Doc), out conflict.Doc))
                    {
                        conflict.Flags = Enum.Parse<DocumentFlags>(flags);
                        if (conflict.Doc != null) // This is null for conflict that was generated from tombstone
                            conflict.Doc = context.ReadObject(conflict.Doc, conflict.Id, BlittableJsonDocumentBuilder.UsageMode.ToDisk);
                        yield return conflict;
                    }
                    else
                    {
                        var msg = "Ignoring an invalied conflict which you try to import. " + data;
                        if (_log.IsOperationsEnabled)
                            _log.Operations(msg);

                        _result.Conflicts.ErroredCount++;
                        _result.AddWarning(msg);
                    }
                }
            }
            finally
            {
                builder.Dispose();
            }
        }

        internal unsafe LegacyAttachmentDetails ProcessLegacyAttachment(
            DocumentsOperationContext context,
            BlittableJsonReaderObject data,
            ref DocumentItem.AttachmentStream attachment)
        {
            if (data.TryGet("Key", out string key) == false)
            {
                throw new ArgumentException("The key of legacy attachment is missing its key property.");
            }

            if (data.TryGet("Metadata", out BlittableJsonReaderObject metadata) == false)
            {
                throw new ArgumentException($"Metadata of legacy attachment with key={key} is missing");
            }

            if (data.TryGet("Data", out string base64data) == false)
            {
                throw new ArgumentException($"Data of legacy attachment with key={key} is missing");
            }

            if (_readLegacyEtag && data.TryGet("Etag", out string etag))
            {
                _result.LegacyLastAttachmentEtag = etag;
            }

            var memoryStream = new MemoryStream();

            fixed (char* pdata = base64data)
            {
                memoryStream.SetLength(Base64.FromBase64_ComputeResultLength(pdata, base64data.Length));
                fixed (byte* buffer = memoryStream.GetBuffer())
                    Base64.FromBase64_Decode(pdata, base64data.Length, buffer, (int)memoryStream.Length);
            }

            memoryStream.Position = 0;

            return GenerateLegacyAttachmentDetails(context, memoryStream, key, metadata, ref attachment);
        }

        public static LegacyAttachmentDetails GenerateLegacyAttachmentDetails(
            DocumentsOperationContext context,
            Stream decodedStream,
            string key,
            BlittableJsonReaderObject metadata,
            ref DocumentItem.AttachmentStream attachment)
        {
            var stream = attachment.Stream;
            var hash = AsyncHelpers.RunSync(() => AttachmentsStorageHelper.CopyStreamToFileAndCalculateHash(context, decodedStream, stream, CancellationToken.None));
            attachment.Stream.Flush();
            var lazyHash = context.GetLazyString(hash);
            attachment.Base64HashDispose = Slice.External(context.Allocator, lazyHash, out attachment.Base64Hash);
            var tag = $"{DummyDocumentPrefix}{key}{RecordSeperator}d{RecordSeperator}{key}{RecordSeperator}{hash}{RecordSeperator}";
            var lazyTag = context.GetLazyString(tag);
            attachment.TagDispose = Slice.External(context.Allocator, lazyTag, out attachment.Tag);
            var id = $"{DummyDocumentPrefix}{key}";
            var lazyId = context.GetLazyString(id);

            attachment.Data = context.ReadObject(metadata, id);
            return new LegacyAttachmentDetails
            {
                Id = lazyId,
                Hash = hash,
                Key = key,
                Size = attachment.Stream.Length,
                Tag = tag,
                Metadata = attachment.Data
            };
        }

        public struct LegacyAttachmentDetails
        {
            public LazyStringValue Id;
            public string Hash;
            public string Key;
            public long Size;
            public string Tag;
            public BlittableJsonReaderObject Metadata;
        }

        private const char RecordSeperator = (char)SpecialChars.RecordSeparator;
        private const string DummyDocumentPrefix = "files/";

        public unsafe void ProcessAttachmentStream(DocumentsOperationContext context, BlittableJsonReaderObject data, ref DocumentItem.AttachmentStream attachment)
        {
            if (data.TryGet(nameof(AttachmentName.Hash), out LazyStringValue hash) == false ||
                data.TryGet(nameof(AttachmentName.Size), out long size) == false ||
                data.TryGet(nameof(DocumentItem.AttachmentStream.Tag), out LazyStringValue tag) == false)
                throw new ArgumentException($"Data of attachment stream is not valid: {data}");

            if (_writeBuffer == null)
                _returnWriteBuffer = _context.GetManagedBuffer(out _writeBuffer);

            attachment.Data = data;
            attachment.Base64HashDispose = Slice.External(context.Allocator, hash, out attachment.Base64Hash);
            attachment.TagDispose = Slice.External(context.Allocator, tag, out attachment.Tag);

            while (size > 0)
            {
                var sizeToRead = (int)Math.Min(_writeBuffer.Length, size);
                var read = _parser.Copy(_writeBuffer.Pointer, sizeToRead);
                attachment.Stream.Write(_writeBuffer.Buffer.Array, _writeBuffer.Buffer.Offset, read.bytesRead);
                if (read.done == false)
                {
                    var read2 = _peepingTomStream.Read(_buffer.Buffer.Array, _buffer.Buffer.Offset, _buffer.Length);
                    if (read2 == 0)
                        throw new EndOfStreamException("Stream ended without reaching end of stream content");

                    _parser.SetBuffer(_buffer, 0, read2);
                }
                size -= read.bytesRead;
            }
            attachment.Stream.Flush();
        }

        private BlittableJsonDocumentBuilder CreateBuilder(JsonOperationContext context, BlittableMetadataModifier modifier)
        {
            return new BlittableJsonDocumentBuilder(context,
                BlittableJsonDocumentBuilder.UsageMode.ToDisk, "import/object", _parser, _state,
                modifier: modifier);
        }

        private DatabaseItemType GetType(string type)
        {
            if (type == null)
                return DatabaseItemType.None;

            if (type.Equals("Docs", StringComparison.OrdinalIgnoreCase))
                return DatabaseItemType.Documents;

            // reading from stream/docs endpoint
            if (type.Equals("Results", StringComparison.OrdinalIgnoreCase))
                return DatabaseItemType.Documents;

            if (type.Equals(nameof(DatabaseItemType.RevisionDocuments), StringComparison.OrdinalIgnoreCase))
                return DatabaseItemType.RevisionDocuments;

            if (type.Equals(nameof(DatabaseItemType.Tombstones), StringComparison.OrdinalIgnoreCase))
                return DatabaseItemType.Tombstones;

            if (type.Equals(nameof(DatabaseItemType.Conflicts), StringComparison.OrdinalIgnoreCase))
                return DatabaseItemType.Conflicts;

            if (type.Equals(nameof(DatabaseItemType.Indexes), StringComparison.OrdinalIgnoreCase))
                return DatabaseItemType.Indexes;

            if (type.Equals(nameof(DatabaseItemType.Identities), StringComparison.OrdinalIgnoreCase))
                return DatabaseItemType.Identities;

            if (type.Equals(nameof(DatabaseItemType.CompareExchange), StringComparison.OrdinalIgnoreCase) ||
                type.Equals("CmpXchg", StringComparison.OrdinalIgnoreCase)) //support the old name
                return DatabaseItemType.CompareExchange;

            if (type.Equals("Attachments", StringComparison.OrdinalIgnoreCase))
                return DatabaseItemType.LegacyAttachments;

            if (type.Equals("DocsDeletions", StringComparison.OrdinalIgnoreCase))
                return DatabaseItemType.LegacyDocumentDeletions;

            if (type.Equals("AttachmentsDeletions", StringComparison.OrdinalIgnoreCase))
                return DatabaseItemType.LegacyAttachmentDeletions;

            throw new InvalidOperationException("Got unexpected property name '" + type + "' on " + _parser.GenerateErrorState());
        }

        public void Dispose()
        {
            _peepingTomStream.Dispose();
        }
    }
}

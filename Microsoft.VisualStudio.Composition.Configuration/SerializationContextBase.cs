﻿namespace Microsoft.VisualStudio.Composition
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.Serialization.Formatters.Binary;
    using System.Text;
    using System.Threading.Tasks;
    using Microsoft.VisualStudio.Composition.Reflection;
    using Validation;

    internal abstract class SerializationContextBase
    {
        private static readonly char[] StringSegmentSeparator = { '.' };

        protected BinaryReader reader;

        protected BinaryWriter writer;

        protected Dictionary<object, uint> serializingObjectTable;

        protected Dictionary<uint, object> deserializingObjectTable;

        protected int indentationLevel;

        protected Dictionary<string, int> sizeStats;

        internal SerializationContextBase(BinaryReader reader)
        {
            Requires.NotNull(reader, "reader");
            this.reader = reader;
            this.deserializingObjectTable = new Dictionary<uint, object>();
        }

        internal SerializationContextBase(BinaryWriter writer)
        {
            Requires.NotNull(writer, "writer");
            this.writer = writer;
            this.serializingObjectTable = new Dictionary<object, uint>(SmartInterningEqualityComparer.Default);
            this.sizeStats = new Dictionary<string, int>();
        }

        protected SerializationTrace Trace(string elementName, Stream stream)
        {
            return new SerializationTrace(this, elementName, stream);
        }

        protected SerializationTrace Trace(string elementName)
        {
            return new SerializationTrace(this, elementName, reader != null ? reader.BaseStream : writer.BaseStream);
        }

        protected void Write(MethodRef methodRef)
        {
            using (Trace("MethodRef", writer.BaseStream))
            {
                if (methodRef.IsEmpty)
                {
                    writer.Write((byte)0);
                }
                else
                {
                    writer.Write((byte)1);
                    this.Write(methodRef.DeclaringType);
                    this.WriteCompressedMetadataToken(methodRef.MetadataToken, MetadataTokenType.Method);
                    this.Write(methodRef.GenericMethodArguments, this.Write);
                }
            }
        }

        protected MethodRef ReadMethodRef()
        {
            using (Trace("MethodRef", reader.BaseStream))
            {
                byte nullCheck = reader.ReadByte();
                if (nullCheck == 1)
                {
                    var declaringType = this.ReadTypeRef();
                    var metadataToken = this.ReadCompressedMetadataToken(MetadataTokenType.Method);
                    var genericMethodArguments = this.ReadList(reader, this.ReadTypeRef);
                    return new MethodRef(declaringType, metadataToken, genericMethodArguments.ToImmutableArray());
                }
                else
                {
                    return default(MethodRef);
                }
            }
        }

        protected void Write(MemberRef memberRef)
        {
            using (Trace("MemberRef", writer.BaseStream))
            {
                if (memberRef.IsConstructor)
                {
                    writer.Write((byte)1);
                    this.Write(memberRef.Constructor);
                }
                else if (memberRef.IsField)
                {
                    writer.Write((byte)2);
                    this.Write(memberRef.Field);
                }
                else if (memberRef.IsProperty)
                {
                    writer.Write((byte)3);
                    this.Write(memberRef.Property);
                }
                else if (memberRef.IsMethod)
                {
                    writer.Write((byte)4);
                    this.Write(memberRef.Method);
                }
                else
                {
                    writer.Write((byte)0);
                }
            }
        }

        protected MemberRef ReadMemberRef()
        {
            using (Trace("MemberRef", reader.BaseStream))
            {
                int kind = reader.ReadByte();
                switch (kind)
                {
                    case 0:
                        return default(MemberRef);
                    case 1:
                        return new MemberRef(this.ReadConstructorRef());
                    case 2:
                        return new MemberRef(this.ReadFieldRef());
                    case 3:
                        return new MemberRef(this.ReadPropertyRef());
                    case 4:
                        return new MemberRef(this.ReadMethodRef());
                    default:
                        throw new NotSupportedException();
                }
            }
        }

        protected void Write(PropertyRef propertyRef)
        {
            using (Trace("PropertyRef", writer.BaseStream))
            {
                this.Write(propertyRef.DeclaringType);
                this.WriteCompressedMetadataToken(propertyRef.MetadataToken, MetadataTokenType.Property);

                byte flags = 0;
                flags |= propertyRef.GetMethodMetadataToken.HasValue ? (byte)0x1 : (byte)0x0;
                flags |= propertyRef.SetMethodMetadataToken.HasValue ? (byte)0x2 : (byte)0x0;
                writer.Write(flags);

                if (propertyRef.GetMethodMetadataToken.HasValue)
                {
                    this.WriteCompressedMetadataToken(propertyRef.GetMethodMetadataToken.Value, MetadataTokenType.Method);
                }

                if (propertyRef.SetMethodMetadataToken.HasValue)
                {
                    this.WriteCompressedMetadataToken(propertyRef.SetMethodMetadataToken.Value, MetadataTokenType.Method);
                }
            }
        }

        protected PropertyRef ReadPropertyRef()
        {
            using (Trace("PropertyRef", reader.BaseStream))
            {
                var declaringType = this.ReadTypeRef();
                var metadataToken = this.ReadCompressedMetadataToken(MetadataTokenType.Property);

                byte flags = reader.ReadByte();
                int? getter = null, setter = null;
                if ((flags & 0x1) != 0)
                {
                    getter = this.ReadCompressedMetadataToken(MetadataTokenType.Method);
                }

                if ((flags & 0x2) != 0)
                {
                    setter = this.ReadCompressedMetadataToken(MetadataTokenType.Method);
                }

                return new PropertyRef(
                    declaringType,
                    metadataToken,
                    getter,
                    setter);
            }
        }

        protected void Write(FieldRef fieldRef)
        {
            using (Trace("FieldRef", writer.BaseStream))
            {
                writer.Write(!fieldRef.IsEmpty);
                if (!fieldRef.IsEmpty)
                {
                    this.Write(fieldRef.DeclaringType);
                    this.WriteCompressedMetadataToken(fieldRef.MetadataToken, MetadataTokenType.Field);
                }
            }
        }

        protected FieldRef ReadFieldRef()
        {
            using (Trace("FieldRef", reader.BaseStream))
            {
                if (reader.ReadBoolean())
                {
                    var declaringType = this.ReadTypeRef();
                    int metadataToken = this.ReadCompressedMetadataToken(MetadataTokenType.Field);
                    return new FieldRef(declaringType, metadataToken);
                }
                else
                {
                    return default(FieldRef);
                }
            }
        }

        protected void Write(ParameterRef parameterRef)
        {
            using (Trace("ParameterRef", writer.BaseStream))
            {
                writer.Write(!parameterRef.IsEmpty);
                if (!parameterRef.IsEmpty)
                {
                    this.Write(parameterRef.DeclaringType);
                    this.WriteCompressedMetadataToken(parameterRef.MethodMetadataToken, MetadataTokenType.Method);
                    writer.Write((byte)parameterRef.ParameterIndex);
                }
            }
        }

        protected ParameterRef ReadParameterRef()
        {
            using (Trace("ParameterRef", reader.BaseStream))
            {
                if (reader.ReadBoolean())
                {
                    var declaringType = this.ReadTypeRef();
                    int methodMetadataToken = this.ReadCompressedMetadataToken(MetadataTokenType.Method);
                    var parameterIndex = reader.ReadByte();
                    return new ParameterRef(declaringType, methodMetadataToken, parameterIndex);
                }
                else
                {
                    return default(ParameterRef);
                }
            }
        }

        protected void WriteCompressedMetadataToken(int metadataToken, MetadataTokenType type)
        {
            uint token = (uint)metadataToken;
            uint flags = (uint)type;
            Requires.Argument((token & (uint)MetadataTokenType.Mask) == flags, "type", "Wrong type"); // just a sanity check
            this.WriteCompressedUInt(token & ~(uint)MetadataTokenType.Mask);
        }

        protected int ReadCompressedMetadataToken(MetadataTokenType type)
        {
            return (int)(this.ReadCompressedUInt() | (uint)type);
        }

        protected void Write(ConstructorRef constructorRef)
        {
            Requires.Argument(!constructorRef.IsEmpty, "constructorRef", "Cannot be empty.");
            using (Trace("ConstructorRef", writer.BaseStream))
            {
                this.Write(constructorRef.DeclaringType);
                this.WriteCompressedMetadataToken(constructorRef.MetadataToken, MetadataTokenType.Method);
            }
        }

        protected ConstructorRef ReadConstructorRef()
        {
            using (Trace("ConstructorRef", reader.BaseStream))
            {
                var declaringType = this.ReadTypeRef();
                var metadataToken = this.ReadCompressedMetadataToken(MetadataTokenType.Method);

                return new ConstructorRef(
                    declaringType,
                    metadataToken);
            }
        }

        protected void Write(TypeRef typeRef)
        {
            using (Trace("TypeRef", writer.BaseStream))
            {
                if (this.TryPrepareSerializeReusableObject(typeRef))
                {
                    this.Write(typeRef.AssemblyName);
                    this.WriteCompressedMetadataToken(typeRef.MetadataToken, MetadataTokenType.Type);
                    writer.Write(typeRef.IsArray);
                    writer.Write((byte)typeRef.GenericTypeParameterCount);
                    this.Write(typeRef.GenericTypeArguments, this.Write);
                }
            }
        }

        protected TypeRef ReadTypeRef()
        {
            using (Trace("TypeRef", reader.BaseStream))
            {
                uint id;
                TypeRef value;
                if (this.TryPrepareDeserializeReusableObject(out id, out value))
                {
                    var assemblyName = this.ReadAssemblyName();
                    var metadataToken = this.ReadCompressedMetadataToken(MetadataTokenType.Type);
                    bool isArray = reader.ReadBoolean();
                    int genericTypeParameterCount = reader.ReadByte();
                    var genericTypeArguments = this.ReadList(reader, this.ReadTypeRef);
                    value = TypeRef.Get(assemblyName, metadataToken, isArray, genericTypeParameterCount, genericTypeArguments.ToImmutableArray());
                    this.OnDeserializedReusableObject(id, value);
                }

                return value;
            }
        }

        protected void Write(AssemblyName assemblyName)
        {
            using (Trace("AssemblyName", writer.BaseStream))
            {
                if (this.TryPrepareSerializeReusableObject(assemblyName))
                {
                    this.Write(assemblyName.FullName);
                    this.Write(assemblyName.CodeBase);
                }
            }
        }

        protected AssemblyName ReadAssemblyName()
        {
            using (Trace("AssemblyName", reader.BaseStream))
            {
                uint id;
                AssemblyName value;
                if (this.TryPrepareDeserializeReusableObject(out id, out value))
                {
                    string fullName = this.ReadString();
                    string codeBase = this.ReadString();
                    value = new AssemblyName(fullName) { CodeBase = codeBase };
                    this.OnDeserializedReusableObject(id, value);
                }

                return value;
            }
        }

        protected void Write(string value)
        {
            using (Trace("String", writer.BaseStream))
            {
                if (value != null)
                {
                    string[] segments = value.Split(StringSegmentSeparator);
                    this.WriteCompressedUInt((uint)segments.Length);
                    foreach (string segment in segments)
                    {
                        if (this.TryPrepareSerializeReusableObject(segment))
                        {
                            writer.Write(segment);
                        }
                    }
                }
                else
                {
                    this.WriteCompressedUInt(0);
                }
            }
        }

        protected string ReadString()
        {
            using (Trace("String", reader.BaseStream))
            {
                uint segmentsCount = this.ReadCompressedUInt();
                if (segmentsCount == 0)
                {
                    return null;
                }

                var builder = new StringBuilder();
                for (int i = 0; i < segmentsCount; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(StringSegmentSeparator[0]);
                    }

                    uint id;
                    string value;
                    if (this.TryPrepareDeserializeReusableObject(out id, out value))
                    {
                        value = reader.ReadString();
                        this.OnDeserializedReusableObject(id, value);
                    }

                    builder.Append(value);
                }

                return builder.ToString();
            }
        }

        protected void WriteCompressedUInt(uint value)
        {
            CompressedUInt.WriteCompressedUInt(writer, value);
        }

        protected uint ReadCompressedUInt()
        {
            return CompressedUInt.ReadCompressedUInt(reader);
        }

        protected void Write<T>(IReadOnlyCollection<T> list, Action<T> itemWriter)
        {
            Requires.NotNull(list, "list");
            using (Trace("List<" + typeof(T).Name + ">", writer.BaseStream))
            {
                this.WriteCompressedUInt((uint)list.Count);
                foreach (var item in list)
                {
                    itemWriter(item);
                }
            }
        }

        protected void Write(Array list, Action<object> itemWriter)
        {
            Requires.NotNull(list, "list");
            using (Trace((list != null ? list.GetType().GetElementType().Name : "null") + "[]", writer.BaseStream))
            {
                this.WriteCompressedUInt((uint)list.Length);
                foreach (var item in list)
                {
                    itemWriter(item);
                }
            }
        }

        protected IReadOnlyList<T> ReadList<T>(Func<T> itemReader)
        {
            return this.ReadList<T>(this.reader, itemReader);
        }

        protected IReadOnlyList<T> ReadList<T>(BinaryReader reader, Func<T> itemReader)
        {
            using (Trace("List<" + typeof(T).Name + ">", reader.BaseStream))
            {
                uint count = this.ReadCompressedUInt();
                if (count > 0xffff)
                {
                    // Probably either file corruption or a bug in serialization.
                    // Let's not take untold amounts of memory by throwing out suspiciously large lengths.
                    throw new NotSupportedException();
                }

                var list = new T[count];
                for (int i = 0; i < list.Length; i++)
                {
                    list[i] = itemReader();
                }

                return list;
            }
        }

        protected Array ReadArray(BinaryReader reader, Func<object> itemReader, Type elementType)
        {
            using (Trace("List<" + elementType.Name + ">", reader.BaseStream))
            {
                uint count = this.ReadCompressedUInt();
                if (count > 0xffff)
                {
                    // Probably either file corruption or a bug in serialization.
                    // Let's not take untold amounts of memory by throwing out suspiciously large lengths.
                    throw new NotSupportedException();
                }

                var list = Array.CreateInstance(elementType, count);
                for (int i = 0; i < list.Length; i++)
                {
                    object value = itemReader();
                    list.SetValue(value, i);
                }

                return list;
            }
        }

        protected void Write(IReadOnlyDictionary<string, object> metadata)
        {
            using (Trace("Metadata", writer.BaseStream))
            {
                this.WriteCompressedUInt((uint)metadata.Count);

                // Special case certain values to avoid defeating lazy load later.
                // Check out the ReadMetadata below, how it wraps the return value.
                var serializedMetadata = new LazyMetadataWrapper(metadata.ToImmutableDictionary(), LazyMetadataWrapper.Direction.ToSubstitutedValue);
                foreach (var entry in serializedMetadata)
                {
                    this.Write(entry.Key);
                    this.WriteObject(entry.Value);
                }
            }
        }

        protected IReadOnlyDictionary<string, object> ReadMetadata()
        {
            using (Trace("Metadata", reader.BaseStream))
            {
                // PERF TIP: if ReadMetadata shows up on startup perf traces,
                // we could simply read the blob containing the metadata into a byte[]
                // and defer actually deserializing it until such time as the metadata
                // is actually required.
                // We might do this with minimal impact to other code by implementing
                // IReadOnlyDictionary<string, object> ourselves such that on the first
                // access of any of its contents, we'll do a just-in-time deserialization,
                // and perhaps only of the requested values.
                uint count = this.ReadCompressedUInt();
                var metadata = ImmutableDictionary<string, object>.Empty;

                if (count > 0)
                {
                    var builder = metadata.ToBuilder();
                    for (int i = 0; i < count; i++)
                    {
                        string key = this.ReadString();
                        object value = this.ReadObject();
                        builder.Add(key, value);
                    }

                    metadata = builder.ToImmutable();
                }

                return new LazyMetadataWrapper(metadata, LazyMetadataWrapper.Direction.ToOriginalValue);
            }
        }

        protected void Write(ImportCardinality cardinality)
        {
            using (Trace("ImportCardinality", writer.BaseStream))
            {
                writer.Write((byte)cardinality);
            }
        }

        protected ImportCardinality ReadImportCardinality()
        {
            using (Trace("ImportCardinality", reader.BaseStream))
            {
                return (ImportCardinality)reader.ReadByte();
            }
        }

        /// <summary>
        /// Prepares the object for referential sharing in the serialization stream.
        /// </summary>
        /// <param name="value">The value that may be serialized more than once.</param>
        /// <returns><c>true</c> if the object should be serialized; otherwise <c>false</c>.</returns>
        protected bool TryPrepareSerializeReusableObject(object value)
        {
            uint id;
            bool result;
            if (value == null)
            {
                id = 0;
                result = false;
            }
            else if (this.serializingObjectTable.TryGetValue(value, out id))
            {
                // The object has already been serialized.
                result = false;
            }
            else
            {
                this.serializingObjectTable.Add(value, id = (uint)this.serializingObjectTable.Count + 1);
                result = true;
            }

            this.WriteCompressedUInt(id);
            return result;
        }

        /// <summary>
        /// Gets an object that has already been deserialized, if available.
        /// </summary>
        /// <param name="id">Receives the ID of the object.</param>
        /// <param name="value">Receives the value of the object, if available.</param>
        /// <returns><c>true</c> if the caller should deserialize the object; <c>false</c> if the object is in <paramref name="value"/>.</returns>
        protected bool TryPrepareDeserializeReusableObject<T>(out uint id, out T value)
            where T : class
        {
            id = this.ReadCompressedUInt();
            if (id == 0)
            {
                value = null;
                return false;
            }

            object valueObject;
            bool result = !this.deserializingObjectTable.TryGetValue(id, out valueObject);
            value = (T)valueObject;
            return result;
        }

        protected void OnDeserializedReusableObject(uint id, object value)
        {
            this.deserializingObjectTable.Add(id, value);
        }

        protected enum ObjectType : byte
        {
            Null,
            String,
            CreationPolicy,
            Type,
            Array,
            BinaryFormattedObject,
            TypeRef,
            BoolTrue,
            BoolFalse,
            Int32,
            Char,
            Guid,
            Enum32Substitution,
            TypeSubstitution,
            TypeArraySubstitution,
        }

        protected void WriteObject(object value)
        {
            if (value == null)
            {
                using (Trace("Object (null)", writer.BaseStream))
                {
                    this.Write(ObjectType.Null);
                }
            }
            else
            {
                Type valueType = value.GetType();
                using (Trace("Object (" + valueType.Name + ")", writer.BaseStream))
                {
                    if (valueType.IsArray)
                    {
                        Array array = (Array)value;
                        this.Write(ObjectType.Array);
                        TypeRef elementTypeRef = TypeRef.Get(valueType.GetElementType());
                        this.Write(elementTypeRef);
                        this.Write(array, this.WriteObject);
                    }
                    else if (valueType == typeof(bool))
                    {
                        this.Write((bool)value ? ObjectType.BoolTrue : ObjectType.BoolFalse);
                    }
                    else if (valueType == typeof(string))
                    {
                        this.Write(ObjectType.String);
                        this.Write((string)value);
                    }
                    else if (valueType == typeof(int))
                    {
                        this.Write(ObjectType.Int32);
                        writer.Write((int)value);
                    }
                    else if (valueType == typeof(char))
                    {
                        this.Write(ObjectType.Char);
                        writer.Write((char)value);
                    }
                    else if (valueType == typeof(Guid))
                    {
                        this.Write(ObjectType.Guid);
                        writer.Write(((Guid)value).ToByteArray());
                    }
                    else if (valueType == typeof(CreationPolicy)) // TODO: how do we handle arbitrary value types?
                    {
                        this.Write(ObjectType.CreationPolicy);
                        writer.Write((byte)(CreationPolicy)value);
                    }
                    else if (typeof(Type).IsAssignableFrom(valueType))
                    {
                        this.Write(ObjectType.Type);
                        this.Write(TypeRef.Get((Type)value));
                    }
                    else if (typeof(TypeRef) == valueType)
                    {
                        this.Write(ObjectType.TypeRef);
                        this.Write((TypeRef)value);
                    }
                    else if (typeof(LazyMetadataWrapper.Enum32Substitution) == valueType)
                    {
                        var substValue = (LazyMetadataWrapper.Enum32Substitution)value;
                        this.Write(ObjectType.Enum32Substitution);
                        this.Write(substValue.EnumType);
                        writer.Write(substValue.RawValue);
                    }
                    else if (typeof(LazyMetadataWrapper.TypeSubstitution) == valueType)
                    {
                        var substValue = (LazyMetadataWrapper.TypeSubstitution)value;
                        this.Write(ObjectType.TypeSubstitution);
                        this.Write(substValue.TypeRef);
                    }
                    else if (typeof(LazyMetadataWrapper.TypeArraySubstitution) == valueType)
                    {
                        var substValue = (LazyMetadataWrapper.TypeArraySubstitution)value;
                        this.Write(ObjectType.TypeArraySubstitution);
                        this.Write(substValue.TypeRefArray, this.Write);
                    }
                    else
                    {
                        Debug.WriteLine("Falling back to binary formatter for value of type: {0}", valueType);
                        this.Write(ObjectType.BinaryFormattedObject);
                        var formatter = new BinaryFormatter();
                        writer.Flush();
                        formatter.Serialize(writer.BaseStream, value);
                    }
                }
            }
        }

        protected object ReadObject()
        {
            using (Trace("Object", reader.BaseStream))
            {
                ObjectType objectType = this.ReadObjectType();
                switch (objectType)
                {
                    case ObjectType.Null:
                        return null;
                    case ObjectType.Array:
                        Type elementType = this.ReadTypeRef().Resolve();
                        return this.ReadArray(reader, this.ReadObject, elementType);
                    case ObjectType.BoolTrue:
                        return true;
                    case ObjectType.BoolFalse:
                        return false;
                    case ObjectType.Int32:
                        return reader.ReadInt32();
                    case ObjectType.String:
                        return this.ReadString();
                    case ObjectType.Char:
                        return reader.ReadChar();
                    case ObjectType.Guid:
                        return new Guid(reader.ReadBytes(16));
                    case ObjectType.CreationPolicy:
                        return (CreationPolicy)reader.ReadByte();
                    case ObjectType.Type:
                        return this.ReadTypeRef().Resolve();
                    case ObjectType.TypeRef:
                        return this.ReadTypeRef();
                    case ObjectType.Enum32Substitution:
                        TypeRef enumType = this.ReadTypeRef();
                        int rawValue = reader.ReadInt32();
                        return new LazyMetadataWrapper.Enum32Substitution(enumType, rawValue);
                    case ObjectType.TypeSubstitution:
                        TypeRef typeRef = this.ReadTypeRef();
                        return new LazyMetadataWrapper.TypeSubstitution(typeRef);
                    case ObjectType.TypeArraySubstitution:
                        IReadOnlyList<TypeRef> typeRefArray = this.ReadList(reader, this.ReadTypeRef);
                        return new LazyMetadataWrapper.TypeArraySubstitution(typeRefArray);
                    case ObjectType.BinaryFormattedObject:
                        var formatter = new BinaryFormatter();
                        return formatter.Deserialize(reader.BaseStream);
                    default:
                        throw new NotSupportedException("Unsupported format: " + objectType);
                }
            }
        }

        protected void Write(ObjectType type)
        {
            writer.Write((byte)type);
        }

        protected ObjectType ReadObjectType()
        {
            var objectType = (ObjectType)reader.ReadByte();
            return objectType;
        }

        [Conditional("TRACESTATS")]
        protected void TraceStats()
        {
            if (this.sizeStats != null)
            {
                foreach (var item in this.sizeStats.OrderByDescending(kv => kv.Value))
                {
                    Debug.WriteLine("{0,7} {1}", item.Value, item.Key);
                }
            }
        }

        protected struct SerializationTrace : IDisposable
        {
            private const string Indent = "  ";
            private readonly SerializationContextBase context;
            private readonly string elementName;
            private readonly Stream stream;
            private readonly int startStreamPosition;

            internal SerializationTrace(SerializationContextBase context, string elementName, Stream stream)
            {
                this.context = context;
                this.elementName = elementName;
                this.stream = stream;

                this.context.indentationLevel++;
                this.startStreamPosition = (int)stream.Position;

#if DEBUG && TRACESERIALIZATION
                    for (int i = 0; i < this.context.indentationLevel; i++)
                    {
                        Debug.Write(Indent);
                    }

                    Debug.WriteLine("Serialization: {1,7} {0}", elementName, stream.Position);
#endif
            }

            public void Dispose()
            {
                this.context.indentationLevel--;

                if (this.context.sizeStats != null)
                {
                    int length = (int)this.stream.Position - startStreamPosition;
                    this.context.sizeStats[this.elementName] = this.context.sizeStats.GetValueOrDefault(this.elementName) + length;
                }
            }
        }

        /// <summary>
        /// An equality comparer that provides a bit better recognition of objects for better interning.
        /// </summary>
        private class SmartInterningEqualityComparer : IEqualityComparer<object>
        {
            internal static readonly IEqualityComparer<object> Default = new SmartInterningEqualityComparer();

            private static readonly IEqualityComparer<object> Fallback = EqualityComparer<object>.Default;

            private SmartInterningEqualityComparer() { }

            new public bool Equals(object x, object y)
            {
                if (x is AssemblyName && y is AssemblyName)
                {
                    return ByValueEquality.AssemblyName.Equals((AssemblyName)x, (AssemblyName)y);
                }

                return Fallback.Equals(x, y);
            }

            public int GetHashCode(object obj)
            {
                if (obj is AssemblyName)
                {
                    return ByValueEquality.AssemblyName.GetHashCode((AssemblyName)obj);
                }

                return Fallback.GetHashCode(obj);
            }
        }
    }
}
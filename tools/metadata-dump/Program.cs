// Dump the public and non-public shape of a managed assembly without loading or
// running it: types, base types, fields, properties and methods with decoded
// signatures. Used to answer "which members can an adapter reflect into?" for an
// engine that is packaged as a bundle and cannot simply be loaded.
//
//   metadata-dump <assembly.dll> [output.json] [--filter Text]
//
// The type names in the output are the ones an adapter would pass to Type.GetType.

using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;

namespace MetadataDump;

internal sealed class StringTypeProvider : ISignatureTypeProvider<string, string>
{
    public static readonly StringTypeProvider Instance = new();

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Byte => "byte",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.Double => "double",
        PrimitiveTypeCode.Int16 => "short",
        PrimitiveTypeCode.Int32 => "int",
        PrimitiveTypeCode.Int64 => "long",
        PrimitiveTypeCode.IntPtr => "nint",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.SByte => "sbyte",
        PrimitiveTypeCode.Single => "float",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.TypedReference => "TypedReference",
        PrimitiveTypeCode.UInt16 => "ushort",
        PrimitiveTypeCode.UInt32 => "uint",
        PrimitiveTypeCode.UInt64 => "ulong",
        PrimitiveTypeCode.UIntPtr => "nuint",
        PrimitiveTypeCode.Void => "void",
        _ => typeCode.ToString(),
    };

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var definition = reader.GetTypeDefinition(handle);
        var name = reader.GetString(definition.Name);
        var ns = reader.GetString(definition.Namespace);
        var declaring = definition.GetDeclaringType();
        if (!declaring.IsNil)
        {
            var outer = reader.GetTypeDefinition(declaring);
            return GetTypeFromDefinition(reader, declaring, rawTypeKind) + "." + name;
        }
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var reference = reader.GetTypeReference(handle);
        var name = reader.GetString(reference.Name);
        var ns = reader.GetString(reference.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    public string GetTypeFromSpecification(MetadataReader reader, string genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public string GetSZArrayType(string elementType) => elementType + "[]";

    public string GetArrayType(string elementType, ArrayShape shape)
        => elementType + "[" + new string(',', Math.Max(0, shape.Rank - 1)) + "]";

    public string GetByReferenceType(string elementType) => elementType + "&";

    public string GetPointerType(string elementType) => elementType + "*";

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
        => genericType + "<" + string.Join(", ", typeArguments) + ">";

    public string GetGenericMethodParameter(string genericContext, int index) => "!!" + index;

    public string GetGenericTypeParameter(string genericContext, int index) => "!" + index;

    public string GetFunctionPointerType(MethodSignature<string> signature)
        => "fnptr(" + string.Join(", ", signature.ParameterTypes) + ") -> " + signature.ReturnType;

    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

    public string GetPinnedType(string elementType) => elementType;
}

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: metadata-dump <assembly.dll> [output.json] [--filter Text]");
            return 2;
        }

        var path = Path.GetFullPath(args[0]);
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"file not found: {path}");
            return 2;
        }

        string output = null;
        string filter = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--filter" && i + 1 < args.Length) filter = args[++i];
            else if (output is null) output = args[i];
        }

        using var stream = File.OpenRead(path);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
        {
            Console.Error.WriteLine("the file has no CLI metadata; it is not a managed assembly");
            return 3;
        }

        var reader = peReader.GetMetadataReader();
        var assembly = reader.GetAssemblyDefinition();
        var result = new
        {
            path,
            assembly = reader.GetString(assembly.Name),
            version = assembly.Version.ToString(),
            types = reader.TypeDefinitions
                .Select(handle => DescribeType(reader, handle))
                .Where(t => filter is null || t.FullName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.FullName, StringComparer.Ordinal)
                .ToList(),
        };

        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        if (output is null)
        {
            Console.WriteLine(json);
        }
        else
        {
            File.WriteAllText(Path.GetFullPath(output), json, new UTF8Encoding(false));
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                output = Path.GetFullPath(output),
                types = result.types.Count,
                bytes = Encoding.UTF8.GetByteCount(json),
            }));
        }

        return 0;
    }

    private static TypeShape DescribeType(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var definition = reader.GetTypeDefinition(handle);
        var name = reader.GetString(definition.Name);
        var ns = reader.GetString(definition.Namespace);
        var fullName = string.IsNullOrEmpty(ns) ? name : ns + "." + name;

        var attributes = TypeKindLabel(definition.Attributes);

        return new TypeShape
        {
            FullName = fullName,
            Name = name,
            Namespace = ns,
            BaseType = BaseTypeName(reader, definition.BaseType),
            Attributes = attributes,
            Fields = definition.GetFields()
                .Select(fh =>
                {
                    var field = reader.GetFieldDefinition(fh);
                    var type = field.DecodeSignature(StringTypeProvider.Instance, null);
                    return new MemberShape
                    {
                        Name = reader.GetString(field.Name),
                        Type = type,
                        Access = FieldAccess(field.Attributes),
                        Static = (field.Attributes & FieldAttributes.Static) != 0,
                    };
                })
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .ToList(),
            Properties = definition.GetProperties()
                .Select(ph =>
                {
                    var property = reader.GetPropertyDefinition(ph);
                    var signature = property.DecodeSignature(StringTypeProvider.Instance, null);
                    return new MemberShape
                    {
                        Name = reader.GetString(property.Name),
                        Type = signature.ReturnType,
                        Access = MethodAccess(property.GetAccessors().Getter.IsNil ? default : reader.GetMethodDefinition(property.GetAccessors().Getter).Attributes),
                        Static = !property.GetAccessors().Getter.IsNil && (reader.GetMethodDefinition(property.GetAccessors().Getter).Attributes & MethodAttributes.Static) != 0,
                    };
                })
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToList(),
            Methods = definition.GetMethods()
                .Select(mh =>
                {
                    var method = reader.GetMethodDefinition(mh);
                    var signature = method.DecodeSignature(StringTypeProvider.Instance, null);
                    return new MemberShape
                    {
                        Name = reader.GetString(method.Name),
                        Type = signature.ReturnType + "(" + string.Join(", ", signature.ParameterTypes) + ")",
                        Access = MethodAccess(method.Attributes),
                        Static = (method.Attributes & MethodAttributes.Static) != 0,
                    };
                })
                .OrderBy(m => m.Name, StringComparer.Ordinal)
                .ToList(),
        };
    }

    private static string BaseTypeName(MetadataReader reader, EntityHandle handle)
    {
        if (handle.IsNil) return null;
        return handle.Kind switch
        {
            HandleKind.TypeDefinition => StringTypeProvider.Instance.GetTypeFromDefinition(reader, (TypeDefinitionHandle)handle, 0),
            HandleKind.TypeReference => StringTypeProvider.Instance.GetTypeFromReference(reader, (TypeReferenceHandle)handle, 0),
            HandleKind.TypeSpecification => StringTypeProvider.Instance.GetTypeFromSpecification(reader, null, (TypeSpecificationHandle)handle, 0),
            _ => handle.Kind.ToString(),
        };
    }

    private static string TypeKindLabel(TypeAttributes attributes)
    {
        if ((attributes & TypeAttributes.Interface) != 0) return "interface";
        if ((int)(attributes & TypeAttributes.ClassSemanticsMask) == 0) return (attributes & TypeAttributes.Abstract) != 0 ? "static" : "class";
        return "class";
    }

    private static string FieldAccess(FieldAttributes attributes)
    {
        var access = attributes & FieldAttributes.FieldAccessMask;
        return access switch
        {
            FieldAttributes.Public => "public",
            FieldAttributes.Private => "private",
            FieldAttributes.Family => "protected",
            FieldAttributes.Assembly => "internal",
            FieldAttributes.FamORAssem => "protected internal",
            FieldAttributes.FamANDAssem => "private protected",
            _ => access.ToString(),
        };
    }

    private static string MethodAccess(MethodAttributes attributes)
    {
        var access = attributes & MethodAttributes.MemberAccessMask;
        return access switch
        {
            MethodAttributes.Public => "public",
            MethodAttributes.Private => "private",
            MethodAttributes.Family => "protected",
            MethodAttributes.Assembly => "internal",
            MethodAttributes.FamORAssem => "protected internal",
            MethodAttributes.FamANDAssem => "private protected",
            _ => access.ToString(),
        };
    }
}

internal sealed class TypeShape
{
    public string FullName { get; set; }
    public string Name { get; set; }
    public string Namespace { get; set; }
    public string BaseType { get; set; }
    public string Attributes { get; set; }
    public List<MemberShape> Fields { get; set; }
    public List<MemberShape> Properties { get; set; }
    public List<MemberShape> Methods { get; set; }
}

internal sealed class MemberShape
{
    public string Name { get; set; }
    public string Type { get; set; }
    public string Access { get; set; }
    public bool Static { get; set; }
}

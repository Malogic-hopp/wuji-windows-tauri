using System.Text;
using System.Text.Json;

namespace QuantifiedSelf.Windows.Bridge.ContractGen;

public sealed record GeneratedContractArtifact(string RelativePath, string Content);

public static class ContractGenerator
{
    public const string SchemaRelativePath = "contracts/wuji-bridge/v1/bridge.schema.json";

    public static IReadOnlyList<GeneratedContractArtifact> Generate(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        var schemaPath = Path.Combine(repositoryRoot, SchemaRelativePath.Replace('/', Path.DirectorySeparatorChar));
        using var document = JsonDocument.Parse(File.ReadAllText(schemaPath, Encoding.UTF8));
        var root = document.RootElement;

        var apiVersion = root.GetProperty("x-wuji-api-version").GetString()
            ?? throw new InvalidDataException("Bridge schema api version is missing.");
        var definitions = ReadDefinitions(root.GetProperty("$defs"));

        return
        [
            new(
                "src/QuantifiedSelf.Windows.Client.Bridge/Generated/BridgeContracts.g.cs",
                GenerateCSharp(apiVersion, definitions)),
            new(
                "contracts/wuji-bridge/v1/generated/typescript/bridge-contracts.generated.ts",
                GenerateTypeScript(apiVersion, definitions)),
            new(
                "contracts/wuji-bridge/v1/generated/rust/bridge_contracts.generated.rs",
                GenerateRust(apiVersion, definitions))
        ];
    }

    public static IReadOnlyList<string> FindDrift(string repositoryRoot)
    {
        return Generate(repositoryRoot)
            .Where(artifact =>
            {
                var path = ToAbsolutePath(repositoryRoot, artifact.RelativePath);
                return !File.Exists(path)
                    || !string.Equals(
                        NormalizeLineEndings(File.ReadAllText(path, Encoding.UTF8)),
                        NormalizeLineEndings(artifact.Content),
                        StringComparison.Ordinal);
            })
            .Select(artifact => artifact.RelativePath)
            .ToArray();
    }

    public static void Write(string repositoryRoot)
    {
        foreach (var artifact in Generate(repositoryRoot))
        {
            var path = ToAbsolutePath(repositoryRoot, artifact.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, artifact.Content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private static IReadOnlyDictionary<string, ContractDefinition> ReadDefinitions(JsonElement definitionsElement)
    {
        var definitions = new SortedDictionary<string, ContractDefinition>(StringComparer.Ordinal);

        foreach (var definitionProperty in definitionsElement.EnumerateObject())
        {
            var element = definitionProperty.Value;
            var type = element.GetProperty("type").GetString();

            if (type == "string" && element.TryGetProperty("enum", out var enumElement))
            {
                definitions.Add(
                    definitionProperty.Name,
                    new EnumDefinition(
                        definitionProperty.Name,
                        enumElement.EnumerateArray().Select(value => value.GetString()!).ToArray()));
                continue;
            }

            if (type != "object")
            {
                throw new InvalidDataException(
                    $"Unsupported definition type '{type}' for '{definitionProperty.Name}'.");
            }

            var required = element.TryGetProperty("required", out var requiredElement)
                ? requiredElement.EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            var properties = new List<ContractProperty>();

            if (element.TryGetProperty("properties", out var propertiesElement))
            {
                foreach (var property in propertiesElement.EnumerateObject())
                {
                    properties.Add(new ContractProperty(
                        property.Name,
                        ReadPropertyType(property.Value),
                        required.Contains(property.Name)));
                }
            }

            definitions.Add(
                definitionProperty.Name,
                new ObjectDefinition(definitionProperty.Name, properties));
        }

        ValidateReferences(definitions);
        return definitions;
    }

    private static ContractType ReadPropertyType(JsonElement element)
    {
        if (element.TryGetProperty("$ref", out var referenceElement))
        {
            const string Prefix = "#/$defs/";
            var reference = referenceElement.GetString();
            if (reference is null || !reference.StartsWith(Prefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Unsupported schema reference '{reference}'.");
            }

            return new ContractType(ContractTypeKind.Reference, ReferenceName: reference[Prefix.Length..]);
        }

        var type = element.GetProperty("type").GetString();
        return type switch
        {
            "string" => new ContractType(ContractTypeKind.String),
            "boolean" => new ContractType(ContractTypeKind.Boolean),
            "integer" => new ContractType(ContractTypeKind.Integer),
            "object" when element.TryGetProperty("additionalProperties", out var additionalProperties)
                && additionalProperties.ValueKind == JsonValueKind.True
                => new ContractType(ContractTypeKind.RawObject),
            "array" => ReadArrayType(element.GetProperty("items")),
            _ => throw new InvalidDataException($"Unsupported schema property type '{type}'.")
        };
    }

    private static ContractType ReadArrayType(JsonElement itemsElement)
    {
        var itemType = ReadPropertyType(itemsElement);
        if (itemType.Kind is not (ContractTypeKind.String or ContractTypeKind.Reference))
        {
            throw new InvalidDataException($"Unsupported array item type '{itemType.Kind}'.");
        }

        return new ContractType(ContractTypeKind.Array, ItemType: itemType);
    }

    private static void ValidateReferences(IReadOnlyDictionary<string, ContractDefinition> definitions)
    {
        foreach (var property in definitions.Values.OfType<ObjectDefinition>().SelectMany(value => value.Properties))
        {
            ValidateType(property.Type, definitions, property.JsonName);
        }
    }

    private static void ValidateType(
        ContractType type,
        IReadOnlyDictionary<string, ContractDefinition> definitions,
        string propertyName)
    {
        if (type.Kind == ContractTypeKind.Reference && !definitions.ContainsKey(type.ReferenceName!))
        {
            throw new InvalidDataException(
                $"Property '{propertyName}' references unknown definition '{type.ReferenceName}'.");
        }

        if (type.Kind == ContractTypeKind.Array)
        {
            ValidateType(type.ItemType!, definitions, propertyName);
        }
    }

    private static string GenerateCSharp(
        string apiVersion,
        IReadOnlyDictionary<string, ContractDefinition> definitions)
    {
        var builder = CreateHeader("//", apiVersion);
        builder.AppendLine("#nullable enable");
        builder.AppendLine();
        builder.AppendLine("using System.Text.Json;");
        builder.AppendLine();
        builder.AppendLine("namespace QuantifiedSelf.Windows.Client.Bridge.Generated;");
        builder.AppendLine();

        foreach (var definition in definitions.Values)
        {
            switch (definition)
            {
                case EnumDefinition enumDefinition:
                    builder.AppendLine($"public enum {enumDefinition.Name}");
                    builder.AppendLine("{");
                    foreach (var value in enumDefinition.Values)
                    {
                        builder.AppendLine($"    {ToPascalCase(value)},");
                    }
                    builder.AppendLine("}");
                    break;

                case ObjectDefinition objectDefinition:
                    builder.AppendLine($"public sealed class {objectDefinition.Name}");
                    builder.AppendLine("{");
                    foreach (var property in objectDefinition.Properties)
                    {
                        var propertyType = GetCSharpType(property.Type, definitions);
                        if (!property.Required)
                        {
                            propertyType += "?";
                        }

                        var required = property.Required ? "required " : string.Empty;
                        builder.AppendLine(
                            $"    public {required}{propertyType} {ToPascalCase(property.JsonName)} {{ get; init; }}");
                    }
                    builder.AppendLine("}");
                    break;
            }

            builder.AppendLine();
        }

        return CompleteArtifact(builder);
    }

    private static string GenerateTypeScript(
        string apiVersion,
        IReadOnlyDictionary<string, ContractDefinition> definitions)
    {
        var builder = CreateHeader("//", apiVersion);

        foreach (var definition in definitions.Values)
        {
            switch (definition)
            {
                case EnumDefinition enumDefinition:
                    builder.AppendLine($"export type {enumDefinition.Name} =");
                    for (var index = 0; index < enumDefinition.Values.Count; index++)
                    {
                        var terminator = index == enumDefinition.Values.Count - 1 ? ";" : string.Empty;
                        builder.AppendLine($"  | '{enumDefinition.Values[index]}'{terminator}");
                    }
                    break;

                case ObjectDefinition objectDefinition:
                    if (objectDefinition.Properties.Count == 0)
                    {
                        builder.AppendLine(
                            $"export type {objectDefinition.Name} = Record<string, never>;");
                        break;
                    }

                    builder.AppendLine($"export interface {objectDefinition.Name} {{");
                    foreach (var property in objectDefinition.Properties)
                    {
                        var optional = property.Required ? string.Empty : "?";
                        builder.AppendLine(
                            $"  readonly {property.JsonName}{optional}: {GetTypeScriptType(property.Type)};");
                    }
                    builder.AppendLine("}");
                    break;
            }

            builder.AppendLine();
        }

        return CompleteArtifact(builder);
    }

    private static string GenerateRust(
        string apiVersion,
        IReadOnlyDictionary<string, ContractDefinition> definitions)
    {
        var builder = CreateHeader("//", apiVersion);
        builder.AppendLine("use serde::{Deserialize, Serialize};");
        builder.AppendLine();

        foreach (var definition in definitions.Values)
        {
            switch (definition)
            {
                case EnumDefinition enumDefinition:
                    builder.AppendLine("#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]");
                    builder.AppendLine("#[serde(rename_all = \"snake_case\")]");
                    builder.AppendLine($"pub enum {enumDefinition.Name} {{");
                    foreach (var value in enumDefinition.Values)
                    {
                        builder.AppendLine($"    {ToPascalCase(value)},");
                    }
                    builder.AppendLine("}");
                    break;

                case ObjectDefinition objectDefinition:
                    builder.AppendLine("#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]");
                    builder.AppendLine("#[serde(rename_all = \"camelCase\")]");
                    builder.AppendLine($"pub struct {objectDefinition.Name} {{");
                    foreach (var property in objectDefinition.Properties)
                    {
                        if (!property.Required)
                        {
                            builder.AppendLine("    #[serde(skip_serializing_if = \"Option::is_none\")]");
                        }

                        var type = GetRustType(property.Type);
                        if (!property.Required)
                        {
                            type = $"Option<{type}>";
                        }

                        builder.AppendLine($"    pub {ToSnakeCase(property.JsonName)}: {type},");
                    }
                    builder.AppendLine("}");
                    break;
            }

            builder.AppendLine();
        }

        return CompleteArtifact(builder);
    }

    private static string GetCSharpType(
        ContractType type,
        IReadOnlyDictionary<string, ContractDefinition> definitions)
    {
        return type.Kind switch
        {
            ContractTypeKind.String => "string",
            ContractTypeKind.Boolean => "bool",
            ContractTypeKind.Integer => "long",
            ContractTypeKind.RawObject => "JsonElement",
            ContractTypeKind.Reference => type.ReferenceName!,
            ContractTypeKind.Array => $"IReadOnlyList<{GetCSharpType(type.ItemType!, definitions)}>",
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
    }

    private static string GetTypeScriptType(ContractType type)
    {
        return type.Kind switch
        {
            ContractTypeKind.String => "string",
            ContractTypeKind.Boolean => "boolean",
            ContractTypeKind.Integer => "number",
            ContractTypeKind.RawObject => "Record<string, unknown>",
            ContractTypeKind.Reference => type.ReferenceName!,
            ContractTypeKind.Array => $"ReadonlyArray<{GetTypeScriptType(type.ItemType!)}>",
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
    }

    private static string GetRustType(ContractType type)
    {
        return type.Kind switch
        {
            ContractTypeKind.String => "String",
            ContractTypeKind.Boolean => "bool",
            ContractTypeKind.Integer => "i64",
            ContractTypeKind.RawObject => "serde_json::Value",
            ContractTypeKind.Reference => type.ReferenceName!,
            ContractTypeKind.Array => $"Vec<{GetRustType(type.ItemType!)}>",
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
    }

    private static StringBuilder CreateHeader(string prefix, string apiVersion)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{prefix} <auto-generated />");
        builder.AppendLine($"{prefix} Source: {SchemaRelativePath}");
        builder.AppendLine($"{prefix} API version: {apiVersion}");
        builder.AppendLine($"{prefix} Regenerate with: dotnet run --project tools/QuantifiedSelf.Windows.Bridge.ContractGen -- --write");
        builder.AppendLine();
        return builder;
    }

    private static string ToPascalCase(string value)
    {
        var builder = new StringBuilder();
        var capitalize = true;
        foreach (var character in value)
        {
            if (!char.IsLetterOrDigit(character))
            {
                capitalize = true;
                continue;
            }

            builder.Append(capitalize ? char.ToUpperInvariant(character) : character);
            capitalize = false;
        }

        return builder.ToString();
    }

    private static string ToSnakeCase(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in value)
        {
            if (char.IsUpper(character))
            {
                if (builder.Length > 0)
                {
                    builder.Append('_');
                }
                builder.Append(char.ToLowerInvariant(character));
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static string ToAbsolutePath(string repositoryRoot, string relativePath)
        => Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string NormalizeLineEndings(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string CompleteArtifact(StringBuilder builder)
        => builder.ToString().TrimEnd('\r', '\n') + Environment.NewLine;

    private abstract record ContractDefinition(string Name);

    private sealed record EnumDefinition(string EnumName, IReadOnlyList<string> Values)
        : ContractDefinition(EnumName);

    private sealed record ObjectDefinition(string ObjectName, IReadOnlyList<ContractProperty> Properties)
        : ContractDefinition(ObjectName);

    private sealed record ContractProperty(string JsonName, ContractType Type, bool Required);

    private sealed record ContractType(
        ContractTypeKind Kind,
        string? ReferenceName = null,
        ContractType? ItemType = null);

    private enum ContractTypeKind
    {
        String,
        Boolean,
        Integer,
        RawObject,
        Reference,
        Array
    }
}

public static class ContractGeneratorCli
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            var mode = args.Contains("--write", StringComparer.Ordinal) ? "write"
                : args.Contains("--check", StringComparer.Ordinal) ? "check"
                : null;
            if (mode is null || (args.Contains("--write") && args.Contains("--check")))
            {
                error.WriteLine("Usage: ContractGen (--write|--check) [--repo-root <path>]");
                return 2;
            }

            var repositoryRoot = ReadRepositoryRoot(args);
            if (mode == "write")
            {
                ContractGenerator.Write(repositoryRoot);
                foreach (var artifact in ContractGenerator.Generate(repositoryRoot))
                {
                    output.WriteLine($"Generated {artifact.RelativePath}");
                }
                return 0;
            }

            var drift = ContractGenerator.FindDrift(repositoryRoot);
            if (drift.Count == 0)
            {
                output.WriteLine("Bridge contract artifacts are up to date.");
                return 0;
            }

            error.WriteLine("Bridge contract artifacts are out of date:");
            foreach (var path in drift)
            {
                error.WriteLine($"  {path}");
            }
            return 1;
        }
        catch (Exception exception)
        {
            error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static string ReadRepositoryRoot(string[] args)
    {
        var rootIndex = Array.IndexOf(args, "--repo-root");
        if (rootIndex >= 0)
        {
            if (rootIndex == args.Length - 1 || string.IsNullOrWhiteSpace(args[rootIndex + 1]))
            {
                throw new ArgumentException("--repo-root requires a path.");
            }

            return Path.GetFullPath(args[rootIndex + 1]);
        }

        return FindRepositoryRoot(Directory.GetCurrentDirectory())
            ?? FindRepositoryRoot(AppContext.BaseDirectory)
            ?? throw new DirectoryNotFoundException("Unable to locate QuantifiedSelf.Windows.sln.");
    }

    private static string? FindRepositoryRoot(string startPath)
    {
        var directory = new DirectoryInfo(startPath);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "QuantifiedSelf.Windows.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}

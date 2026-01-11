using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Google.Protobuf.Reflection;

namespace FastDotnet.Protobuf.CodeGen;

public static class Program
{
    public static int Main(string[] args)
    {
        // Args:
        // 0: path to descriptor_set.pb (FileDescriptorSet)
        // 1: output directory
        // 2: root namespace for generated code
        //
        // Optional flags:
        //   --all
        //   --package <protoPackage>   (repeatable)
        //
        // 3..N: (optional) list of types to generate (example: GetTradingStatusRequest GetTradingStatusResponse SecurityTradingStatus)
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage:");
            Console.Error.WriteLine("  FastDotnet.Protobuf.CodeGen <descriptor_set.pb> <outDir> <rootNamespace> [--all] [--package <pkg>] [typeName1 typeName2 ...]");
            return 2;
        }

        var descriptorPath = args[0];
        var outDir         = args[1];
        var rootNamespace  = args[2];

        if (!File.Exists(descriptorPath))
        {
            Console.Error.WriteLine("Descriptor set not found: " + descriptorPath);
            return 3;
        }

        Directory.CreateDirectory(outDir);

        FileDescriptorSet fds;
        using (var fs = File.OpenRead(descriptorPath))
        {
            fds = FileDescriptorSet.Parser.ParseFrom(fs);
        }

        var generateAll = false;
        var packageFilters = new HashSet<string>(StringComparer.Ordinal);

        var argi = 3;
        while (argi < args.Length && args[argi].StartsWith("--", StringComparison.Ordinal))
        {
            var opt = args[argi];
            if (string.Equals(opt, "--all", StringComparison.Ordinal))
            {
                generateAll = true;
                argi++;
                continue;
            }
            if (string.Equals(opt, "--package", StringComparison.Ordinal))
            {
                if (argi + 1 >= args.Length)
                {
                    Console.Error.WriteLine("Missing value for --package.");
                    return 2;
                }

                packageFilters.Add(args[argi + 1]);
                argi += 2;
                continue;
            }

            Console.Error.WriteLine("Unknown option: " + opt);
            return 2;
        }

        // If no filters are provided, write a report and exit.
        if (!generateAll && argi == args.Length)
        {
            WriteReport(rootNamespace, outDir, fds);
            Console.WriteLine("OK: descriptor parsed. Report: " + Path.Combine(outDir, "codegen.report.txt"));
            return 0;
        }

        // Build type indexes for resolving references (enum/message).
        BuildTypeIndexes(
            fds,
            out var enumsByFullName,
            out var messagesByFullName,
            out var fileByEnum,
            out var fileByMessage,
            out var enumCSharpNameByFullName,
            out var messageCSharpNameByFullName);

        bool PackageAllowed(string? pkg)
        {
            if (packageFilters.Count == 0)
            {
                return true;
            }

            return packageFilters.Contains(pkg ?? string.Empty);
        }

        // Generate everything (optionally filtered by package).
        if (generateAll)
        {
            var enumFullNames = new List<string>(enumsByFullName.Count);
            foreach (var kv in enumsByFullName)
            {
                var enumFile = fileByEnum[kv.Key];
                if (PackageAllowed(enumFile.Package))
                {
                    enumFullNames.Add(kv.Key);
                }
            }
            enumFullNames.Sort(StringComparer.Ordinal);

            var msgFullNames = new List<string>(messagesByFullName.Count);
            foreach (var kv in messagesByFullName)
            {
                var msgFile = fileByMessage[kv.Key];
                if (PackageAllowed(msgFile.Package))
                {
                    msgFullNames.Add(kv.Key);
                }
            }
            msgFullNames.Sort(StringComparer.Ordinal);

            for (int i = 0; i < enumFullNames.Count; i++)
            {
                var enumFullName = enumFullNames[i];
                var enumDesc = enumsByFullName[enumFullName];
                var enumFile = fileByEnum[enumFullName];
                var enumCSharpName = enumCSharpNameByFullName[enumFullName];

                var code = EmitEnum(rootNamespace, enumDesc, enumFile.Package, enumCSharpName);
                var outPath = Path.Combine(outDir, MakeSafeFileName(enumFullName.TrimStart('.')) + ".g.cs");
                File.WriteAllText(outPath, code, Encoding.UTF8);
            }

            for (int i = 0; i < msgFullNames.Count; i++)
            {
                var msgFullName = msgFullNames[i];
                var msgDesc = messagesByFullName[msgFullName];
                var msgFile = fileByMessage[msgFullName];
                var msgCSharpName = messageCSharpNameByFullName[msgFullName];

                var code = EmitMessageFast(
                    rootNamespace,
                    msgDesc,
                    msgFile.Package,
                    enumsByFullName,
                    messagesByFullName,
                    enumCSharpNameByFullName,
                    messageCSharpNameByFullName,
                    msgCSharpName);

                var outPath = Path.Combine(outDir, MakeSafeFileName(msgFullName.TrimStart('.')) + ".g.cs");
                File.WriteAllText(outPath, code, Encoding.UTF8);
            }

            Console.WriteLine("OK: generated enums=" + enumFullNames.Count.ToString(CultureInfo.InvariantCulture) +
                              ", messages=" + msgFullNames.Count.ToString(CultureInfo.InvariantCulture) +
                              " into: " + outDir);
            return 0;
        }

        // Collect target types (user-provided names).
        var targets = new string[args.Length - argi];
        for (int i = argi; i < args.Length; i++)
        {
            targets[i - argi] = args[i];
        }

        // Generate requested types (message -> *Fast, enum -> enum).
        for (int i = 0; i < targets.Length; i++)
        {
            var target = targets[i];

            // Try resolve enum by short name or by full name.
            if (TryResolveEnum(target, enumsByFullName, out var enumFullName, out var enumDesc))
            {
                var enumFile = fileByEnum[enumFullName];
                var enumCSharpName = enumCSharpNameByFullName[enumFullName];
                var code = EmitEnum(rootNamespace, enumDesc, enumFile.Package, enumCSharpName);
                var outPath = Path.Combine(outDir, MakeSafeFileName(enumFullName.TrimStart('.')) + ".g.cs");
                File.WriteAllText(outPath, code, Encoding.UTF8);
                continue;
            }

            // Try resolve message by short name or by full name.
            if (TryResolveMessage(target, messagesByFullName, out var msgFullName, out var msgDesc))
            {
                var msgFile = fileByMessage[msgFullName];
                var msgCSharpName = messageCSharpNameByFullName[msgFullName];
                var code = EmitMessageFast(
                    rootNamespace,
                    msgDesc,
                    msgFile.Package,
                    enumsByFullName,
                    messagesByFullName,
                    enumCSharpNameByFullName,
                    messageCSharpNameByFullName,
                    msgCSharpName);

                var outPath = Path.Combine(outDir, MakeSafeFileName(msgFullName.TrimStart('.')) + ".g.cs");
                File.WriteAllText(outPath, code, Encoding.UTF8);
                continue;
            }

            Console.Error.WriteLine("Type not found in descriptor set: " + target);
            return 4;
        }

        Console.WriteLine("OK: generated " + targets.Length.ToString(CultureInfo.InvariantCulture) + " type(s) into: " + outDir);
        return 0;
    }

    static void WriteReport(string rootNamespace, string outDir, FileDescriptorSet fds)
    {
        var fileCount = fds.File.Count;
        var msgCount  = 0;
        var enumCount = 0;
        for (int i = 0; i < fileCount; i++)
        {
            var file = fds.File[i];
            // Safety: descriptor sets may theoretically contain null elements.
            if (file == null)
            {
                continue;
            }
            msgCount  += file.MessageType.Count;
            enumCount += file.EnumType.Count;
        }

        var reportPath = Path.Combine(outDir, "codegen.report.txt");
        File.WriteAllText(reportPath, "rootNamespace=" + rootNamespace + Environment.NewLine +
                                      "files=" + fileCount + Environment.NewLine +
                                      "messages=" + msgCount + Environment.NewLine +
                                      "enums=" + enumCount + Environment.NewLine);
    }

    static void BuildTypeIndexes(
        FileDescriptorSet fds,
        out Dictionary<string, EnumDescriptorProto> enumsByFullName,
        out Dictionary<string, DescriptorProto> messagesByFullName,
        out Dictionary<string, FileDescriptorProto> fileByEnum,
        out Dictionary<string, FileDescriptorProto> fileByMessage,
        out Dictionary<string, string> enumCSharpNameByFullName,
        out Dictionary<string, string> messageCSharpNameByFullName)
    {
        enumsByFullName    = new Dictionary<string, EnumDescriptorProto>(StringComparer.Ordinal);
        messagesByFullName = new Dictionary<string, DescriptorProto>(StringComparer.Ordinal);
        fileByEnum         = new Dictionary<string, FileDescriptorProto>(StringComparer.Ordinal);
        fileByMessage      = new Dictionary<string, FileDescriptorProto>(StringComparer.Ordinal);
        enumCSharpNameByFullName = new Dictionary<string, string>(StringComparer.Ordinal);
        messageCSharpNameByFullName = new Dictionary<string, string>(StringComparer.Ordinal);

        for (int i = 0; i < fds.File.Count; i++)
        {
            var file = fds.File[i];
            if (file == null)
            {
                continue;
            }
            var pkg = file.Package ?? string.Empty;
            var prefix = pkg.Length == 0 ? string.Empty : "." + pkg;

            // Enum types (top-level)
            for (int e = 0; e < file.EnumType.Count; e++)
            {
                var en = file.EnumType[e];
                var full = prefix + "." + en.Name;
                enumsByFullName[full] = en;
                fileByEnum[full] = file;
                enumCSharpNameByFullName[full] = GetGeneratedEnumName(full, pkg);
            }

            // Messages (top-level) + nested
            for (int m = 0; m < file.MessageType.Count; m++)
            {
                var msg = file.MessageType[m];
                IndexMessage(prefix + "." + msg.Name, msg, file, messagesByFullName, fileByMessage, enumsByFullName, fileByEnum, enumCSharpNameByFullName, pkg);
            }
        }

        // Now that all messages are indexed, compute C# names for messages (need fullName + package).
        foreach (var kv in messagesByFullName)
        {
            var msgFullName = kv.Key;
            var msgFile = fileByMessage[msgFullName];
            messageCSharpNameByFullName[msgFullName] = GetGeneratedMessageName(msgFullName, msgFile.Package ?? string.Empty);
        }
    }

    static void IndexMessage(
        string fullName,
        DescriptorProto msg,
        FileDescriptorProto file,
        Dictionary<string, DescriptorProto> messagesByFullName,
        Dictionary<string, FileDescriptorProto> fileByMessage,
        Dictionary<string, EnumDescriptorProto> enumsByFullName,
        Dictionary<string, FileDescriptorProto> fileByEnum,
        Dictionary<string, string> enumCSharpNameByFullName,
        string protoPackage)
    {
        messagesByFullName[fullName] = msg;
        fileByMessage[fullName] = file;

        for (int e = 0; e < msg.EnumType.Count; e++)
        {
            var en = msg.EnumType[e];
            var enumFull = fullName + "." + en.Name;
            enumsByFullName[enumFull] = en;
            fileByEnum[enumFull] = file;
            enumCSharpNameByFullName[enumFull] = GetGeneratedEnumName(enumFull, protoPackage);
        }

        for (int i = 0; i < msg.NestedType.Count; i++)
        {
            var nested = msg.NestedType[i];
            IndexMessage(fullName + "." + nested.Name, nested, file, messagesByFullName, fileByMessage, enumsByFullName, fileByEnum, enumCSharpNameByFullName, protoPackage);
        }
    }

    static string GetGeneratedMessageName(string fullName, string protoPackage)
    {
        // Full name example: ".tinkoff.public...v1.PostStopOrderRequest.TrailingData"
        // -> "PostStopOrderRequest_TrailingData"
        var rel = GetRelativeTypeName(fullName, protoPackage);
        return rel.Replace('.', '_');
    }

    static string GetGeneratedEnumName(string fullName, string protoPackage)
    {
        // Full name example: ".tinkoff.public...v1.CandleInterval"
        // -> "CandleInterval"
        // Nested example: ".tinkoff...v1.PostOrderRequest.OrderType"
        // -> "PostOrderRequest_OrderType"
        var rel = GetRelativeTypeName(fullName, protoPackage);
        return rel.Replace('.', '_');
    }

    static string GetRelativeTypeName(string fullName, string protoPackage)
    {
        // Returns type name without package prefix (but preserves nesting with '.')
        var tn = fullName.TrimStart('.');
        if (protoPackage.Length == 0)
        {
            return tn;
        }

        var pkgPrefix = protoPackage + ".";
        if (tn.StartsWith(pkgPrefix, StringComparison.Ordinal))
        {
            return tn.Substring(pkgPrefix.Length);
        }

        return tn;
    }

    static bool TryResolveEnum(
        string typeName,
        Dictionary<string, EnumDescriptorProto> enumsByFullName,
        out string fullName,
        out EnumDescriptorProto desc)
    {
        fullName = string.Empty;
        desc = null!;

        // Try full name as-is (with or without a leading dot).
        if (typeName.Length != 0 && typeName[0] != '.')
        {
            var withDot = "." + typeName;
            if (enumsByFullName.TryGetValue(withDot, out var found) && found != null)
            {
                fullName = withDot;
                desc = found;
                return true;
            }
        }
        if (enumsByFullName.TryGetValue(typeName, out var found2) && found2 != null)
        {
            fullName = typeName;
            desc = found2;
            return true;
        }

        // Search by short name (last segment).
        foreach (var kv in enumsByFullName)
        {
            if (kv.Value.Name == typeName)
            {
                fullName = kv.Key;
                desc = kv.Value;
                return true;
            }
        }
        return false;
    }

    static bool TryResolveMessage(
        string typeName,
        Dictionary<string, DescriptorProto> messagesByFullName,
        out string fullName,
        out DescriptorProto desc)
    {
        fullName = string.Empty;
        desc = null!;

        if (typeName.Length != 0 && typeName[0] != '.')
        {
            var withDot = "." + typeName;
            if (messagesByFullName.TryGetValue(withDot, out var found) && found != null)
            {
                fullName = withDot;
                desc = found;
                return true;
            }
        }
        if (messagesByFullName.TryGetValue(typeName, out var found2) && found2 != null)
        {
            fullName = typeName;
            desc = found2;
            return true;
        }

        foreach (var kv in messagesByFullName)
        {
            if (kv.Value.Name == typeName)
            {
                fullName = kv.Key;
                desc = kv.Value;
                return true;
            }
        }
        return false;
    }

    static string EmitEnum(string rootNamespace, EnumDescriptorProto en, string protoPackage, string enumCSharpName)
    {
        var sb = new StringBuilder(8 * 1024);
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.Append("namespace ").Append(rootNamespace).AppendLine(";");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.Append("/// Enum from proto package ").Append(protoPackage).AppendLine(".");
        sb.AppendLine("/// </summary>");
        sb.Append("public enum ").Append(enumCSharpName).AppendLine();
        sb.AppendLine("{");

        var enumPrefix = ToScreamingSnake(enumCSharpName) + "_";
        for (int i = 0; i < en.Value.Count; i++)
        {
            var v = en.Value[i];
            var raw = v.Name ?? string.Empty;
            if (raw.StartsWith(enumPrefix, StringComparison.Ordinal))
            {
                raw = raw.Substring(enumPrefix.Length);
            }

            var csValueName = ToPascalEnumValue(raw);
            if (csValueName.Length == 0)
            {
                csValueName = "Value" + v.Number.ToString(CultureInfo.InvariantCulture);
            }

            // Leading digit is not a valid C# identifier.
            if (csValueName.Length != 0 && char.IsDigit(csValueName[0]))
            {
                csValueName = "_" + csValueName;
            }

            // Avoid invalid member name equal to enum name.
            csValueName = SafeMemberName(csValueName, enumCSharpName);

            sb.Append("    ").Append(csValueName).Append(" = ").Append(v.Number.ToString(CultureInfo.InvariantCulture)).AppendLine(",");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    static string EmitMessageFast(
        string rootNamespace,
        DescriptorProto msg,
        string protoPackage,
        Dictionary<string, EnumDescriptorProto> enumsByFullName,
        Dictionary<string, DescriptorProto> messagesByFullName,
        Dictionary<string, string> enumCSharpNameByFullName,
        Dictionary<string, string> messageCSharpNameByFullName,
        string messageCSharpName)
    {
        var sb = new StringBuilder(32 * 1024);
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using Google.Protobuf.Fast.Abstractions;");
        sb.AppendLine("using Google.Protobuf.Fast.Pooling;");
        sb.AppendLine("using Google.Protobuf.Fast.Proto;");
        sb.AppendLine();
        sb.Append("namespace ").Append(rootNamespace).AppendLine(";");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.Append("/// Fast DTO (protobuf wire format) for proto package ").Append(protoPackage).AppendLine(".");
        sb.AppendLine("/// </summary>");
        sb.Append("public sealed class ").Append(messageCSharpName).Append(" : PooledObjectBase, IFastMessage").AppendLine();
        sb.AppendLine("{");

        sb.Append("    static readonly ObjectPool<").Append(messageCSharpName).Append("> s_pool = new(128, static () => new ").Append(messageCSharpName).Append("());").AppendLine();
        sb.AppendLine();
        sb.AppendLine("    // Primary API: RentValue/Return (explicit return).");
        sb.Append("    public static ").Append(messageCSharpName).Append(" Rent() => s_pool.Rent();").AppendLine();
        sb.Append("    public static void Return(").Append(messageCSharpName).Append(" value) => s_pool.Return(value);").AppendLine();
        sb.AppendLine();

        sb.Append("    internal ").Append(messageCSharpName).AppendLine("() { }");
        sb.AppendLine();

        // Special case: google.protobuf.Timestamp helpers (Google-style API surface used in existing code).
        if (protoPackage == "google.protobuf" && messageCSharpName == "Timestamp")
        {
            sb.AppendLine("    static readonly System.DateTime s_unixEpoch = new System.DateTime(1970, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);");
            sb.AppendLine();
            sb.AppendLine("    public static Timestamp FromDateTime(System.DateTime dateTime)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (dateTime.Kind != System.DateTimeKind.Utc)");
            sb.AppendLine("        {");
            sb.AppendLine("            dateTime = dateTime.ToUniversalTime();");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        var ticks = dateTime.Ticks - s_unixEpoch.Ticks; // 100ns");
            sb.AppendLine("        var seconds = ticks / System.TimeSpan.TicksPerSecond;");
            sb.AppendLine("        var nanos = (int)((ticks % System.TimeSpan.TicksPerSecond) * 100);");
            sb.AppendLine();
            sb.AppendLine("        var ts = Timestamp.Rent();");
            sb.AppendLine("        ts.Seconds = seconds;");
            sb.AppendLine("        ts.Nanos = nanos;");
            sb.AppendLine("        return ts;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    public System.DateTime ToDateTime()");
            sb.AppendLine("    {");
            sb.AppendLine("        var ticks = (Seconds * System.TimeSpan.TicksPerSecond) + (long)(Nanos / 100);");
            sb.AppendLine("        return new System.DateTime(s_unixEpoch.Ticks + ticks, System.DateTimeKind.Utc);");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // Fields
        for (int i = 0; i < msg.Field.Count; i++)
        {
            var f = msg.Field[i];
            // Proto3 optional is encoded as a synthetic oneof (OneofIndex != 0 + Proto3Optional=true).
            if (f.OneofIndex != 0 && !f.Proto3Optional)
            {
                sb.Append("    // TODO: oneof field is not supported yet: ").Append(f.Name).AppendLine();
                continue;
            }

            var csType = GetCSharpFieldType(f, enumsByFullName, messagesByFullName, enumCSharpNameByFullName, messageCSharpNameByFullName);
            var csName = SafeMemberName(ToPascal(f.Name), messageCSharpName);

            // Repeated fields are represented as List<T> (cleared on pool return).
            if (f.Label == FieldDescriptorProto.Types.Label.Repeated)
            {
                sb.Append("    public List<").Append(csType).Append("> ").Append(csName).AppendLine(" = new(4);");
            }
            else if (csType == "string")
            {
                sb.Append("    public ").Append(csType).Append(' ').Append(csName).AppendLine(" = string.Empty;");
            }
            else if (csType == "byte[]")
            {
                sb.Append("    public ").Append(csType).Append(' ').Append(csName).AppendLine(" = System.Array.Empty<byte>();");
            }
            else if (f.Type == FieldDescriptorProto.Types.Type.Message)
            {
                // Messages are always initialized to avoid null checks.
                sb.Append("    public ").Append(csType).Append(' ').Append(csName).AppendLine(" = new();");
            }
            else
            {
                sb.Append("    public ").Append(csType).Append(' ').Append(csName).AppendLine(";");
            }
        }

        sb.AppendLine();
        sb.AppendLine("    public void Clear()");
        sb.AppendLine("    {");
        for (int i = 0; i < msg.Field.Count; i++)
        {
            var f = msg.Field[i];
            if (f.OneofIndex != 0 && !f.Proto3Optional)
            {
                continue;
            }

            var csName = SafeMemberName(ToPascal(f.Name), messageCSharpName);
            var csType = GetCSharpFieldType(f, enumsByFullName, messagesByFullName, enumCSharpNameByFullName, messageCSharpNameByFullName);

            if (f.Label == FieldDescriptorProto.Types.Label.Repeated)
            {
                if (f.Type == FieldDescriptorProto.Types.Type.Message)
                {
                    sb.Append("        for (int i = 0; i < ").Append(csName).Append(".Count; i++)").AppendLine();
                    sb.AppendLine("        {");
                    sb.Append("            global::").Append(rootNamespace).Append('.').Append(csType).Append(".Return(").Append(csName).Append("[i]);").AppendLine();
                    sb.AppendLine("        }");
                }

                sb.Append("        ").Append(csName).Append(".Clear();").AppendLine();
                continue;
            }

            // Reset without nullable types.
            if (csType == "string")
            {
                sb.Append("        ").Append(csName).Append(" = string.Empty;").AppendLine();
            }
            else if (csType == "byte[]")
            {
                sb.Append("        ").Append(csName).Append(" = System.Array.Empty<byte>();").AppendLine();
            }
            else if (csType == "bool")
            {
                sb.Append("        ").Append(csName).Append(" = false;").AppendLine();
            }
            else if (f.Type == FieldDescriptorProto.Types.Type.Message)
            {
                sb.Append("        ").Append(csName).Append(".Clear();").AppendLine();
            }
            else
            {
                sb.Append("        ").Append(csName).Append(" = default;").AppendLine();
            }
        }
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine("    public void WriteTo(ref ProtoWriter writer)");
        sb.AppendLine("    {");
        for (int i = 0; i < msg.Field.Count; i++)
        {
            var f = msg.Field[i];
            if (f.OneofIndex != 0 && !f.Proto3Optional)
            {
                continue;
            }

            var csName = SafeMemberName(ToPascal(f.Name), messageCSharpName);
            EmitWriteField(sb, f, csName, enumsByFullName, messagesByFullName);
        }
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine("    public void MergeFrom(ref ProtoReader reader)");
        sb.AppendLine("    {");
        sb.AppendLine("        while (reader.TryReadTag(out var fieldNumber, out var wireType))");
        sb.AppendLine("        {");
        sb.AppendLine("            switch (fieldNumber)");
        sb.AppendLine("            {");
        for (int i = 0; i < msg.Field.Count; i++)
        {
            var f = msg.Field[i];
            if (f.OneofIndex != 0 && !f.Proto3Optional)
            {
                continue;
            }

            var csName = SafeMemberName(ToPascal(f.Name), messageCSharpName);
            EmitReadCase(sb, rootNamespace, f, csName, enumsByFullName, messagesByFullName, enumCSharpNameByFullName, messageCSharpNameByFullName);
        }
        sb.AppendLine("                default:");
        sb.AppendLine("                    reader.SkipField(wireType);");
        sb.AppendLine("                    break;");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    static string GetCSharpFieldType(
        FieldDescriptorProto f,
        Dictionary<string, EnumDescriptorProto> enumsByFullName,
        Dictionary<string, DescriptorProto> messagesByFullName,
        Dictionary<string, string> enumCSharpNameByFullName,
        Dictionary<string, string> messageCSharpNameByFullName)
    {
        switch (f.Type)
        {
            case FieldDescriptorProto.Types.Type.Bool:
                return "bool";
            case FieldDescriptorProto.Types.Type.Int32:
            case FieldDescriptorProto.Types.Type.Sint32:
            case FieldDescriptorProto.Types.Type.Sfixed32:
                return "int";
            case FieldDescriptorProto.Types.Type.Uint32:
            case FieldDescriptorProto.Types.Type.Fixed32:
                return "uint";
            case FieldDescriptorProto.Types.Type.Int64:
            case FieldDescriptorProto.Types.Type.Sint64:
            case FieldDescriptorProto.Types.Type.Sfixed64:
                return "long";
            case FieldDescriptorProto.Types.Type.Uint64:
            case FieldDescriptorProto.Types.Type.Fixed64:
                return "ulong";
            case FieldDescriptorProto.Types.Type.Float:
                return "float";
            case FieldDescriptorProto.Types.Type.Double:
                return "double";
            case FieldDescriptorProto.Types.Type.String:
                return "string";
            case FieldDescriptorProto.Types.Type.Bytes:
                // TODO: bytes support later (likely ReadOnlyMemory<byte> + pooling/segments).
                return "byte[]";
            case FieldDescriptorProto.Types.Type.Enum:
            {
                var tn = NormalizeTypeName(f.TypeName);
                if (!enumsByFullName.ContainsKey(tn))
                {
                    throw new NotSupportedException("Enum type not found: " + tn);
                }
                return enumCSharpNameByFullName[tn];
            }
            case FieldDescriptorProto.Types.Type.Message:
            {
                var tn = NormalizeTypeName(f.TypeName);
                if (!messagesByFullName.ContainsKey(tn))
                {
                    throw new NotSupportedException("Message type not found: " + tn);
                }
                return messageCSharpNameByFullName[tn];
            }
            default:
                throw new NotSupportedException("Field type not supported: " + f.Type);
        }
    }

    static void EmitWriteField(
        StringBuilder sb,
        FieldDescriptorProto f,
        string csName,
        Dictionary<string, EnumDescriptorProto> enumsByFullName,
        Dictionary<string, DescriptorProto> messagesByFullName)
    {
        // Write only non-default values (proto3 style) to keep payload smaller.
        if (f.Label == FieldDescriptorProto.Types.Label.Repeated)
        {
            EmitWriteRepeatedField(sb, f, csName, enumsByFullName, messagesByFullName);
            return;
        }

        switch (f.Type)
        {
            case FieldDescriptorProto.Types.Type.String:
                sb.Append("        if (").Append(csName).Append(".Length != 0)").AppendLine();
                sb.AppendLine("        {");
                sb.Append("            writer.WriteTag(").Append(f.Number.ToString(CultureInfo.InvariantCulture)).Append(", WireType.LengthDelimited);").AppendLine();
                sb.Append("            writer.WriteString(").Append(csName).Append(");").AppendLine();
                sb.AppendLine("        }");
                return;
            case FieldDescriptorProto.Types.Type.Bool:
                sb.Append("        if (").Append(csName).Append(")").AppendLine();
                sb.AppendLine("        {");
                sb.Append("            writer.WriteTag(").Append(f.Number.ToString(CultureInfo.InvariantCulture)).Append(", WireType.Varint);").AppendLine();
                sb.AppendLine("            writer.WriteUInt32(1);");
                sb.AppendLine("        }");
                return;
            case FieldDescriptorProto.Types.Type.Message:
            {
                // Messages are always initialized; write as length-delimited submessage.
                sb.AppendLine("        {");
                sb.Append("            writer.WriteTag(").Append(f.Number.ToString(CultureInfo.InvariantCulture)).Append(", WireType.LengthDelimited);").AppendLine();
                sb.Append("            FastMessageCodec.WriteMessage(ref writer, ").Append(csName).Append(");").AppendLine();
                sb.AppendLine("        }");
                return;
            }
            case FieldDescriptorProto.Types.Type.Enum:
            case FieldDescriptorProto.Types.Type.Int32:
            case FieldDescriptorProto.Types.Type.Sint32:
            case FieldDescriptorProto.Types.Type.Sfixed32:
                sb.Append("        if (").Append(csName).Append(" != 0)").AppendLine();
                sb.AppendLine("        {");
                sb.Append("            writer.WriteTag(").Append(f.Number.ToString(CultureInfo.InvariantCulture)).Append(", WireType.Varint);").AppendLine();
                sb.Append("            writer.WriteUInt32((uint)").Append(csName).Append(");").AppendLine();
                sb.AppendLine("        }");
                return;
            case FieldDescriptorProto.Types.Type.Int64:
            case FieldDescriptorProto.Types.Type.Sint64:
            case FieldDescriptorProto.Types.Type.Sfixed64:
                sb.Append("        if (").Append(csName).Append(" != 0)").AppendLine();
                sb.AppendLine("        {");
                sb.Append("            writer.WriteTag(").Append(f.Number.ToString(CultureInfo.InvariantCulture)).Append(", WireType.Varint);").AppendLine();
                sb.Append("            writer.WriteInt64(").Append(csName).Append(");").AppendLine();
                sb.AppendLine("        }");
                return;
            default:
                sb.Append("        // TODO: WriteTo generator does not support this field yet: ").Append(f.Name).Append(" (type=").Append(f.Type.ToString()).AppendLine(").");
                return;
        }
    }

    static void EmitReadCase(
        StringBuilder sb,
        string rootNamespace,
        FieldDescriptorProto f,
        string csName,
        Dictionary<string, EnumDescriptorProto> enumsByFullName,
        Dictionary<string, DescriptorProto> messagesByFullName,
        Dictionary<string, string> enumCSharpNameByFullName,
        Dictionary<string, string> messageCSharpNameByFullName)
    {
        sb.Append("                case ").Append(f.Number.ToString(CultureInfo.InvariantCulture)).AppendLine(":");

        if (f.Label == FieldDescriptorProto.Types.Label.Repeated)
        {
            EmitReadRepeatedCase(sb, rootNamespace, f, csName, enumsByFullName, messagesByFullName, enumCSharpNameByFullName, messageCSharpNameByFullName);
            return;
        }

        switch (f.Type)
        {
            case FieldDescriptorProto.Types.Type.String:
                sb.Append("                    ").Append(csName).Append(" = reader.ReadString();").AppendLine();
                sb.AppendLine("                    break;");
                return;
            case FieldDescriptorProto.Types.Type.Bool:
                sb.Append("                    ").Append(csName).Append(" = reader.ReadUInt32() != 0;").AppendLine();
                sb.AppendLine("                    break;");
                return;
            case FieldDescriptorProto.Types.Type.Enum:
            {
                var tn = NormalizeTypeName(f.TypeName);
                var enumName = enumCSharpNameByFullName[tn];
                sb.Append("                    ").Append(csName).Append(" = (").Append(enumName).Append(")reader.ReadUInt32();").AppendLine();
                sb.AppendLine("                    break;");
                return;
            }
            case FieldDescriptorProto.Types.Type.Int32:
            case FieldDescriptorProto.Types.Type.Sint32:
            case FieldDescriptorProto.Types.Type.Sfixed32:
                sb.Append("                    ").Append(csName).Append(" = (int)reader.ReadUInt32();").AppendLine();
                sb.AppendLine("                    break;");
                return;
            case FieldDescriptorProto.Types.Type.Int64:
            case FieldDescriptorProto.Types.Type.Sint64:
            case FieldDescriptorProto.Types.Type.Sfixed64:
                sb.Append("                    ").Append(csName).Append(" = reader.ReadInt64();").AppendLine();
                sb.AppendLine("                    break;");
                return;
            case FieldDescriptorProto.Types.Type.Message:
            {
                var tn = NormalizeTypeName(f.TypeName);
                var msgName = messageCSharpNameByFullName[tn];
                sb.AppendLine("                    {");
                sb.AppendLine("                        var bytes = reader.ReadBytes();");
                sb.AppendLine("                        var sub = new ProtoReader(bytes);");
                sb.Append("                        ").Append(csName).Append(".MergeFrom(ref sub);").AppendLine();
                sb.AppendLine("                    }");
                sb.AppendLine("                    break;");
                return;
            }
            default:
                sb.AppendLine("                    reader.SkipField(wireType);");
                sb.AppendLine("                    break;");
                return;
        }
    }

    static void EmitWriteRepeatedField(
        StringBuilder sb,
        FieldDescriptorProto f,
        string csName,
        Dictionary<string, EnumDescriptorProto> enumsByFullName,
        Dictionary<string, DescriptorProto> messagesByFullName)
    {
        sb.Append("        for (int i = 0; i < ").Append(csName).Append(".Count; i++)").AppendLine();
        sb.AppendLine("        {");

        // Access element once to avoid repeating indexer.
        sb.Append("            var value = ").Append(csName).Append("[i];").AppendLine();

        switch (f.Type)
        {
            case FieldDescriptorProto.Types.Type.String:
                sb.AppendLine("            writer.WriteTag(" + f.Number.ToString(CultureInfo.InvariantCulture) + ", WireType.LengthDelimited);");
                sb.AppendLine("            writer.WriteString(value);");
                sb.AppendLine("        }");
                return;
            case FieldDescriptorProto.Types.Type.Bool:
                sb.AppendLine("            writer.WriteTag(" + f.Number.ToString(CultureInfo.InvariantCulture) + ", WireType.Varint);");
                sb.AppendLine("            writer.WriteUInt32(value ? 1u : 0u);");
                sb.AppendLine("        }");
                return;
            case FieldDescriptorProto.Types.Type.Enum:
            case FieldDescriptorProto.Types.Type.Int32:
            case FieldDescriptorProto.Types.Type.Sint32:
            case FieldDescriptorProto.Types.Type.Sfixed32:
                sb.AppendLine("            writer.WriteTag(" + f.Number.ToString(CultureInfo.InvariantCulture) + ", WireType.Varint);");
                sb.AppendLine("            writer.WriteUInt32((uint)value);");
                sb.AppendLine("        }");
                return;
            case FieldDescriptorProto.Types.Type.Int64:
            case FieldDescriptorProto.Types.Type.Sint64:
            case FieldDescriptorProto.Types.Type.Sfixed64:
                sb.AppendLine("            writer.WriteTag(" + f.Number.ToString(CultureInfo.InvariantCulture) + ", WireType.Varint);");
                sb.AppendLine("            writer.WriteInt64(value);");
                sb.AppendLine("        }");
                return;
            case FieldDescriptorProto.Types.Type.Message:
                sb.AppendLine("            writer.WriteTag(" + f.Number.ToString(CultureInfo.InvariantCulture) + ", WireType.LengthDelimited);");
                sb.AppendLine("            FastMessageCodec.WriteMessage(ref writer, value);");
                sb.AppendLine("        }");
                return;
            default:
                sb.Append("            // TODO: WriteTo generator does not support repeated field yet: ").Append(f.Name).Append(" (type=").Append(f.Type.ToString()).AppendLine(").");
                sb.AppendLine("        }");
                return;
        }
    }

    static void EmitReadRepeatedCase(
        StringBuilder sb,
        string rootNamespace,
        FieldDescriptorProto f,
        string csName,
        Dictionary<string, EnumDescriptorProto> enumsByFullName,
        Dictionary<string, DescriptorProto> messagesByFullName,
        Dictionary<string, string> enumCSharpNameByFullName,
        Dictionary<string, string> messageCSharpNameByFullName)
    {
        switch (f.Type)
        {
            case FieldDescriptorProto.Types.Type.String:
                sb.Append("                    ").Append(csName).Append(".Add(reader.ReadString());").AppendLine();
                sb.AppendLine("                    break;");
                return;
            case FieldDescriptorProto.Types.Type.Bool:
                sb.Append("                    ").Append(csName).Append(".Add(reader.ReadUInt32() != 0);").AppendLine();
                sb.AppendLine("                    break;");
                return;
            case FieldDescriptorProto.Types.Type.Enum:
            {
                var tn = NormalizeTypeName(f.TypeName);
                var enumName = enumCSharpNameByFullName[tn];
                sb.Append("                    ").Append(csName).Append(".Add((").Append(enumName).Append(")reader.ReadUInt32());").AppendLine();
                sb.AppendLine("                    break;");
                return;
            }
            case FieldDescriptorProto.Types.Type.Int32:
            case FieldDescriptorProto.Types.Type.Sint32:
            case FieldDescriptorProto.Types.Type.Sfixed32:
                sb.Append("                    ").Append(csName).Append(".Add((int)reader.ReadUInt32());").AppendLine();
                sb.AppendLine("                    break;");
                return;
            case FieldDescriptorProto.Types.Type.Int64:
            case FieldDescriptorProto.Types.Type.Sint64:
            case FieldDescriptorProto.Types.Type.Sfixed64:
                sb.Append("                    ").Append(csName).Append(".Add(reader.ReadInt64());").AppendLine();
                sb.AppendLine("                    break;");
                return;
            case FieldDescriptorProto.Types.Type.Message:
            {
                var tn = NormalizeTypeName(f.TypeName);
                var msgName = messageCSharpNameByFullName[tn];
                sb.AppendLine("                    {");
                sb.AppendLine("                        var bytes = reader.ReadBytes();");
                sb.AppendLine("                        var sub = new ProtoReader(bytes);");
                sb.Append("                        var msg = global::").Append(rootNamespace).Append('.').Append(msgName).Append(".Rent();").AppendLine();
                sb.AppendLine("                        msg.MergeFrom(ref sub);");
                sb.Append("                        ").Append(csName).Append(".Add(msg);").AppendLine();
                sb.AppendLine("                    }");
                sb.AppendLine("                    break;");
                return;
            }
            default:
                sb.AppendLine("                    reader.SkipField(wireType);");
                sb.AppendLine("                    break;");
                return;
        }
    }

    static string NormalizeTypeName(string typeName)
    {
        if (typeName.Length == 0)
        {
            return typeName;
        }
        if (typeName[0] == '.')
        {
            return typeName;
        }
        return "." + typeName;
    }

    static string ToPascal(string snake)
    {
        if (snake.Length == 0)
        {
            return snake;
        }

        // Fast path: already PascalCase
        if (snake.IndexOf('_') < 0)
        {
            var c0 = snake[0];
            if (c0 >= 'a' && c0 <= 'z')
            {
                return char.ToUpperInvariant(c0) + snake.Substring(1);
            }
            return snake;
        }

        var sb = new StringBuilder(snake.Length);
        var upper = true;
        for (int i = 0; i < snake.Length; i++)
        {
            var c = snake[i];
            if (c == '_')
            {
                upper = true;
                continue;
            }

            if (upper)
            {
                sb.Append(char.ToUpperInvariant(c));
                upper = false;
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    static string ToPascalEnumValue(string screamingSnake)
    {
        if (screamingSnake.Length == 0)
        {
            return screamingSnake;
        }

        // Similar to protoc C# style: underscores => word boundaries, remaining chars lowercased.
        var sb = new StringBuilder(screamingSnake.Length);
        var upper = true;
        for (int i = 0; i < screamingSnake.Length; i++)
        {
            var c = screamingSnake[i];
            if (c == '_')
            {
                upper = true;
                continue;
            }

            if (upper)
            {
                sb.Append(char.ToUpperInvariant(c));
                upper = false;
            }
            else
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    static string ToScreamingSnake(string pascal)
    {
        if (pascal.Length == 0)
        {
            return pascal;
        }

        var sb = new StringBuilder(pascal.Length + 8);
        for (int i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (c >= 'A' && c <= 'Z')
            {
                if (i != 0)
                {
                    var prev = pascal[i - 1];
                    if ((prev >= 'a' && prev <= 'z') || (prev >= '0' && prev <= '9'))
                    {
                        sb.Append('_');
                    }
                }

                sb.Append(c);
            }
            else
            {
                sb.Append(char.ToUpperInvariant(c));
            }
        }

        return sb.ToString();
    }

    static string SafeMemberName(string memberName, string containingTypeName)
    {
        // C# restriction: a member cannot have the same name as the containing type (it clashes with constructor).
        if (string.Equals(memberName, containingTypeName, StringComparison.Ordinal))
        {
            return memberName + "_";
        }

        return memberName;
    }

    static string MakeSafeFileName(string name)
    {
        // Minimal protection against "weird" names.
        return name.Replace('<', '_').Replace('>', '_').Replace('.', '_').Replace('/', '_').Replace('\\', '_');
    }
}


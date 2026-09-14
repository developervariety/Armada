namespace Armada.Helm.Commands
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Nodes;

    /// <summary>Edits one direct JSONC object member or array object while preserving other text.</summary>
    internal static class JsoncScopedEditor
    {
        internal static void Validate(string source)
        {
            ParseDocument(source);
            string withoutComments = RemoveComments(source);
            JsonNode? node = JsonNode.Parse(withoutComments, null, new JsonDocumentOptions { AllowTrailingCommas = true });
            if (node is not JsonObject)
                throw new JsonException("JSONC root must be an object.");
        }

        internal static string Upsert(string source, string containerName, string propertyName, string value, out bool changed)
        {
            if (String.IsNullOrWhiteSpace(source))
            {
                changed = true;
                return "{\n  \"" + containerName + "\": {\n    \"" + propertyName + "\": " + value + "\n  }\n}\n";
            }

            Document document = ParseDocument(source);
            MemberRange? containerMember = FindDirectMember(document.Root, containerName);
            if (containerMember == null)
            {
                string containerValue = "{\n" + ChildIndent(source, document.Root.Open) + "  \"" + propertyName + "\": " + value + "\n" + ParentIndent(source, document.Root.Open) + "}";
                return InsertObjectMember(source, document.Root, containerName, containerValue, out changed);
            }

            if (containerMember.ValueStart >= source.Length || source[containerMember.ValueStart] != '{')
                throw new JsonException("JSONC container '" + containerName + "' must be an object.");

            ObjectRange container = ParseObject(source, containerMember.ValueStart);
            MemberRange? managed = FindDirectMember(container, propertyName);
            if (managed == null)
                return InsertObjectMember(source, container, propertyName, value, out changed);

            changed = !String.Equals(source.Substring(managed.ValueStart, managed.ValueEnd - managed.ValueStart), value, StringComparison.Ordinal);
            return changed
                ? source.Substring(0, managed.ValueStart) + value + source.Substring(managed.ValueEnd)
                : source;
        }

        internal static string Remove(string source, string containerName, string propertyName, out bool changed)
        {
            if (String.IsNullOrWhiteSpace(source))
            {
                changed = false;
                return source;
            }

            Document document = ParseDocument(source);
            MemberRange? containerMember = FindDirectMember(document.Root, containerName);
            if (containerMember == null)
            {
                changed = false;
                return source;
            }

            if (containerMember.ValueStart >= source.Length || source[containerMember.ValueStart] != '{')
                throw new JsonException("JSONC container '" + containerName + "' must be an object.");

            ObjectRange container = ParseObject(source, containerMember.ValueStart);
            MemberRange? managed = FindDirectMember(container, propertyName);
            if (managed == null)
            {
                changed = false;
                return source;
            }

            changed = true;
            return RemoveMemberText(source, container, managed);
        }

        internal static string UpsertArrayObject(string source, string containerName, string objectName, string value, out bool changed)
        {
            if (String.IsNullOrWhiteSpace(source))
            {
                changed = true;
                return "{\n  \"" + containerName + "\": [\n    " + value + "\n  ]\n}\n";
            }

            Document document = ParseDocument(source);
            MemberRange? containerMember = FindDirectMember(document.Root, containerName);
            if (containerMember == null)
            {
                string containerValue = "[\n" + ChildIndent(source, document.Root.Open) + "  " + value + "\n" + ParentIndent(source, document.Root.Open) + "]";
                return InsertObjectMember(source, document.Root, containerName, containerValue, out changed);
            }

            if (containerMember.ValueStart >= source.Length || source[containerMember.ValueStart] != '[')
                throw new JsonException("JSONC container '" + containerName + "' must be an array.");

            ArrayRange array = ParseArray(source, containerMember.ValueStart);
            List<ArrayElementRange> matches = new List<ArrayElementRange>();
            foreach (ArrayElementRange element in array.Elements)
            {
                if (element.ValueStart >= source.Length || source[element.ValueStart] != '{')
                    continue;

                ObjectRange item = ParseObject(source, element.ValueStart);
                MemberRange? nameMember = FindDirectMember(item, "name");
                if (nameMember == null)
                    continue;

                string? name = ReadStringValue(source, nameMember.ValueStart, nameMember.ValueEnd);
                if (String.Equals(name, objectName, StringComparison.Ordinal))
                    matches.Add(element);
            }

            if (matches.Count > 1)
                throw new InvalidOperationException("JSONC array entry '" + objectName + "' is ambiguous.");

            if (matches.Count == 1)
            {
                ArrayElementRange match = matches[0];
                string existing = source.Substring(match.ValueStart, match.ValueEnd - match.ValueStart);
                changed = !String.Equals(existing, value, StringComparison.Ordinal);
                return changed
                    ? source.Substring(0, match.ValueStart) + value + source.Substring(match.ValueEnd)
                    : source;
            }

            return InsertArrayElement(source, array, value, out changed);
        }

        internal static string RemoveArrayObject(string source, string containerName, string objectName, out bool changed)
        {
            if (String.IsNullOrWhiteSpace(source))
            {
                changed = false;
                return source;
            }

            Document document = ParseDocument(source);
            MemberRange? containerMember = FindDirectMember(document.Root, containerName);
            if (containerMember == null)
            {
                changed = false;
                return source;
            }

            if (containerMember.ValueStart >= source.Length || source[containerMember.ValueStart] != '[')
                throw new JsonException("JSONC container '" + containerName + "' must be an array.");

            ArrayRange array = ParseArray(source, containerMember.ValueStart);
            List<ArrayElementRange> matches = new List<ArrayElementRange>();
            foreach (ArrayElementRange element in array.Elements)
            {
                if (element.ValueStart >= source.Length || source[element.ValueStart] != '{')
                    continue;

                ObjectRange item = ParseObject(source, element.ValueStart);
                MemberRange? nameMember = FindDirectMember(item, "name");
                if (nameMember == null)
                    continue;

                string? name = ReadStringValue(source, nameMember.ValueStart, nameMember.ValueEnd);
                if (String.Equals(name, objectName, StringComparison.Ordinal))
                    matches.Add(element);
            }

            if (matches.Count > 1)
                throw new InvalidOperationException("JSONC array entry '" + objectName + "' is ambiguous.");
            if (matches.Count == 0)
            {
                changed = false;
                return source;
            }

            changed = true;
            ArrayElementRange match = matches[0];
            StringBuilder result = new StringBuilder(source);
            int removeStart = LineStartIfIndented(source, match.ValueStart);
            int removeEnd = EndOfInsertedLine(source, match.ValueEnd);
            if (match.CommaIndex >= 0)
            {
                result.Remove(match.CommaIndex, 1);
            }
            result.Remove(removeStart, removeEnd - removeStart);
            return result.ToString();
        }

        private static string InsertObjectMember(string source, ObjectRange container, string key, string value, out bool changed)
        {
            MemberRange? existing = FindDirectMember(container, key);
            if (existing != null)
                throw new InvalidOperationException("JSONC object entry '" + key + "' is ambiguous.");

            MemberRange? last = container.Members.Count == 0 ? null : container.Members[container.Members.Count - 1];
            StringBuilder result = new StringBuilder(source);
            if (last != null && last.CommaIndex < 0)
                result.Insert(last.ValueEnd, ',');

            int close = container.Close;
            if (last != null && last.CommaIndex < 0)
                close++;

            string indent = last == null ? ChildIndent(source, container.Open) : LineIndent(source, last.Start);
            string insertion = BuildInsertion(source, close, indent, "\"" + key + "\": " + value);
            result.Insert(close, insertion);
            changed = true;
            return result.ToString();
        }

        private static string InsertArrayElement(string source, ArrayRange array, string value, out bool changed)
        {
            ArrayElementRange? last = array.Elements.Count == 0 ? null : array.Elements[array.Elements.Count - 1];
            StringBuilder result = new StringBuilder(source);
            if (last != null && last.CommaIndex < 0)
                result.Insert(last.ValueEnd, ',');

            int close = array.Close;
            if (last != null && last.CommaIndex < 0)
                close++;

            string indent = last == null ? ChildIndent(source, array.Open) : LineIndent(source, last.ValueStart);
            string insertion = BuildInsertion(source, close, indent, value);
            result.Insert(close, insertion);
            changed = true;
            return result.ToString();
        }

        private static string RemoveMemberText(string source, ObjectRange container, MemberRange member)
        {
            StringBuilder result = new StringBuilder(source);
            int removeStart = LineStartIfIndented(source, member.Start);
            int removeEnd = EndOfInsertedLine(source, member.ValueEnd);
            if (member.CommaIndex >= 0)
            {
                result.Remove(member.CommaIndex, 1);
            }
            // Keep a preceding separator as a valid trailing comma. It belongs to the
            // neighboring member and retaining it restores the original formatting.
            result.Remove(removeStart, removeEnd - removeStart);
            return result.ToString();
        }

        private static int LineStartIfIndented(string source, int position)
        {
            int lineStart = source.LastIndexOf('\n', Math.Max(0, position - 1));
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            for (int i = lineStart; i < position; i++)
            {
                if (!Char.IsWhiteSpace(source[i]))
                    return position;
            }
            return lineStart;
        }

        private static int EndOfInsertedLine(string source, int position)
        {
            if (position + 1 < source.Length && source[position] == '\r' && source[position + 1] == '\n')
                return position + 2;
            if (position < source.Length && source[position] == '\n')
                return position + 1;
            return position;
        }

        private static string BuildInsertion(string source, int close, string indent, string content)
        {
            int lineStart = source.LastIndexOf('\n', Math.Max(0, close - 1));
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            bool onlyWhitespace = true;
            for (int i = lineStart; i < close; i++)
            {
                if (!Char.IsWhiteSpace(source[i]))
                {
                    onlyWhitespace = false;
                    break;
                }
            }

            string newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            if (onlyWhitespace)
            {
                int existingIndentLength = close - lineStart;
                string prefix = existingIndentLength < indent.Length
                    ? indent.Substring(existingIndentLength)
                    : String.Empty;
                string closeIndent = source.Substring(lineStart, existingIndentLength);
                return prefix + content + newline + closeIndent;
            }
            return newline + indent + content + newline;
        }

        private static Document ParseDocument(string source)
        {
            int start = SkipTrivia(source, 0);
            if (start >= source.Length || source[start] != '{')
                throw new JsonException("JSONC root must be an object.");

            ObjectRange root = ParseObject(source, start);
            int after = SkipTrivia(source, root.Close + 1);
            if (after != source.Length)
                throw new JsonException("JSONC contains data after the root object.");
            return new Document(root);
        }

        private static ObjectRange ParseObject(string source, int open)
        {
            ObjectRange result = new ObjectRange(open, FindMatchingClose(source, open, '{', '}'));
            int cursor = SkipTrivia(source, open + 1);
            while (cursor < result.Close)
            {
                if (source[cursor] != '"')
                    throw new JsonException("JSONC object member must have a quoted name.");

                StringToken key = ReadString(source, cursor);
                int colon = SkipTrivia(source, key.End);
                if (colon >= result.Close || source[colon] != ':')
                    throw new JsonException("JSONC object member is missing a colon.");

                int valueStart = SkipTrivia(source, colon + 1);
                int valueEnd = ParseValue(source, valueStart, result.Close);
                MemberRange member = new MemberRange(key.Value, key.Start, valueStart, valueEnd);
                int separator = SkipTrivia(source, valueEnd);
                if (separator < result.Close && source[separator] == ',')
                {
                    member.CommaIndex = separator;
                    cursor = SkipTrivia(source, separator + 1);
                    result.Members.Add(member);
                    if (cursor >= result.Close)
                        break;
                }
                else
                {
                    result.Members.Add(member);
                    if (separator != result.Close)
                        throw new JsonException("JSONC object member is missing a comma.");
                    cursor = result.Close;
                }
            }
            return result;
        }

        private static ArrayRange ParseArray(string source, int open)
        {
            ArrayRange result = new ArrayRange(open, FindMatchingClose(source, open, '[', ']'));
            int cursor = SkipTrivia(source, open + 1);
            while (cursor < result.Close)
            {
                int valueStart = cursor;
                int valueEnd = ParseValue(source, valueStart, result.Close);
                ArrayElementRange element = new ArrayElementRange(valueStart, valueEnd);
                int separator = SkipTrivia(source, valueEnd);
                if (separator < result.Close && source[separator] == ',')
                {
                    element.CommaIndex = separator;
                    cursor = SkipTrivia(source, separator + 1);
                    result.Elements.Add(element);
                    if (cursor >= result.Close)
                        break;
                }
                else
                {
                    result.Elements.Add(element);
                    if (separator != result.Close)
                        throw new JsonException("JSONC array element is missing a comma.");
                    cursor = result.Close;
                }
            }
            return result;
        }

        private static int ParseValue(string source, int start, int limit)
        {
            if (start >= limit)
                throw new JsonException("JSONC value is missing.");
            if (source[start] == '"')
                return ReadString(source, start).End;
            if (source[start] == '{')
                return ParseObject(source, start).Close + 1;
            if (source[start] == '[')
                return ParseArray(source, start).Close + 1;

            int cursor = start;
            while (cursor < limit)
            {
                if (source[cursor] == ',' || source[cursor] == '}' || source[cursor] == ']')
                    break;
                if (source[cursor] == '/' && cursor + 1 < limit && (source[cursor + 1] == '/' || source[cursor + 1] == '*'))
                    break;
                if (Char.IsWhiteSpace(source[cursor]))
                    break;
                cursor++;
            }
            if (cursor == start)
                throw new JsonException("JSONC value is missing.");
            return cursor;
        }

        private static int FindMatchingClose(string source, int start, char open, char close)
        {
            int depth = 0;
            for (int i = start; i < source.Length; i++)
            {
                if (source[i] == '"')
                {
                    i = ReadString(source, i).End - 1;
                    continue;
                }
                if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
                {
                    int lineEnd = source.IndexOf('\n', i + 2);
                    i = lineEnd < 0 ? source.Length : lineEnd;
                    continue;
                }
                if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
                {
                    int commentEnd = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (commentEnd < 0)
                        throw new JsonException("JSONC block comment is unclosed.");
                    i = commentEnd + 1;
                    continue;
                }
                if (source[i] == open)
                    depth++;
                else if (source[i] == close)
                {
                    depth--;
                    if (depth == 0)
                        return i;
                }
            }
            throw new JsonException("JSONC container is unclosed.");
        }

        private static StringToken ReadString(string source, int start)
        {
            if (source[start] != '"')
                throw new JsonException("JSONC string is missing its opening quote.");
            StringBuilder value = new StringBuilder();
            for (int i = start + 1; i < source.Length; i++)
            {
                char c = source[i];
                if (c == '"')
                    return new StringToken(start, i + 1, value.ToString());
                if (c == '\\')
                {
                    if (++i >= source.Length)
                        throw new JsonException("JSONC string escape is unclosed.");
                    char escaped = source[i];
                    switch (escaped)
                    {
                        case '"': value.Append('"'); break;
                        case '\\': value.Append('\\'); break;
                        case '/': value.Append('/'); break;
                        case 'b': value.Append('\b'); break;
                        case 'f': value.Append('\f'); break;
                        case 'n': value.Append('\n'); break;
                        case 'r': value.Append('\r'); break;
                        case 't': value.Append('\t'); break;
                        case 'u':
                            if (i + 4 >= source.Length)
                                throw new JsonException("JSONC unicode escape is incomplete.");
                            int code = 0;
                            for (int digit = 1; digit <= 4; digit++)
                            {
                                int hex = HexValue(source[i + digit]);
                                if (hex < 0)
                                    throw new JsonException("JSONC unicode escape is invalid.");
                                code = (code * 16) + hex;
                            }
                            value.Append((char)code);
                            i += 4;
                            break;
                        default: throw new JsonException("JSONC string escape is invalid.");
                    }
                    continue;
                }
                value.Append(c);
            }
            throw new JsonException("JSONC string is unclosed.");
        }

        private static string? ReadStringValue(string source, int start, int end)
        {
            if (start >= end || source[start] != '"')
                return null;
            StringToken token = ReadString(source, start);
            return token.End == end ? token.Value : null;
        }

        private static int SkipTrivia(string source, int start)
        {
            int cursor = start;
            while (cursor < source.Length)
            {
                if (Char.IsWhiteSpace(source[cursor]))
                {
                    cursor++;
                    continue;
                }
                if (source[cursor] == '/' && cursor + 1 < source.Length && source[cursor + 1] == '/')
                {
                    int lineEnd = source.IndexOf('\n', cursor + 2);
                    cursor = lineEnd < 0 ? source.Length : lineEnd + 1;
                    continue;
                }
                if (source[cursor] == '/' && cursor + 1 < source.Length && source[cursor + 1] == '*')
                {
                    int commentEnd = source.IndexOf("*/", cursor + 2, StringComparison.Ordinal);
                    if (commentEnd < 0)
                        throw new JsonException("JSONC block comment is unclosed.");
                    cursor = commentEnd + 2;
                    continue;
                }
                break;
            }
            return cursor;
        }

        private static MemberRange? FindDirectMember(ObjectRange container, string key)
        {
            MemberRange? found = null;
            foreach (MemberRange member in container.Members)
            {
                if (!String.Equals(member.Key, key, StringComparison.Ordinal))
                    continue;
                if (found != null)
                    throw new InvalidOperationException("JSONC object entry '" + key + "' is ambiguous.");
                found = member;
            }
            return found;
        }

        private static MemberRange? PreviousMember(ObjectRange container, MemberRange current)
        {
            for (int i = 1; i < container.Members.Count; i++)
            {
                if (Object.ReferenceEquals(container.Members[i], current))
                    return container.Members[i - 1];
            }
            return null;
        }

        private static ArrayElementRange? PreviousElement(ArrayRange array, ArrayElementRange current)
        {
            for (int i = 1; i < array.Elements.Count; i++)
            {
                if (Object.ReferenceEquals(array.Elements[i], current))
                    return array.Elements[i - 1];
            }
            return null;
        }

        private static string ParentIndent(string source, int open)
        {
            int lineStart = source.LastIndexOf('\n', Math.Max(0, open - 1));
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            int cursor = lineStart;
            while (cursor < open && (source[cursor] == ' ' || source[cursor] == '\t')) cursor++;
            return source.Substring(lineStart, cursor - lineStart);
        }

        private static string ChildIndent(string source, int open)
        {
            return ParentIndent(source, open) + "  ";
        }

        private static string LineIndent(string source, int position)
        {
            int lineStart = source.LastIndexOf('\n', Math.Max(0, position - 1));
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            int cursor = lineStart;
            while (cursor < position && (source[cursor] == ' ' || source[cursor] == '\t')) cursor++;
            return source.Substring(lineStart, cursor - lineStart);
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        private static string RemoveComments(string source)
        {
            StringBuilder result = new StringBuilder(source.Length);
            for (int i = 0; i < source.Length; i++)
            {
                if (source[i] == '"')
                {
                    StringToken token = ReadString(source, i);
                    result.Append(source, i, token.End - i);
                    i = token.End - 1;
                    continue;
                }
                if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
                {
                    int lineEnd = source.IndexOf('\n', i + 2);
                    if (lineEnd < 0)
                        break;
                    result.Append('\n');
                    i = lineEnd;
                    continue;
                }
                if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
                {
                    int commentEnd = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (commentEnd < 0)
                        throw new JsonException("JSONC block comment is unclosed.");
                    for (int line = i; line <= commentEnd + 1; line++)
                    {
                        if (source[line] == '\n')
                            result.Append('\n');
                    }
                    i = commentEnd + 1;
                    continue;
                }
                result.Append(source[i]);
            }
            return result.ToString();
        }

        private sealed class Document
        {
            internal Document(ObjectRange root) { Root = root; }
            internal ObjectRange Root { get; }
        }

        private sealed class ObjectRange
        {
            internal ObjectRange(int open, int close) { Open = open; Close = close; }
            internal int Open { get; }
            internal int Close { get; }
            internal List<MemberRange> Members { get; } = new List<MemberRange>();
        }

        private sealed class ArrayRange
        {
            internal ArrayRange(int open, int close) { Open = open; Close = close; }
            internal int Open { get; }
            internal int Close { get; }
            internal List<ArrayElementRange> Elements { get; } = new List<ArrayElementRange>();
        }

        private sealed class MemberRange
        {
            internal MemberRange(string key, int start, int valueStart, int valueEnd)
            {
                Key = key;
                Start = start;
                ValueStart = valueStart;
                ValueEnd = valueEnd;
                CommaIndex = -1;
            }
            internal string Key { get; }
            internal int Start { get; }
            internal int ValueStart { get; }
            internal int ValueEnd { get; }
            internal int CommaIndex { get; set; }
        }

        private sealed class ArrayElementRange
        {
            internal ArrayElementRange(int valueStart, int valueEnd)
            {
                ValueStart = valueStart;
                ValueEnd = valueEnd;
                CommaIndex = -1;
            }
            internal int ValueStart { get; }
            internal int ValueEnd { get; }
            internal int CommaIndex { get; set; }
        }

        private sealed class StringToken
        {
            internal StringToken(int start, int end, string value)
            {
                Start = start;
                End = end;
                Value = value;
            }
            internal int Start { get; }
            internal int End { get; }
            internal string Value { get; }
        }
    }
}
